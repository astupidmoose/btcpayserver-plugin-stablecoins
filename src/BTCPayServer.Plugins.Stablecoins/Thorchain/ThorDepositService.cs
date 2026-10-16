#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Thorchain;

/// <summary>What the checkout shows: send exactly <see cref="Amount"/> to <see cref="Address"/>. No memo.</summary>
public record ThorPayment(
    decimal Amount,
    string Address,
    DateTimeOffset Expiry,
    string RegistrationId,
    string Memo,
    long TargetBtcUnits,
    long ExpectedBtcOutUnits,
    long? TotalSwapSeconds,
    bool Used,
    DateTimeOffset CheckedAt);

public enum ThorPaymentState { Preparing, Ready, Failed }

public record ThorPaymentStatus(ThorPaymentState State, ThorPayment? Payment, string? Error);

/// <summary>
/// Prepares a memoless THORChain deposit for an invoice: quotes the coin amount that yields the BTC due,
/// registers the swap memo with THORChain's memoless API, and reads back the vault address once the
/// registration is on chain. The amount ends in the registration's reference number, which is what
/// lets THORChain match the deposit without a memo.
/// Work runs in the background and the checkout reads the current state, so a page load never waits on THORChain.
/// </summary>
public class ThorDepositService(ThorQuoteService quotes, MemolessClient memoless, InvoiceRepository invoiceRepository, ILogger<ThorDepositService> logger)
{
    static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);

    class Entry
    {
        public long Target;
        public ThorPayment? Payment;
        public string? Error;
        public DateTimeOffset ErrorAt;
        public Task? Running;
        public DateTimeOffset Touched;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Used by tests to replace the current payment.</summary>
    public void Seed(string invoiceId, SwapSourceChain chain, ThorPayment payment)
    {
        var entry = _entries.GetOrAdd(Key(invoiceId, chain), _ => new Entry());
        lock (entry)
            entry.Payment = payment;
    }

    /// <summary>Used by tests to wait for background work.</summary>
    public Task Pending(string invoiceId, SwapSourceChain chain) =>
        _entries.TryGetValue(Key(invoiceId, chain), out var e) && e.Running is { } t ? t : Task.CompletedTask;

    public ThorPaymentStatus Get(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string invoiceId, string btcAddress, decimal btcDue)
    {
        var target = ThorQuoteService.TargetUnits(btcDue, config.BufferPercent);
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.GetOrAdd(Key(invoiceId, chain), _ => new Entry());
        lock (entry)
        {
            entry.Touched = now;
            var payment = entry.Payment;
            var current = payment is not null && entry.Target == target && payment.Expiry - ExpiryMargin > now;
            if (entry.Running is { IsCompleted: false })
                return current ? new(ThorPaymentState.Ready, payment, null) : new(ThorPaymentState.Preparing, null, null);
            if (current)
            {
                if (now - payment!.CheckedAt > RecheckEvery)
                    entry.Running = Task.Run(() => Recheck(entry, invoiceId, chain, config, btcAddress, btcDue, payment));
                return new(ThorPaymentState.Ready, payment, null);
            }
            if (entry.Error is not null && entry.Target == target && now - entry.ErrorAt < RetryFailedAfter)
                return new(ThorPaymentState.Failed, null, entry.Error);
            entry.Target = target;
            entry.Payment = null;
            entry.Error = null;
            entry.Running = Task.Run(() => Prepare(entry, invoiceId, chain, config, btcAddress, btcDue));
        }
        Forget(now);
        return new(ThorPaymentState.Preparing, null, null);
    }

    async Task Prepare(Entry entry, string invoiceId, SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string btcAddress, decimal btcDue)
    {
        try
        {
            var payment = await Register(chain, config, btcAddress, btcDue, CancellationToken.None);
            lock (entry)
                entry.Payment = payment;
            await Log(invoiceId, chain, payment);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare a {Chain} payment for {Address}", chain.Name, btcAddress);
            lock (entry)
            {
                entry.Error = Friendly(ex);
                entry.ErrorAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>Refreshes the vault address and whether the deposit was already made; re-registers if prices moved against the amount.</summary>
    async Task Recheck(Entry entry, string invoiceId, SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string btcAddress, decimal btcDue, ThorPayment payment)
    {
        try
        {
            var preflight = await PreflightWithRetry(ThorQuoteService.MemolessUrl(config), chain, payment.RegistrationId, payment.Amount, 1, CancellationToken.None);
            var used = preflight.CurrentUses >= Math.Max(1, preflight.MaxUses);
            var refreshed = payment with
            {
                Address = preflight.InboundAddress ?? payment.Address,
                Used = used,
                Expiry = preflight.SecondsRemaining is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : payment.Expiry,
                CheckedAt = DateTimeOffset.UtcNow
            };
            if (!used)
            {
                var quote = await quotes.GetDeposit(chain, config, btcAddress, btcDue, CancellationToken.None);
                if (quote.Amount > payment.Amount)
                {
                    logger.LogInformation("{Chain} price moved; asking {New} instead of {Old}", chain.Name, quote.Amount, payment.Amount);
                    refreshed = await Register(chain, config, btcAddress, btcDue, CancellationToken.None);
                    await Log(invoiceId, chain, refreshed);
                }
            }
            lock (entry)
                entry.Payment = refreshed;
        }
        catch (MemolessException ex) when (IsUsed(ex))
        {
            lock (entry)
                entry.Payment = payment with { Used = true, CheckedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex)
        {
            // Keep showing what the customer already has; try again on a later load.
            logger.LogWarning(ex, "Could not refresh the {Chain} payment for {Address}", chain.Name, btcAddress);
            lock (entry)
                entry.Payment = payment with { CheckedAt = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>For support: what the customer was asked to send, and how THORChain ties it to this invoice.</summary>
    async Task Log(string invoiceId, SwapSourceChain chain, ThorPayment payment)
    {
        try
        {
            var logs = new InvoiceLogs();
            logs.Write($"{chain.Name} payment: send {payment.Amount} {chain.Ticker} to {payment.Address} before {payment.Expiry:u}. " +
                $"THORChain memoless registration {payment.RegistrationId} swaps it with memo {payment.Memo}; " +
                $"expected {payment.ExpectedBtcOutUnits / ThornodeClient.Units} BTC. Track the customer's deposit at https://runescan.io by its transaction ID.",
                InvoiceEventData.EventSeverity.Info);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not log the {Chain} payment of invoice {InvoiceId}", chain.Name, invoiceId);
        }
    }

    async Task<ThorPayment> Register(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string btcAddress, decimal btcDue,
        CancellationToken cancellationToken)
    {
        var quote = await quotes.GetDeposit(chain, config, btcAddress, btcDue, cancellationToken);
        var baseUrl = ThorQuoteService.MemolessUrl(config);
        var registration = await memoless.Register(baseUrl, quote.Asset, quote.Memo, quote.Amount, chain.Decimals, cancellationToken);
        if (registration.Id is null || registration.Reference is null)
            throw new ThornodeException("The memoless API did not return a reference");
        // The API may append its own affiliate, but the swap must still pay this invoice's address.
        if (registration.Memo is { } registered && !registered.Contains(btcAddress, StringComparison.Ordinal))
            throw new ThornodeException("The memoless API registered a memo for a different destination");
        if (registration.Memo is { } m && m != quote.Memo)
            logger.LogInformation("Memoless API registered {Registered} for requested {Requested}", m, quote.Memo);

        var amount = AmountWithReference(quote.Amount, registration.Reference, chain.Decimals, registration.ReferenceLength);
        if (registration.SuggestedAmount is { } suggested && suggested >= quote.Amount &&
            HasReference(suggested, registration.Reference, chain.Decimals, registration.ReferenceLength))
            amount = suggested;
        if (registration.MinimumAmountToSend is { } min && amount < min)
            amount = AmountWithReference(min, registration.Reference, chain.Decimals, registration.ReferenceLength);

        // The deposit must land after the registration's block, and the vault address comes from preflight.
        var preflight = await PreflightWithRetry(baseUrl, chain, registration.Id, amount, 8, cancellationToken);
        if (preflight.InboundAddress is null)
            throw new ThornodeException("The memoless API returned no deposit address");
        return new ThorPayment(
            amount,
            preflight.InboundAddress,
            DateTimeOffset.UtcNow.AddSeconds(preflight.SecondsRemaining ?? 3600),
            registration.Id,
            registration.Memo ?? quote.Memo,
            ThorQuoteService.TargetUnits(btcDue, config.BufferPercent),
            quote.ExpectedBtcOutUnits,
            quote.TotalSwapSeconds,
            false,
            DateTimeOffset.UtcNow);
    }

    async Task<MemolessPreflight> PreflightWithRetry(Uri baseUrl, SwapSourceChain chain, string id, decimal amount, int attempts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await memoless.Preflight(baseUrl, id, amount, chain.Decimals, cancellationToken);
            }
            catch (MemolessException ex) when (attempt < attempts && !IsUsed(ex))
            {
                // Usually the registration is not in a block yet; THORChain blocks are about 6 seconds.
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>Settable so tests do not wait.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(4);

    static bool IsUsed(MemolessException ex) =>
        ex.Message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("max use", StringComparison.OrdinalIgnoreCase);

    static string Friendly(Exception ex) => ex switch
    {
        MemolessException { Code: "RATE_LIMITED" } => "Payments in this coin are busy right now. Please try again in a few minutes.",
        ThornodeException t when t.Message.Contains("paused", StringComparison.OrdinalIgnoreCase) => "Payments in this coin are paused right now. Please choose another way to pay.",
        _ => "This payment option is unavailable right now. Please try again shortly or choose another way to pay."
    };

    /// <summary>
    /// The smallest amount at or above <paramref name="atLeast"/> whose last <paramref name="referenceLength"/> digits,
    /// ending at the coin's last decimal (at most 8), are the reference. Deeper digits are dropped.
    /// </summary>
    public static decimal AmountWithReference(decimal atLeast, string reference, int decimals, int referenceLength = 5)
    {
        var places = Math.Min(decimals, 8);
        var scale = Pow10(places);
        var modulus = (long)Pow10(referenceLength);
        var units = (long)decimal.Ceiling(atLeast * scale);
        var refValue = long.Parse(reference, System.Globalization.CultureInfo.InvariantCulture) % modulus;
        var candidate = units - units % modulus + refValue;
        if (candidate < units)
            candidate += modulus;
        return candidate / scale;
    }

    public static bool HasReference(decimal amount, string reference, int decimals, int referenceLength = 5)
    {
        var scale = Pow10(Math.Min(decimals, 8));
        var units = (long)decimal.Truncate(amount * scale);
        var modulus = (long)Pow10(referenceLength);
        return units % modulus == long.Parse(reference, System.Globalization.CultureInfo.InvariantCulture) % modulus;
    }

    static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }

    static string Key(string invoiceId, SwapSourceChain chain) => $"{invoiceId}:{chain.Code}";

    void Forget(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(e => now - e.Value.Touched > ForgetAfter).Select(e => e.Key).ToList())
            _entries.TryRemove(key, out _);
    }
}
