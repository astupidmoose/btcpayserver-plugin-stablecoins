#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Thorchain;

/// <summary>
/// The few THORNode REST calls the plugin needs. Amounts on THORChain are always integers in 1e8 units,
/// whatever the coin's own decimals.
/// </summary>
public class ThornodeClient(HttpClient http)
{
    public const string HttpClientName = "stablecoins-thornode";
    public const string DefaultUrl = "https://gateway.liquify.com/chain/thorchain_api";
    public const decimal Units = 100_000_000m;

    public async Task<ThorQuote> QuoteSwap(Uri baseUrl, ThorQuoteRequest request, CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            $"from_asset={Uri.EscapeDataString(request.FromAsset)}",
            $"to_asset={Uri.EscapeDataString(request.ToAsset)}",
            $"amount={request.Amount.ToString(CultureInfo.InvariantCulture)}",
            $"destination={Uri.EscapeDataString(request.Destination)}"
        };
        if (request.StreamingInterval is { } interval)
            query.Add($"streaming_interval={interval}");
        if (request.StreamingQuantity is { } quantity)
            query.Add($"streaming_quantity={quantity}");
        if (request.ToleranceBps is { } tolerance)
            query.Add($"liquidity_tolerance_bps={tolerance}");
        if (!string.IsNullOrEmpty(request.Affiliate) && request.AffiliateBps > 0)
        {
            query.Add($"affiliate={Uri.EscapeDataString(request.Affiliate)}");
            query.Add($"affiliate_bps={request.AffiliateBps}");
        }
        if (!string.IsNullOrEmpty(request.RefundAddress))
            query.Add($"refund_address={Uri.EscapeDataString(request.RefundAddress)}");
        var url = Combine(baseUrl, "/thorchain/quote/swap?" + string.Join("&", query));
        var json = await Get(url, cancellationToken);
        return json.ToObject<ThorQuote>() ?? throw new ThornodeException("Empty quote");
    }

    public async Task<List<ThorPool>> GetPools(Uri baseUrl, CancellationToken cancellationToken)
    {
        var json = await Get(Combine(baseUrl, "/thorchain/pools"), cancellationToken);
        return json.ToObject<List<ThorPool>>() ?? [];
    }

    public async Task<List<ThorInboundAddress>> GetInboundAddresses(Uri baseUrl, CancellationToken cancellationToken)
    {
        var json = await Get(Combine(baseUrl, "/thorchain/inbound_addresses"), cancellationToken);
        return json.ToObject<List<ThorInboundAddress>>() ?? [];
    }

    /// <summary>Keeps the base URL's own path (gateway.liquify.com serves THORNode under /chain/thorchain_api).</summary>
    public static Uri Combine(Uri baseUrl, string pathAndQuery) => new(baseUrl.ToString().TrimEnd('/') + pathAndQuery);

    async Task<JToken> Get(Uri url, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Public THORNode gateways ask integrators to identify themselves.
        req.Headers.TryAddWithoutValidation("x-client-id", "btcpay-stablecoins");
        using var res = await http.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        JToken json;
        try
        {
            json = JToken.Parse(body);
        }
        catch (JsonException)
        {
            throw new ThornodeException($"THORNode returned HTTP {(int)res.StatusCode}");
        }
        if (!res.IsSuccessStatusCode || json is JObject o && (o["error"] is not null || o["code"] is not null && o["message"] is not null))
        {
            var message = json["message"]?.ToString() ?? json["error"]?.ToString() ?? $"HTTP {(int)res.StatusCode}";
            throw new ThornodeException(message);
        }
        return json;
    }

    public static long ToUnits(decimal amount) => (long)decimal.Ceiling(amount * Units);
    public static decimal FromUnits(long units) => units / Units;
}

public class ThornodeException(string message) : Exception(message);

public record ThorQuoteRequest
{
    public required string FromAsset { get; init; }
    public required string ToAsset { get; init; }
    public required long Amount { get; init; }
    public required string Destination { get; init; }
    public int? StreamingInterval { get; init; }
    public int? StreamingQuantity { get; init; }
    public int? ToleranceBps { get; init; }
    public string? Affiliate { get; init; }
    public int AffiliateBps { get; init; }
    public string? RefundAddress { get; init; }
}

public class ThorQuote
{
    [JsonProperty("inbound_address")] public string? InboundAddress { get; set; }
    [JsonProperty("router")] public string? Router { get; set; }
    [JsonProperty("memo")] public string? Memo { get; set; }
    [JsonProperty("expected_amount_out")] public long ExpectedAmountOut { get; set; }
    [JsonProperty("expiry")] public long Expiry { get; set; }
    [JsonProperty("recommended_min_amount_in")] public long? RecommendedMinAmountIn { get; set; }
    [JsonProperty("dust_threshold")] public long? DustThreshold { get; set; }
    [JsonProperty("recommended_gas_rate")] public string? RecommendedGasRate { get; set; }
    [JsonProperty("gas_rate_units")] public string? GasRateUnits { get; set; }
    [JsonProperty("warning")] public string? Warning { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("total_swap_seconds")] public long? TotalSwapSeconds { get; set; }
    [JsonProperty("outbound_delay_seconds")] public long? OutboundDelaySeconds { get; set; }
    [JsonProperty("fees")] public JObject? Fees { get; set; }
}

public class ThorPool
{
    [JsonProperty("asset")] public string Asset { get; set; } = "";
    [JsonProperty("status")] public string? Status { get; set; }
    [JsonProperty("asset_tor_price")] public long AssetTorPrice { get; set; }
}

public class ThorInboundAddress
{
    [JsonProperty("chain")] public string Chain { get; set; } = "";
    [JsonProperty("address")] public string? Address { get; set; }
    [JsonProperty("halted")] public bool Halted { get; set; }
    [JsonProperty("global_trading_paused")] public bool GlobalTradingPaused { get; set; }
    [JsonProperty("chain_trading_paused")] public bool ChainTradingPaused { get; set; }
    [JsonProperty("dust_threshold")] public long? DustThreshold { get; set; }
}
