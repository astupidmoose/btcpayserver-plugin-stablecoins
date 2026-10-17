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
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
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

/// <summary>A quote as kept on the invoice (prompt details), so tracking survives a restart.</summary>
public class PersistedQuote
{
    public string Option { get; set; } = "";
    public string Network { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Memo { get; set; }
    // BTCPay's serializer writes dates as unix seconds; this reads them back.
    [Newtonsoft.Json.JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset? Deadline { get; set; }
    public long TargetSats { get; set; }
    public string AmountUnits { get; set; } = "0";
    public int Decimals { get; set; }
    public long? TimeEstimateSeconds { get; set; }
    public string Status { get; set; } = "PENDING_DEPOSIT";
    public string DepositedUnits { get; set; } = "0";
    public string[] LoggedTxs { get; set; } = [];
}

/// <summary>
/// Prepares NEAR Intents deposits for an invoice on a token checkout's own chain: asks 1Click for an exact-output quote
/// (the BTC due, to this invoice's address) and shows its one-time deposit address and amount.
/// Every quote issued for an invoice is kept, saved on the invoice, and followed in the background until it is settled or
/// past its deadline, whether or not anyone has the checkout open, and across restarts (<see cref="IntentsTracker"/>).
/// </summary>
public class IntentsDepositService(OneClickClient client, InvoiceRepository invoiceRepository, ILogger<IntentsDepositService> logger,
    SwapSecrets? secrets = null)
{
    static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(20);
    static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TokensFor = TimeSpan.FromMinutes(15);
    /// <summary>A quote is followed this long past its deadline: NEAR Intents refunds late deposits, and the refund is worth logging.</summary>
    static readonly TimeSpan FollowAfterDeadline = TimeSpan.FromHours(2);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);
    /// <summary>NEAR Intents refunds a deposit that has not been swapped by the deadline; leave room past the invoice's expiry.</summary>
    static readonly TimeSpan DeadlineMargin = TimeSpan.FromMinutes(30);
    /// <summary>Quotes kept per invoice and option: one per amount due (a partial payment asks a new one). Bounds what callers can make it ask.</summary>
    const int MaxQuotesPerEntry = 6;
    /// <summary>Environment variable that points 1Click somewhere else (a local test double). Not a store setting.</summary>
    public const string UrlVariable = "BTCPAY_STABLECOINS_ONECLICK_URL";

    static readonly Newtonsoft.Json.JsonSerializer DetailsSerializer = BlobSerializer.CreateSerializer().Serializer;

    class Quote
    {
        public required IntentsPayment Payment;
        public readonly HashSet<string> LoggedTxs = new(StringComparer.OrdinalIgnoreCase);
        public bool Final => Payment.Status is "SUCCESS" or "REFUNDED" or "FAILED";
    }

    class Entry
    {
        public required string InvoiceId;
        public required string StoreId;
        public required PaymentMethodId PaymentMethodId;
        public required SwapSourceChain Option;
        public required string Network;
        public SwapToBtcPaymentMethodConfig Config = new();
        public readonly List<Quote> Quotes = new();
        public long Target;
        public string? Error;
        public DateTimeOffset ErrorAt;
        public string? LoggedError;
        public Task? Running;
        public DateTimeOffset Touched;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _saving = new();
    readonly ConcurrentDictionary<string, (DateTimeOffset At, List<OneClickToken> Tokens)> _tokens = new();
    /// <summary>
    /// Minimum swap NEAR Intents asked for on each network ("Temporary swap limits: minimum swap amount is $100"), in USD.
    /// Smaller invoices do not get the option on that network for a while, instead of showing an error.
    /// </summary>
    readonly ConcurrentDictionary<string, (decimal Usd, DateTimeOffset At)> _minimums = new();
    static readonly TimeSpan MinimumFor = TimeSpan.FromMinutes(30);
    static readonly System.Text.RegularExpressions.Regex MinimumPattern =
        new(@"minimum swap amount is \$\s*([\d,]+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static decimal? ParseMinimumUsd(string? message) =>
        message is not null && MinimumPattern.Match(message) is { Success: true } m &&
        decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var usd) ? usd : null;

    /// <summary>True when NEAR Intents recently refused swaps this small on the network.</summary>
    public bool BelowMinimum(string? network, decimal btcDue)
    {
        if (network is null || !_minimums.TryGetValue(network, out var min) || Now() - min.At > MinimumFor)
            return false;
        var btcUsd = _tokens.Values.SelectMany(t => t.Tokens).FirstOrDefault(t => t.Blockchain == "btc" && t.Symbol == "BTC")?.Price;
        // Without a price, keep offering it: the quote then says whether it is still too small.
        return btcUsd is > 0m && btcDue * btcUsd.Value < min.Usd;
    }

    /// <summary>Settable so tests do not wait.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Settable by tests; otherwise NEAR Intents' own endpoint, or the environment variable's.</summary>
    public Uri BaseUrl { get; set; } = SafeUrl(Environment.GetEnvironmentVariable(UrlVariable), OneClickClient.DefaultUrl);

    /// <summary>https only, except plain http to this machine (a local node or a test double).</summary>
    public static Uri SafeUrl(string? url, string fallback) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)
            ? u
            : new Uri(fallback);

    /// <summary>The BTC due plus the buffer, in satoshis, rounded up.</summary>
    public static long TargetUnits(decimal btcDue, decimal bufferPercent) =>
        (long)decimal.Ceiling(btcDue * (1m + bufferPercent / 100m) * 100_000_000m);

    /// <summary>The networks this store can take the coin on: those with a refund address.</summary>
    public static IEnumerable<IntentsNetwork> Networks(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config) =>
        chain.Networks.Where(n => !string.IsNullOrWhiteSpace(config.RefundAddress(n.Family)));

    /// <summary>Used by tests to wait for background work.</summary>
    public Task Pending(string invoiceId, SwapSourceChain option, string network) =>
        _entries.TryGetValue(Key(invoiceId, option, network), out var e) && e.Running is { } t ? t : Task.CompletedTask;

    async Task<string?> ApiKey(Entry entry) =>
        (secrets is null ? null : (await secrets.GetAsync(entry.StoreId)).IntentsApiKey) ?? entry.Config.IntentsApiKey;

    /// <summary>
    /// The deposit to show on the token checkout for <paramref name="option"/> on <paramref name="network"/>: quoted in the background
    /// the first time, then followed. A new amount due (after a partial payment) asks a new quote; the earlier ones are still followed.
    /// </summary>
    public IntentsStatus Get(SwapSourceChain option, SwapToBtcPaymentMethodConfig config, string storeId, PaymentMethodId pmi, string invoiceId,
        string network, string btcAddress, decimal btcDue, DateTimeOffset invoiceExpiry)
    {
        if (!Networks(option, config).Any(n => n.Code == network))
            return new(IntentsState.Failed, network, null, "This payment option is unavailable for this network.");
        var target = TargetUnits(btcDue, config.BufferPercent);
        var now = Now();
        var entry = _entries.GetOrAdd(Key(invoiceId, option, network), _ => new Entry
        {
            InvoiceId = invoiceId, StoreId = storeId, PaymentMethodId = pmi, Option = option, Network = network
        });
        IntentsStatus result;
        lock (entry)
        {
            entry.Touched = now;
            entry.Config = config;
            // A deposit on its way stays on screen even if the amount due changed meanwhile.
            var shown = entry.Quotes.LastOrDefault(q => q.Payment.Status is "KNOWN_DEPOSIT_TX" or "PROCESSING" or "INCOMPLETE_DEPOSIT")
                        ?? entry.Quotes.LastOrDefault(q => q.Payment.TargetSats == target && !Expired(q, now));
            if (shown is not null)
            {
                if (!shown.Final && now - shown.Payment.CheckedAt > RecheckEvery && entry.Running is not { IsCompleted: false })
                    entry.Running = Task.Run(() => Recheck(entry, shown));
                return StatusOf(network, shown.Payment);
            }
            if (entry.Running is { IsCompleted: false })
                return new(IntentsState.Preparing, network, null, null);
            if (entry.Error is not null && entry.Target == target && now - entry.ErrorAt < RetryFailedAfter)
                return new(IntentsState.Failed, network, null, entry.Error);
            if (entry.Quotes.Count >= MaxQuotesPerEntry)
                return new(IntentsState.Failed, network, null, "This payment option is unavailable right now. Please pay from a wallet instead.");
            entry.Target = target;
            entry.Error = null;
            entry.Running = Task.Run(() => Prepare(entry, btcAddress, target, invoiceExpiry));
            result = new(IntentsState.Preparing, network, null, null);
        }
        Forget(now);
        return result;
    }

    static bool Expired(Quote q, DateTimeOffset now) => q.Payment.Deadline is { } d && now > d;

    static IntentsStatus StatusOf(string network, IntentsPayment payment) => payment.Status switch
    {
        "KNOWN_DEPOSIT_TX" or "PROCESSING" or "SUCCESS" => new(IntentsState.Received, network, payment, null),
        "INCOMPLETE_DEPOSIT" => new(IntentsState.Incomplete, network, payment, null),
        "REFUNDED" or "FAILED" => new(IntentsState.Refunded, network, payment, null),
        _ => new(IntentsState.Ready, network, payment, null)
    };

    async Task Prepare(Entry entry, string btcAddress, long target, DateTimeOffset invoiceExpiry)
    {
        var chain = entry.Option;
        var network = chain.Networks.First(n => n.Code == entry.Network);
        var config = entry.Config;
        try
        {
            var apiKey = await ApiKey(entry);
            var tokens = await Tokens(BaseUrl, apiKey);
            var origin = Single(tokens, network.Code, chain.Ticker!) ??
                         throw new OneClickException(null, $"NEAR Intents lists no single {chain.Ticker} on {network.Code}");
            var btc = Single(tokens, "btc", "BTC") ?? throw new OneClickException(null, "NEAR Intents lists no BTC");
            var now = Now();
            var deadline = (invoiceExpiry > now.AddMinutes(30) ? invoiceExpiry : now.AddMinutes(30)) + DeadlineMargin;
            var response = await client.Quote(BaseUrl, apiKey, new OneClickQuoteRequest
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
                quote.DepositMemo, quote.Deadline ?? deadline, target, quote.TimeEstimate, "PENDING_DEPOSIT", BigInteger.Zero, now);
            lock (entry)
                entry.Quotes.Add(new Quote { Payment = payment });
            await Log(entry.InvoiceId,
                $"{chain.Ticker} on {network.Name}: send {payment.Amount} {chain.Ticker} to {payment.Address}" +
                (payment.Memo is null ? "" : $" with memo {payment.Memo}") +
                $". NEAR Intents quote {response.CorrelationId}: {amountOut} satoshis to {btcAddress}, deposit deadline {payment.Deadline:u}. " +
                $"Unswapped or excess {chain.Ticker} is refunded to {config.RefundAddress(network.Family)}. Quote signature: {response.Signature}");
            // Support can find the invoice by pasting the deposit address or the quote id into the invoice search.
            await SearchTerms(entry.InvoiceId, payment.Address, response.CorrelationId);
            await Save(entry);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare a {Coin} payment on {Network} for {Address}", chain.Ticker, network.Code, btcAddress);
            var minimum = ParseMinimumUsd(ex.Message);
            if (minimum is { } usd)
                _minimums[network.Code] = (usd, Now());
            bool log;
            lock (entry)
            {
                entry.Error = minimum is not null
                    ? "This payment option is only available for larger orders right now. Please pay from a wallet instead."
                    : "This payment option is unavailable right now. Please try again shortly or pay from a wallet instead.";
                entry.ErrorAt = Now();
                // The reason goes in the invoice's log (for the store, not the customer), once per distinct reason.
                log = entry.LoggedError != ex.Message;
                entry.LoggedError = ex.Message;
            }
            if (log)
                await Log(entry.InvoiceId, $"Could not get a {chain.Ticker} on {network.Name} deposit address from NEAR Intents: {ex.Message}",
                    InvoiceEventData.EventSeverity.Warning);
        }
    }

    async Task Recheck(Entry entry, Quote quote)
    {
        var chain = entry.Option;
        var payment = quote.Payment;
        var updated = payment with { CheckedAt = Now() };
        var changed = false;
        try
        {
            var status = await client.Status(BaseUrl, await ApiKey(entry), payment.Address, payment.Memo, CancellationToken.None);
            var deposited = BigInteger.TryParse(status.SwapDetails?.DepositedAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : payment.DepositedUnits;
            updated = updated with { Status = string.IsNullOrEmpty(status.Status) ? payment.Status : status.Status, DepositedUnits = deposited };
            changed = updated.Status != payment.Status || updated.DepositedUnits != payment.DepositedUnits;
            if (updated.Status != payment.Status)
                await Log(entry.InvoiceId, $"{chain.Ticker} deposit to {payment.Address}: {updated.Status}" +
                                           (status.SwapDetails?.RefundReason is { } reason ? $" ({reason})" : ""),
                    updated.Status is "REFUNDED" or "FAILED" ? InvoiceEventData.EventSeverity.Warning : InvoiceEventData.EventSeverity.Info);
            changed |= await LogTxs(entry.InvoiceId, quote, status.SwapDetails?.OriginChainTxHashes, $"Customer's {chain.Ticker} deposit transaction");
            changed |= await LogTxs(entry.InvoiceId, quote, status.SwapDetails?.DestinationChainTxHashes, "NEAR Intents' BTC payout transaction");
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
            quote.Payment = updated;
        if (changed)
            await Save(entry);
    }

    /// <summary>Follows every open quote, whether or not anyone is looking at the checkout. Called by <see cref="IntentsTracker"/>.</summary>
    public async Task FollowAll(CancellationToken cancellationToken)
    {
        var now = Now();
        foreach (var entry in _entries.Values.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            Quote[] open;
            lock (entry)
            {
                if (entry.Running is { IsCompleted: false })
                    continue;
                open = entry.Quotes.Where(q => !q.Final && (q.Payment.Deadline is not { } d || now < d + FollowAfterDeadline) &&
                                               now - q.Payment.CheckedAt > RecheckEvery).ToArray();
            }
            foreach (var quote in open)
                await Recheck(entry, quote);
        }
        Forget(now);
    }

    /// <summary>Picks up the quotes saved on invoices BTCPay still monitors (after a restart or an update).</summary>
    public async Task Restore(StoreRepository storeRepository, CancellationToken cancellationToken)
    {
        foreach (var chain in SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Lifi))
        {
            InvoiceEntity[] invoices;
            try
            {
                invoices = await invoiceRepository.GetMonitoredInvoices(chain.PaymentMethodId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not list invoices to resume NEAR Intents deposits for {Chain}", chain.Name);
                continue;
            }
            foreach (var invoice in invoices)
            {
                if (invoice.GetPaymentPrompt(chain.PaymentMethodId)?.Details is not { } details)
                    continue;
                var saved = details.ToObject<SwapToBtcPromptDetails>(DetailsSerializer)?.IntentsQuotes;
                if (saved is not { Count: > 0 })
                    continue;
                var config = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(invoice.StoreId, Controllers.UISwapStoreController.StoreSettingName)
                             ?? new SwapToBtcPaymentMethodConfig();
                foreach (var group in saved.GroupBy(q => (q.Option, q.Network)))
                {
                    if (SwapSourceChain.ExchangeOptions.FirstOrDefault(o => o.Code == group.Key.Option) is not { } option ||
                        option.Networks.All(n => n.Code != group.Key.Network))
                        continue;
                    var entry = _entries.GetOrAdd(Key(invoice.Id, option, group.Key.Network), _ => new Entry
                    {
                        InvoiceId = invoice.Id, StoreId = invoice.StoreId, PaymentMethodId = chain.PaymentMethodId, Option = option, Network = group.Key.Network
                    });
                    lock (entry)
                    {
                        entry.Config = config;
                        entry.Touched = Now();
                        foreach (var q in group)
                        {
                            if (entry.Quotes.Any(e => e.Payment.Address == q.Address && e.Payment.Memo == q.Memo))
                                continue;
                            var quote = new Quote
                            {
                                Payment = new IntentsPayment(q.Network, ToDecimal(BigInteger.Parse(q.AmountUnits, CultureInfo.InvariantCulture), q.Decimals),
                                    BigInteger.Parse(q.AmountUnits, CultureInfo.InvariantCulture), q.Decimals, q.Address, q.Memo, q.Deadline, q.TargetSats,
                                    q.TimeEstimateSeconds, q.Status, BigInteger.Parse(q.DepositedUnits, CultureInfo.InvariantCulture), DateTimeOffset.MinValue)
                            };
                            foreach (var tx in q.LoggedTxs)
                                quote.LoggedTxs.Add(tx);
                            entry.Quotes.Add(quote);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Writes the invoice's quotes for this payment method into its prompt details.</summary>
    async Task Save(Entry entry)
    {
        var gate = _saving.GetOrAdd(entry.InvoiceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var invoice = await invoiceRepository.GetInvoice(entry.InvoiceId);
            if (invoice?.GetPaymentPrompt(entry.PaymentMethodId) is not { Details: { } details })
                return;
            var parsed = details.ToObject<SwapToBtcPromptDetails>(DetailsSerializer) ?? new SwapToBtcPromptDetails();
            var all = _entries.Values.Where(e => e.InvoiceId == entry.InvoiceId && e.PaymentMethodId == entry.PaymentMethodId).ToArray();
            parsed.IntentsQuotes = all.SelectMany(e =>
            {
                lock (e)
                    return e.Quotes.Select(q => new PersistedQuote
                    {
                        Option = e.Option.Code,
                        Network = e.Network,
                        Address = q.Payment.Address,
                        Memo = q.Payment.Memo,
                        Deadline = q.Payment.Deadline,
                        TargetSats = q.Payment.TargetSats,
                        AmountUnits = q.Payment.AmountUnits.ToString(CultureInfo.InvariantCulture),
                        Decimals = q.Payment.Decimals,
                        TimeEstimateSeconds = q.Payment.TimeEstimateSeconds,
                        Status = q.Payment.Status,
                        DepositedUnits = q.Payment.DepositedUnits.ToString(CultureInfo.InvariantCulture),
                        LoggedTxs = q.LoggedTxs.ToArray()
                    }).ToList();
            }).ToList();
            await invoiceRepository.UpdatePaymentDetails(entry.InvoiceId, entry.PaymentMethodId, Newtonsoft.Json.Linq.JObject.FromObject(parsed, DetailsSerializer));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save the NEAR Intents quotes of invoice {InvoiceId}", entry.InvoiceId);
        }
        finally
        {
            gate.Release();
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

    /// <summary>True when a transaction was new.</summary>
    async Task<bool> LogTxs(string invoiceId, Quote quote, List<OneClickTxHash>? txs, string what)
    {
        var any = false;
        foreach (var tx in txs ?? [])
        {
            if (string.IsNullOrWhiteSpace(tx.Hash))
                continue;
            lock (quote.LoggedTxs)
            {
                if (!quote.LoggedTxs.Add(tx.Hash))
                    continue;
            }
            any = true;
            await Log(invoiceId, $"{what}: {tx.Hash}" + (Uri.TryCreate(tx.ExplorerUrl, UriKind.Absolute, out var url) && url.Scheme == "https" ? $" ({url})" : ""));
            await SearchTerms(invoiceId, tx.Hash);
        }
        return any;
    }

    async Task SearchTerms(string invoiceId, params string?[] terms)
    {
        var list = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        try
        {
            await invoiceRepository.AddSearchTerms(invoiceId, list);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not add search terms to invoice {InvoiceId}", invoiceId);
        }
    }

    async Task Log(string invoiceId, string message, InvoiceEventData.EventSeverity severity = InvoiceEventData.EventSeverity.Info)
    {
        try
        {
            var logs = new InvoiceLogs();
            logs.Write(message, severity);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not log to invoice {InvoiceId}", invoiceId);
        }
    }

    static string Key(string invoiceId, SwapSourceChain option, string network) => $"{invoiceId}:{option.Code}:{network}";

    void Forget(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToArray())
        {
            bool done;
            lock (entry)
                done = now - entry.Touched > ForgetAfter &&
                       entry.Quotes.All(q => q.Final || q.Payment.Deadline is { } d && now > d + FollowAfterDeadline);
            if (done && _entries.TryRemove(key, out _))
                _saving.TryRemove(entry.InvoiceId, out _);
        }
    }
}
