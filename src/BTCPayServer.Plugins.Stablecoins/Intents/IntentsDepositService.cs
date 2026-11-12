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
    /// <summary>A payment from the customer's connected wallet (<see cref="IntentsDepositService.WalletOption"/>): the NEAR Intents asset paid with.</summary>
    public string? AssetId { get; set; }
    /// <summary>The token's symbol, for the invoice log (wallet payments).</summary>
    public string? Symbol { get; set; }
    /// <summary>Where NEAR Intents returns what it cannot swap, and any excess: the customer's wallet (wallet payments).</summary>
    public string? RefundTo { get; set; }
    /// <summary>The token chain (code) the customer paid from (wallet payments).</summary>
    public string? PaidFrom { get; set; }
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
        /// <summary>The customer's wallet, for a wallet payment: where NEAR Intents refunds.</summary>
        public string? RefundTo;
        /// <summary>When this server made it; MinValue for one picked up after a restart.</summary>
        public DateTimeOffset Created = DateTimeOffset.MinValue;
    }

    class Entry
    {
        public required string InvoiceId;
        public required string StoreId;
        public required PaymentMethodId PaymentMethodId;
        /// <summary>The exchange option (USDC, USDT), or for a wallet payment the token chain the customer paid from.</summary>
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
        /// <summary>A payment from the customer's connected wallet, in <see cref="AssetId"/>.</summary>
        public bool Wallet;
        public string? AssetId;
        public string? Symbol;
        /// <summary>One deposit is made at a time per wallet entry, so pressing Pay twice never makes two.</summary>
        public readonly SemaphoreSlim Creating = new(1, 1);
        /// <summary>The coin's name in the invoice log.</summary>
        public string Coin => (Wallet ? Symbol : Option.Ticker) ?? "tokens";
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new();
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

    Task<string?> ApiKey(Entry entry) => ApiKey(entry.StoreId, entry.Config);

    async Task<string?> ApiKey(string storeId, SwapToBtcPaymentMethodConfig config) =>
        (secrets is null ? null : (await secrets.GetAsync(storeId)).IntentsApiKey) ?? config.IntentsApiKey;

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

    // ---- Payments from the customer's connected wallet ------------------------------------------------------------
    // The wallet panel asks LI.FI and NEAR Intents what the chosen token must be, and pays through whichever asks less.
    // A NEAR Intents payment is a plain transfer of that token from the customer's wallet to a one-time deposit address,
    // made for this invoice's BTC address; what NEAR Intents cannot swap, and any excess, goes back to the customer's wallet.

    /// <summary>The <see cref="PersistedQuote.Option"/> of a payment from the customer's connected wallet.</summary>
    public const string WalletOption = "WALLET";
    /// <summary>Pressing Pay again within this time reuses the deposit already made, instead of making another.</summary>
    static readonly TimeSpan ReuseWalletDepositFor = TimeSpan.FromMinutes(5);
    /// <summary>A price check is reused this long, so a busy checkout does not ask NEAR Intents on every refresh.</summary>
    static readonly TimeSpan WalletQuoteFor = TimeSpan.FromSeconds(20);

    /// <summary>How LI.FI names each chain family's native coin.</summary>
    static readonly string[] EvmNative = ["0x0000000000000000000000000000000000000000", "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"];
    const string SolanaNative = "11111111111111111111111111111111";
    const string TronNative = "T9yD14Nj9j7xAB4dbGeiX9h8unkKHxuWwb";

    public static bool IsNative(AddressFamily family, string token) => family switch
    {
        AddressFamily.Evm => EvmNative.Contains(token, StringComparer.OrdinalIgnoreCase),
        AddressFamily.Solana => token == SolanaNative,
        _ => token == TronNative
    };

    /// <summary>
    /// NEAR Intents' asset for a token the customer holds (LI.FI's address for it) on one chain: the native coin, or the token
    /// with that exact contract. Only one unambiguous, current match with the same decimals, so an amount is never read in
    /// the wrong unit and a look-alike token is never taken for another.
    /// </summary>
    public static OneClickToken? MatchToken(IEnumerable<OneClickToken> tokens, string blockchain, AddressFamily family, string token, int decimals)
    {
        var native = IsNative(family, token);
        // EVM addresses are hex (any case); Solana and Tron addresses are case-sensitive.
        var compare = family == AddressFamily.Evm ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = tokens.Where(t => t.Blockchain.Equals(blockchain, StringComparison.OrdinalIgnoreCase) &&
                                        !t.Symbol.Contains("DEPRECATED", StringComparison.OrdinalIgnoreCase) &&
                                        (native ? string.IsNullOrEmpty(t.ContractAddress) : string.Equals(t.ContractAddress, token, compare)))
            .Take(2).ToList();
        return matches.Count == 1 && matches[0].Decimals == decimals ? matches[0] : null;
    }

    /// <summary>A NEAR Intents price for paying from the customer's wallet: send <see cref="AmountIn"/> of the token.</summary>
    public record WalletQuote(string AssetId, BigInteger AmountIn, int Decimals, decimal? AmountInUsd, long TargetSats, long? TimeEstimateSeconds);

    /// <summary>Send exactly <see cref="AmountIn"/> of the token to <see cref="Address"/>.</summary>
    public record WalletDeposit(string Address, BigInteger AmountIn, int Decimals, DateTimeOffset? Deadline);

    readonly ConcurrentDictionary<(string BtcAddress, string Asset, long Target, string RefundTo), (WalletQuote Quote, DateTimeOffset At)> _walletQuotes = new();

    async Task<(OneClickToken Origin, OneClickToken Btc, string? ApiKey)> WalletAssets(string storeId, SwapToBtcPaymentMethodConfig config,
        SwapSourceChain paidFrom, string token, int decimals, string fromAddress)
    {
        if (paidFrom.Provider != SwapProvider.Lifi || paidFrom.IntentsNetworkCode is not { } network)
            throw new IntentsWalletException("Not a token chain");
        if (!IsAddressOf(paidFrom.Family, fromAddress))
            throw new IntentsWalletException($"{fromAddress} is not a {paidFrom.Name} wallet address");
        var apiKey = await ApiKey(storeId, config);
        var tokens = await Tokens(BaseUrl, apiKey);
        var origin = MatchToken(tokens, network, paidFrom.Family, token, decimals) ??
                     throw new IntentsWalletException($"NEAR Intents does not take this token on {paidFrom.Name}");
        var btc = Single(tokens, "btc", "BTC") ?? throw new IntentsWalletException("NEAR Intents lists no BTC");
        return (origin, btc, apiKey);
    }

    static bool IsAddressOf(AddressFamily family, string address) => family switch
    {
        AddressFamily.Evm => AddressChecks.IsEvmAddress(address),
        AddressFamily.Solana => AddressChecks.IsSolanaAddress(address),
        _ => AddressChecks.IsTronAddress(address)
    };

    OneClickQuoteRequest WalletRequest(SwapToBtcPaymentMethodConfig config, OneClickToken origin, OneClickToken btc, long target, string fromAddress,
        string btcAddress, DateTimeOffset deadline, bool dry) => new()
    {
        Dry = dry,
        SwapType = "EXACT_OUTPUT",
        SlippageTolerance = Math.Clamp(config.IntentsSlippageBps, 1, 1000),
        OriginAsset = origin.AssetId,
        DestinationAsset = btc.AssetId,
        Amount = target.ToString(CultureInfo.InvariantCulture),
        // The customer paid from their own wallet: NEAR Intents returns what it cannot swap, and any excess, there.
        RefundTo = fromAddress,
        Recipient = btcAddress,
        Deadline = deadline.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
    };

    /// <summary>Checks an exact-output quote's amounts: some input, and at least the BTC due to this invoice's address.</summary>
    static (BigInteger AmountIn, long AmountOut) CheckAmounts(OneClickQuote quote, long target)
    {
        if (!BigInteger.TryParse(quote.AmountIn, NumberStyles.None, CultureInfo.InvariantCulture, out var amountIn) || amountIn <= 0)
            throw new IntentsWalletException($"The quote has no amount ({quote.AmountIn})");
        if (!long.TryParse(quote.AmountOut, NumberStyles.None, CultureInfo.InvariantCulture, out var amountOut) || amountOut < target)
            throw new IntentsWalletException($"The quote pays {quote.AmountOut} satoshis, less than the {target} due");
        return (amountIn, amountOut);
    }

    /// <summary>
    /// What the customer's <paramref name="token"/> must be, through NEAR Intents, to deliver the BTC due (plus the store's
    /// buffer) to <paramref name="btcAddress"/>. A price check only (a dry quote): nothing is made at NEAR Intents.
    /// Throws <see cref="IntentsWalletException"/> when NEAR Intents cannot do it.
    /// </summary>
    public async Task<WalletQuote> QuoteWallet(SwapToBtcPaymentMethodConfig config, string storeId, SwapSourceChain paidFrom, string token,
        int decimals, string fromAddress, string btcAddress, decimal btcDue, CancellationToken cancellationToken)
    {
        var target = TargetUnits(btcDue, config.BufferPercent);
        var (origin, btc, apiKey) = await WalletAssets(storeId, config, paidFrom, token, decimals, fromAddress);
        var now = Now();
        var key = (btcAddress, origin.AssetId, target, fromAddress);
        if (_walletQuotes.TryGetValue(key, out var cached) && now - cached.At < WalletQuoteFor)
            return cached.Quote;
        var response = await client.Quote(BaseUrl, apiKey, WalletRequest(config, origin, btc, target, fromAddress, btcAddress, now.AddMinutes(30), dry: true),
            cancellationToken);
        var (amountIn, _) = CheckAmounts(response.Quote, target);
        var result = new WalletQuote(origin.AssetId, amountIn, origin.Decimals, response.Quote.AmountInUsd, target, response.Quote.TimeEstimate);
        foreach (var (k, old) in _walletQuotes.ToArray())
            if (now - old.At >= WalletQuoteFor)
                _walletQuotes.TryRemove(k, out _);
        _walletQuotes[key] = (result, now);
        return result;
    }

    Entry WalletEntry(string invoiceId, string storeId, PaymentMethodId pmi, SwapSourceChain paidFrom, string network, string assetId, string? symbol) =>
        _entries.GetOrAdd($"{invoiceId}:{WalletOption}:{assetId}", _ => new Entry
        {
            InvoiceId = invoiceId, StoreId = storeId, PaymentMethodId = pmi, Option = paidFrom, Network = network,
            Wallet = true, AssetId = assetId, Symbol = symbol
        });

    /// <summary>
    /// Makes the NEAR Intents deposit the customer's wallet pays: an exact-output quote for the BTC due to this invoice's
    /// address, refunding to the customer's wallet. It is saved on the invoice, and followed like every other deposit,
    /// before its address is returned: an address that a restart could lose is never paid. Pressing Pay again shortly after
    /// reuses it. Throws <see cref="IntentsWalletException"/> when no deposit can be made.
    /// </summary>
    public async Task<WalletDeposit> CreateWalletDeposit(SwapToBtcPaymentMethodConfig config, string storeId, PaymentMethodId pmi, string invoiceId,
        SwapSourceChain paidFrom, string token, int decimals, string fromAddress, string btcAddress, decimal btcDue, DateTimeOffset invoiceExpiry)
    {
        var target = TargetUnits(btcDue, config.BufferPercent);
        var (origin, btc, apiKey) = await WalletAssets(storeId, config, paidFrom, token, decimals, fromAddress);
        var entry = WalletEntry(invoiceId, storeId, pmi, paidFrom, paidFrom.IntentsNetworkCode!, origin.AssetId, origin.Symbol);
        await entry.Creating.WaitAsync();
        try
        {
            var now = Now();
            lock (entry)
            {
                entry.Touched = now;
                entry.Config = config;
                var recent = entry.Quotes.LastOrDefault(q => q.Payment.TargetSats == target && q.Payment.Status == "PENDING_DEPOSIT" &&
                                                             string.Equals(q.RefundTo, fromAddress, StringComparison.OrdinalIgnoreCase) &&
                                                             now - q.Created < ReuseWalletDepositFor && !Expired(q, now));
                if (recent is not null)
                    return new WalletDeposit(recent.Payment.Address, recent.Payment.AmountUnits, recent.Payment.Decimals, recent.Payment.Deadline);
                if (entry.Quotes.Count >= MaxQuotesPerEntry)
                    throw new IntentsWalletException("Too many deposits for this invoice and token");
            }
            var deadline = (invoiceExpiry > now.AddMinutes(30) ? invoiceExpiry : now.AddMinutes(30)) + DeadlineMargin;
            var response = await client.Quote(BaseUrl, apiKey, WalletRequest(config, origin, btc, target, fromAddress, btcAddress, deadline, dry: false),
                CancellationToken.None);
            var quote = response.Quote;
            if (string.IsNullOrEmpty(quote.DepositAddress))
                throw new IntentsWalletException("The quote has no deposit address");
            // A wallet transfer carries no memo: a deposit that needs one could not be matched to this payment.
            if (!string.IsNullOrEmpty(quote.DepositMemo))
                throw new IntentsWalletException("The deposit needs a memo, which a wallet transfer cannot carry");
            if (!IsAddressOf(paidFrom.Family, quote.DepositAddress))
                throw new IntentsWalletException($"The deposit address {quote.DepositAddress} is not a {paidFrom.Name} address");
            var (amountIn, amountOut) = CheckAmounts(quote, target);
            var payment = new IntentsPayment(entry.Network, ToDecimal(amountIn, origin.Decimals), amountIn, origin.Decimals, quote.DepositAddress,
                null, quote.Deadline ?? deadline, target, quote.TimeEstimate, "PENDING_DEPOSIT", BigInteger.Zero, now);
            var made = new Quote { Payment = payment, RefundTo = fromAddress, Created = now };
            lock (entry)
                entry.Quotes.Add(made);
            if (!await Save(entry))
            {
                lock (entry)
                    entry.Quotes.Remove(made);
                throw new IntentsWalletException("The deposit could not be saved on the invoice");
            }
            await Log(invoiceId,
                $"Wallet payment on {paidFrom.Name}: the customer's wallet {fromAddress} is to send {payment.Amount} {origin.Symbol} to NEAR Intents " +
                $"deposit {payment.Address}. Quote {response.CorrelationId}: {amountOut} satoshis to {btcAddress}, deposit deadline {payment.Deadline:u}. " +
                $"Anything NEAR Intents cannot swap, and any excess, is refunded to the customer's wallet {fromAddress}. Quote signature: {response.Signature}");
            // Support can find the invoice by the deposit address, the quote id, or the customer's wallet (as wallets show it, too).
            await SearchTerms(invoiceId, payment.Address, response.CorrelationId, fromAddress, SourceVerifier.Checksummed(fromAddress));
            return new WalletDeposit(payment.Address, amountIn, origin.Decimals, payment.Deadline);
        }
        finally
        {
            entry.Creating.Release();
        }
    }

    /// <summary>True when <paramref name="depositAddress"/> is a wallet deposit made for this invoice.</summary>
    public bool HasWalletDeposit(string invoiceId, string depositAddress) =>
        _entries.Values.Where(e => e.Wallet && e.InvoiceId == invoiceId).Any(e =>
        {
            lock (e)
                return e.Quotes.Any(q => q.Payment.Address == depositAddress);
        });

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
        var coin = entry.Coin;
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
                await Log(entry.InvoiceId, $"{coin} deposit to {payment.Address}: {updated.Status}" +
                                           (status.SwapDetails?.RefundReason is { } reason ? $" ({reason})" : ""),
                    updated.Status is "REFUNDED" or "FAILED" ? InvoiceEventData.EventSeverity.Warning : InvoiceEventData.EventSeverity.Info);
            changed |= await LogTxs(entry.InvoiceId, quote, status.SwapDetails?.OriginChainTxHashes, $"Customer's {coin} deposit transaction");
            changed |= await LogTxs(entry.InvoiceId, quote, status.SwapDetails?.DestinationChainTxHashes, "NEAR Intents' BTC payout transaction");
        }
        catch (OneClickException ex) when (ex.Status == 404)
        {
            // Not seen yet.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the {Coin} deposit {Address}", coin, payment.Address);
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
                RestoreWallet(invoice.Id, invoice.StoreId, chain, config, saved);
                foreach (var group in saved.Where(q => q.Option != WalletOption).GroupBy(q => (q.Option, q.Network)))
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
    /// <summary>
    /// Picks up the wallet payments saved on an invoice (tests call it directly). Each is followed again until NEAR Intents
    /// settles or refunds it.
    /// </summary>
    public void RestoreWallet(string invoiceId, string storeId, SwapSourceChain checkout, SwapToBtcPaymentMethodConfig config, IEnumerable<PersistedQuote> saved)
    {
        foreach (var group in saved.Where(q => q.Option == WalletOption && !string.IsNullOrEmpty(q.AssetId)).GroupBy(q => q.AssetId!))
        {
            var first = group.First();
            var paidFrom = SwapSourceChain.Find(first.PaidFrom) is { Provider: SwapProvider.Lifi } c ? c : checkout;
            var entry = WalletEntry(invoiceId, storeId, checkout.PaymentMethodId, paidFrom, first.Network, group.Key, first.Symbol);
            lock (entry)
            {
                entry.Config = config;
                entry.Touched = Now();
                foreach (var q in group)
                {
                    if (entry.Quotes.Any(e => e.Payment.Address == q.Address))
                        continue;
                    var units = BigInteger.Parse(q.AmountUnits, CultureInfo.InvariantCulture);
                    var quote = new Quote
                    {
                        Payment = new IntentsPayment(q.Network, ToDecimal(units, q.Decimals), units, q.Decimals, q.Address, q.Memo, q.Deadline, q.TargetSats,
                            q.TimeEstimateSeconds, q.Status, BigInteger.Parse(q.DepositedUnits, CultureInfo.InvariantCulture), DateTimeOffset.MinValue),
                        RefundTo = q.RefundTo
                    };
                    foreach (var tx in q.LoggedTxs)
                        quote.LoggedTxs.Add(tx);
                    entry.Quotes.Add(quote);
                }
            }
        }
    }

    /// <summary>Where the invoice's quotes are saved instead of its prompt details; set by tests. Throwing is a failed save.</summary>
    public Func<string, PaymentMethodId, List<PersistedQuote>, Task>? WriteQuotes { get; set; }

    /// <summary>True when the invoice's quotes were written.</summary>
    async Task<bool> Save(Entry entry)
    {
        var gate = PromptDetailsGate.For(entry.InvoiceId);
        await gate.WaitAsync();
        try
        {
            var all = _entries.Values.Where(e => e.InvoiceId == entry.InvoiceId && e.PaymentMethodId == entry.PaymentMethodId).ToArray();
            var quotes = all.SelectMany(e =>
            {
                lock (e)
                    return e.Quotes.Select(q => new PersistedQuote
                    {
                        Option = e.Wallet ? WalletOption : e.Option.Code,
                        AssetId = e.Wallet ? e.AssetId : null,
                        Symbol = e.Wallet ? e.Symbol : null,
                        RefundTo = q.RefundTo,
                        PaidFrom = e.Wallet ? e.Option.Code : null,
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
            if (WriteQuotes is not null)
            {
                await WriteQuotes(entry.InvoiceId, entry.PaymentMethodId, quotes);
                return true;
            }
            var invoice = await invoiceRepository.GetInvoice(entry.InvoiceId);
            if (invoice?.GetPaymentPrompt(entry.PaymentMethodId) is not { Details: { } details })
                return false;
            var parsed = details.ToObject<SwapToBtcPromptDetails>(DetailsSerializer) ?? new SwapToBtcPromptDetails();
            parsed.IntentsQuotes = quotes;
            await invoiceRepository.UpdatePaymentDetails(entry.InvoiceId, entry.PaymentMethodId, Newtonsoft.Json.Linq.JObject.FromObject(parsed, DetailsSerializer));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save the NEAR Intents quotes of invoice {InvoiceId}", entry.InvoiceId);
            return false;
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
            if (done)
                _entries.TryRemove(key, out _);
        }
    }
}

/// <summary>NEAR Intents cannot take this wallet payment; the reason is for the invoice log and the server log, not the customer.</summary>
public class IntentsWalletException(string message) : Exception(message);
