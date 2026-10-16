#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Plugins.Stablecoins.Thorchain;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Intents;

/// <summary>What the checkout shows: send exactly <see cref="Amount"/> of the coin on <see cref="Network"/> to <see cref="Address"/>.</summary>
public record IntentsPayment(
    string Network,
    decimal Amount,
    BigInteger AmountUnits,
    int Decimals,
    string Address,
    string? Memo,
    DateTimeOffset? Deadline,
    long TargetSats,
    long? TimeEstimateSeconds,
    string Status,
    BigInteger DepositedUnits,
    DateTimeOffset CheckedAt);

public enum IntentsState { ChooseNetwork, Preparing, Ready, Incomplete, Received, Refunded, Failed }

public record IntentsStatus(IntentsState State, string? Network, IntentsPayment? Payment, string? Error)
{
    /// <summary>What is still to send after a partial deposit, in the coin.</summary>
    public decimal? Remaining => Payment is { } p && State == IntentsState.Incomplete
        ? Math.Max(0m, IntentsDepositService.ToDecimal(p.AmountUnits - p.DepositedUnits, p.Decimals))
        : null;
}

/// <summary>
/// Prepares a NEAR Intents deposit for an invoice once the customer picks a network: asks 1Click for an
/// exact-output quote (the BTC due, to this invoice's address) and shows its one-time deposit address and amount.
/// Then follows the deposit through 1Click's status endpoint. Work runs in the background, like the THORChain
/// deposits, so a page load never waits on the API.
/// </summary>
public class IntentsDepositService(OneClickClient client, InvoiceRepository invoiceRepository, ILogger<IntentsDepositService> logger)
{
    static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(20);
    static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TokensFor = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);
    /// <summary>NEAR Intents refunds a deposit that has not been swapped by the deadline; leave room past the invoice's expiry.</summary>
    static readonly TimeSpan DeadlineMargin = TimeSpan.FromMinutes(30);

    class Entry
    {
        public string? Network;
        public long Target;
        public IntentsPayment? Payment;
        public string? Error;
        public DateTimeOffset ErrorAt;
        public Task? Running;
        public DateTimeOffset Touched;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();
    /// <summary>The exchange option (USDC, USDT) each invoice's checkout shows instead of the wallet panel.</summary>
    readonly ConcurrentDictionary<string, (string Code, DateTimeOffset At)> _chosen = new();
    readonly ConcurrentDictionary<string, (DateTimeOffset At, List<OneClickToken> Tokens)> _tokens = new();

    /// <summary>Settable so tests do not wait.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public static Uri BaseUrl(SwapToBtcPaymentMethodConfig config) => ThorQuoteService.SafeUrl(config.IntentsUrl, OneClickClient.DefaultUrl);

    /// <summary>The networks this store can take the coin on: those with a refund address.</summary>
    public static IEnumerable<IntentsNetwork> Networks(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config) =>
        chain.Networks.Where(n => !string.IsNullOrWhiteSpace(config.RefundAddress(n.Family)));

    /// <summary>Used by tests to wait for background work.</summary>
    public Task Pending(string invoiceId, SwapSourceChain chain) =>
        _entries.TryGetValue(Key(invoiceId, chain), out var e) && e.Running is { } t ? t : Task.CompletedTask;

    /// <summary>The customer tapped "Send USDC from an exchange" (or went back to the wallet panel, with null).</summary>
    public void Choose(string invoiceId, SwapSourceChain? option)
    {
        if (option is null)
            _chosen.TryRemove(invoiceId, out _);
        else
            _chosen[invoiceId] = (option.Code, Now());
    }

    public SwapSourceChain? Chosen(string invoiceId) =>
        _chosen.TryGetValue(invoiceId, out var c) ? SwapSourceChain.ExchangeOptions.FirstOrDefault(o => o.Code == c.Code) : null;

    /// <summary>The customer picked a network. A new pick (or a retry after a refund) starts a new quote.</summary>
    public bool Select(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string invoiceId, string network)
    {
        if (!Networks(chain, config).Any(n => n.Code == network))
            return false;
        var entry = _entries.GetOrAdd(Key(invoiceId, chain), _ => new Entry());
        lock (entry)
        {
            entry.Touched = Now();
            var settled = entry.Payment?.Status is "REFUNDED" or "FAILED";
            if (entry.Network == network && !settled && entry.Error is null)
                return true;
            // Never drop a deposit that is already on its way.
            if (entry.Payment is { Status: "KNOWN_DEPOSIT_TX" or "PROCESSING" or "INCOMPLETE_DEPOSIT" })
                return entry.Network == network;
            entry.Network = network;
            entry.Payment = null;
            entry.Error = null;
        }
        return true;
    }

    public IntentsStatus Get(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string invoiceId, string btcAddress, decimal btcDue,
        DateTimeOffset invoiceExpiry)
    {
        var target = ThorQuoteService.TargetUnits(btcDue, config.BufferPercent);
        var now = Now();
        var entry = _entries.GetOrAdd(Key(invoiceId, chain), _ => new Entry());
        IntentsStatus result;
        lock (entry)
        {
            entry.Touched = now;
            if (entry.Network is not { } network)
                return new(IntentsState.ChooseNetwork, null, null, null);
            var payment = entry.Payment;
            // A deposit on its way stays on screen even if the amount due changed meanwhile.
            var inFlight = payment?.Status is "KNOWN_DEPOSIT_TX" or "PROCESSING" or "INCOMPLETE_DEPOSIT";
            var current = payment is not null && (entry.Target == target || inFlight || payment.Status is "REFUNDED" or "FAILED");
            if (entry.Running is { IsCompleted: false })
                return current ? StatusOf(network, payment!) : new(IntentsState.Preparing, network, null, null);
            if (current)
            {
                if (payment!.Status is not ("SUCCESS" or "REFUNDED" or "FAILED") && now - payment.CheckedAt > RecheckEvery)
                    entry.Running = Task.Run(() => Recheck(entry, invoiceId, chain, config, payment));
                return StatusOf(network, payment);
            }
            if (entry.Error is not null && entry.Target == target && now - entry.ErrorAt < RetryFailedAfter)
                return new(IntentsState.Failed, network, null, entry.Error);
            entry.Target = target;
            entry.Payment = null;
            entry.Error = null;
            entry.Running = Task.Run(() => Prepare(entry, invoiceId, chain, config, network, btcAddress, target, invoiceExpiry));
            result = new(IntentsState.Preparing, network, null, null);
        }
        Forget(now);
        return result;
    }

    static IntentsStatus StatusOf(string network, IntentsPayment payment) => payment.Status switch
    {
        "KNOWN_DEPOSIT_TX" or "PROCESSING" or "SUCCESS" => new(IntentsState.Received, network, payment, null),
        "INCOMPLETE_DEPOSIT" => new(IntentsState.Incomplete, network, payment, null),
        "REFUNDED" or "FAILED" => new(IntentsState.Refunded, network, payment, null),
        _ => new(IntentsState.Ready, network, payment, null)
    };

    async Task Prepare(Entry entry, string invoiceId, SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, string networkCode,
        string btcAddress, long target, DateTimeOffset invoiceExpiry)
    {
        var network = chain.Networks.First(n => n.Code == networkCode);
        try
        {
            var baseUrl = BaseUrl(config);
            var tokens = await Tokens(baseUrl, config.IntentsApiKey);
            var origin = Single(tokens, network.Code, chain.Ticker!) ??
                         throw new OneClickException(null, $"NEAR Intents lists no single {chain.Ticker} on {network.Code}");
            var btc = Single(tokens, "btc", "BTC") ?? throw new OneClickException(null, "NEAR Intents lists no BTC");
            var now = Now();
            var deadline = (invoiceExpiry > now.AddMinutes(30) ? invoiceExpiry : now.AddMinutes(30)) + DeadlineMargin;
            var response = await client.Quote(baseUrl, config.IntentsApiKey, new OneClickQuoteRequest
            {
                Dry = false,
                SwapType = "EXACT_OUTPUT",
                SlippageTolerance = Math.Clamp(config.IntentsSlippageBps, 1, 1000),
                OriginAsset = origin.AssetId,
                DestinationAsset = btc.AssetId,
                Amount = target.ToString(CultureInfo.InvariantCulture),
                RefundTo = config.RefundAddress(network.Family)!.Trim(),
                Recipient = btcAddress,
                Deadline = deadline.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            }, CancellationToken.None);
            var quote = response.Quote;
            if (string.IsNullOrEmpty(quote.DepositAddress))
                throw new OneClickException(null, "The quote has no deposit address");
            if (!BigInteger.TryParse(quote.AmountIn, NumberStyles.None, CultureInfo.InvariantCulture, out var amountIn) || amountIn <= 0)
                throw new OneClickException(null, $"The quote has no amount ({quote.AmountIn})");
            // An exact-output quote must still pay at least what is due to this invoice's address.
            if (!long.TryParse(quote.AmountOut, NumberStyles.None, CultureInfo.InvariantCulture, out var amountOut) || amountOut < target)
                throw new OneClickException(null, $"The quote pays {quote.AmountOut} satoshis, less than the {target} due");

            var payment = new IntentsPayment(network.Code, ToDecimal(amountIn, origin.Decimals), amountIn, origin.Decimals, quote.DepositAddress,
                quote.DepositMemo, quote.Deadline, target, quote.TimeEstimate, "PENDING_DEPOSIT", BigInteger.Zero, now);
            lock (entry)
            {
                if (entry.Network == networkCode)
                    entry.Payment = payment;
            }
            await Log(invoiceId,
                $"{chain.Ticker} on {network.Name}: send {payment.Amount} {chain.Ticker} to {payment.Address}" +
                (payment.Memo is null ? "" : $" with memo {payment.Memo}") +
                $". NEAR Intents quote {response.CorrelationId}: {amountOut} satoshis to {btcAddress}, deposit deadline {quote.Deadline:u}. " +
                $"Unswapped or excess {chain.Ticker} is refunded to {config.RefundAddress(network.Family)}. Quote signature: {response.Signature}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare a {Coin} payment on {Network} for {Address}", chain.Ticker, networkCode, btcAddress);
            lock (entry)
            {
                entry.Error = "This network is unavailable right now. Please try again shortly or choose another network.";
                entry.ErrorAt = Now();
            }
        }
    }

    async Task Recheck(Entry entry, string invoiceId, SwapSourceChain chain, SwapToBtcPaymentMethodConfig config, IntentsPayment payment)
    {
        var updated = payment with { CheckedAt = Now() };
        try
        {
            var status = await client.Status(BaseUrl(config), config.IntentsApiKey, payment.Address, payment.Memo, CancellationToken.None);
            var deposited = BigInteger.TryParse(status.SwapDetails?.DepositedAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : payment.DepositedUnits;
            updated = updated with { Status = string.IsNullOrEmpty(status.Status) ? payment.Status : status.Status, DepositedUnits = deposited };
            if (updated.Status != payment.Status)
                await Log(invoiceId, $"{chain.Ticker} deposit to {payment.Address}: {updated.Status}" +
                                     (status.SwapDetails?.RefundReason is { } reason ? $" ({reason})" : ""));
        }
        catch (OneClickException ex) when (ex.Status == 404)
        {
            // Not seen yet.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the {Coin} deposit {Address}", chain.Ticker, payment.Address);
        }
        lock (entry)
        {
            if (ReferenceEquals(entry.Payment, payment))
                entry.Payment = updated;
        }
    }

    async Task<List<OneClickToken>> Tokens(Uri baseUrl, string? apiKey)
    {
        var key = baseUrl.ToString();
        if (_tokens.TryGetValue(key, out var cached) && Now() - cached.At < TokensFor)
            return cached.Tokens;
        var tokens = await client.GetTokens(baseUrl, apiKey, CancellationToken.None);
        _tokens[key] = (Now(), tokens);
        return tokens;
    }

    /// <summary>Only an unambiguous match: a bridged look-alike (USDC.e) has another symbol, and two of the same name are refused.</summary>
    public static OneClickToken? Single(IEnumerable<OneClickToken> tokens, string blockchain, string symbol)
    {
        var matches = tokens.Where(t => t.Blockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase) &&
                                        t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static decimal ToDecimal(BigInteger units, int decimals)
    {
        // Keep at most 8 decimals so the value fits a decimal; amounts are rounded up for display anyway.
        var drop = Math.Max(0, decimals - 8);
        var scaled = BigInteger.Divide(units + BigInteger.Pow(10, drop) - 1, BigInteger.Pow(10, drop));
        var places = decimals - drop;
        var value = (decimal)scaled;
        for (var i = 0; i < places; i++) value /= 10m;
        return value;
    }

    async Task Log(string invoiceId, string message)
    {
        try
        {
            var logs = new InvoiceLogs();
            logs.Write(message, InvoiceEventData.EventSeverity.Info);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not log to invoice {InvoiceId}", invoiceId);
        }
    }

    static string Key(string invoiceId, SwapSourceChain chain) => $"{invoiceId}:{chain.Code}";

    void Forget(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(e => now - e.Value.Touched > ForgetAfter).Select(e => e.Key).ToList())
            _entries.TryRemove(key, out _);
        foreach (var key in _chosen.Where(e => now - e.Value.At > ForgetAfter).Select(e => e.Key).ToList())
            _chosen.TryRemove(key, out _);
    }
}
