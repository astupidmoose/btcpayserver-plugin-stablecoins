#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Stablecoins.Swap;

namespace BTCPayServer.Plugins.Stablecoins.Thorchain;

/// <summary>
/// What the checkout shows for a THORChain swap: send exactly this much of the source coin,
/// to this vault, with this memo, before this time. The memo names the invoice's BTC address.
/// </summary>
public record ThorDeposit(
    string Asset,
    string Ticker,
    long AmountUnits,
    decimal Amount,
    string InboundAddress,
    string Memo,
    long ExpectedBtcOutUnits,
    DateTimeOffset Expiry,
    long? TotalSwapSeconds,
    string? Warning);

/// <summary>
/// THORChain only quotes from an input amount, so the input that yields the BTC still due is
/// found by quoting a first estimate from pool prices and scaling until the expected output covers it.
/// Quotes are cached per invoice address and amount until shortly before they expire.
/// </summary>
public class ThorQuoteService(ThornodeClient client)
{
    public const string BtcAsset = "BTC.BTC";
    const int MaxAttempts = 4;
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    readonly ConcurrentDictionary<string, ThorDeposit> _cache = new();

    public async Task<ThorDeposit> GetDeposit(SwapSourceChain chain, SwapToBtcPaymentMethodConfig config,
        string btcAddress, decimal btcDue, CancellationToken cancellationToken)
    {
        if (chain.ThorAsset is null)
            throw new InvalidOperationException($"{chain.Name} is not a THORChain source");
        var target = TargetUnits(btcDue, config.BufferPercent);
        var key = $"{chain.Code}:{btcAddress}:{target}";
        if (_cache.TryGetValue(key, out var cached) && cached.Expiry - ExpiryMargin > DateTimeOffset.UtcNow)
            return cached;

        var baseUrl = ThornodeUrl(config);
        var inbound = (await client.GetInboundAddresses(baseUrl, cancellationToken))
            .FirstOrDefault(i => i.Chain.Equals(chain.ThorChain, StringComparison.OrdinalIgnoreCase));
        if (inbound is null)
            throw new ThornodeException($"THORChain does not list {chain.Name} right now");
        if (inbound.Halted || inbound.ChainTradingPaused || inbound.GlobalTradingPaused)
            throw new ThornodeException($"THORChain has paused {chain.Name} swaps right now");

        var pools = await client.GetPools(baseUrl, cancellationToken);
        var src = pools.FirstOrDefault(p => p.Asset == chain.ThorAsset && p.Status == "Available");
        var btc = pools.FirstOrDefault(p => p.Asset == BtcAsset && p.Status == "Available");
        if (src is not { AssetTorPrice: > 0 } || btc is not { AssetTorPrice: > 0 })
            throw new ThornodeException($"THORChain has no active {chain.Ticker} pool");

        var amountIn = EstimateInput(target, btc.AssetTorPrice, src.AssetTorPrice);
        ThorQuote? quote = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            quote = await client.QuoteSwap(baseUrl, new ThorQuoteRequest
            {
                FromAsset = chain.ThorAsset,
                ToAsset = BtcAsset,
                Amount = amountIn,
                Destination = btcAddress,
                ToleranceBps = config.ThorToleranceBps,
                // Streaming swaps get better prices on larger amounts; THORChain picks the sub-swap count.
                StreamingInterval = 1,
                StreamingQuantity = 0,
                Affiliate = config.ThorAffiliate,
                AffiliateBps = config.ThorAffiliateBps
            }, cancellationToken);
            if (quote.RecommendedMinAmountIn is { } min && amountIn < min)
            {
                amountIn = min;
                continue;
            }
            if (quote.ExpectedAmountOut >= target)
                break;
            amountIn = ScaleInput(amountIn, quote.ExpectedAmountOut, target);
        }
        if (quote is not { InboundAddress: not null, Memo: not null } || quote.ExpectedAmountOut < target)
            throw new ThornodeException("THORChain could not quote a swap covering the amount due");

        var deposit = new ThorDeposit(
            chain.ThorAsset,
            chain.Ticker!,
            amountIn,
            RoundForDisplay(amountIn, chain.Decimals),
            quote.InboundAddress,
            quote.Memo,
            quote.ExpectedAmountOut,
            DateTimeOffset.FromUnixTimeSeconds(quote.Expiry),
            quote.TotalSwapSeconds,
            quote.Warning);
        _cache[key] = deposit;
        foreach (var stale in _cache.Where(c => c.Value.Expiry < DateTimeOffset.UtcNow).Select(c => c.Key).ToList())
            _cache.TryRemove(stale, out _);
        return deposit;
    }

    public static Uri ThornodeUrl(SwapToBtcPaymentMethodConfig config) =>
        Uri.TryCreate(config.ThornodeUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            ? u
            : new Uri(ThornodeClient.DefaultUrl);

    /// <summary>The BTC due plus the buffer, in satoshis, rounded up.</summary>
    public static long TargetUnits(decimal btcDue, decimal bufferPercent) =>
        (long)decimal.Ceiling(btcDue * (1m + bufferPercent / 100m) * ThornodeClient.Units);

    /// <summary>First guess from THORChain's own USD (TOR) prices, plus 2% for fees.</summary>
    public static long EstimateInput(long targetBtcUnits, long btcTorPrice, long srcTorPrice) =>
        (long)decimal.Ceiling(targetBtcUnits * (decimal)btcTorPrice / srcTorPrice * 1.02m);

    /// <summary>Scales the input by how far the output fell short, with a little headroom for the fixed outbound fee.</summary>
    public static long ScaleInput(long amountIn, long expectedOut, long target) =>
        expectedOut <= 0 ? amountIn * 2 : (long)decimal.Ceiling(amountIn * (decimal)target / expectedOut * 1.005m);

    /// <summary>
    /// Amounts are 1e8 units on THORChain; coins with fewer decimals (XRP has 6) are rounded up to what the coin can send.
    /// </summary>
    public static decimal RoundForDisplay(long units, int decimals)
    {
        var amount = ThornodeClient.FromUnits(units);
        var factor = (decimal)Math.Pow(10, Math.Min(decimals, 8));
        return decimal.Ceiling(amount * factor) / factor;
    }
}
