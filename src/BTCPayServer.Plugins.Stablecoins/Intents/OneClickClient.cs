#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Intents;

/// <summary>
/// NEAR Intents' 1Click API (https://1click.chaindefuser.com). A quote returns a one-time deposit address on the
/// origin chain; whatever arrives there is swapped by NEAR Intents' solvers and the output is sent to the recipient.
/// </summary>
public class OneClickClient(HttpClient http)
{
    public const string HttpClientName = "stablecoins-oneclick";
    public const string DefaultUrl = "https://1click.chaindefuser.com";

    public async Task<List<OneClickToken>> GetTokens(Uri baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        var json = await Send(HttpMethod.Get, baseUrl, "/v0/tokens", apiKey, null, cancellationToken);
        return json.ToObject<List<OneClickToken>>() ?? [];
    }

    public async Task<OneClickQuoteResponse> Quote(Uri baseUrl, string? apiKey, OneClickQuoteRequest request, CancellationToken cancellationToken)
    {
        var json = await Send(HttpMethod.Post, baseUrl, "/v0/quote", apiKey, JObject.FromObject(request, JsonSerializer.Create(Settings)), cancellationToken);
        return json.ToObject<OneClickQuoteResponse>() ?? throw new OneClickException(null, "Empty quote");
    }

    public async Task<OneClickStatus> Status(Uri baseUrl, string? apiKey, string depositAddress, string? memo, CancellationToken cancellationToken)
    {
        var path = "/v0/status?depositAddress=" + Uri.EscapeDataString(depositAddress) +
                   (string.IsNullOrEmpty(memo) ? "" : "&depositMemo=" + Uri.EscapeDataString(memo));
        var json = await Send(HttpMethod.Get, baseUrl, path, apiKey, null, cancellationToken);
        return json.ToObject<OneClickStatus>() ?? throw new OneClickException(null, "Empty status");
    }

    static readonly JsonSerializerSettings Settings = new() { NullValueHandling = NullValueHandling.Ignore };

    async Task<JToken> Send(HttpMethod method, Uri baseUrl, string pathAndQuery, string? apiKey, JObject? body, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, new Uri(baseUrl.ToString().TrimEnd('/') + pathAndQuery));
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
            req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, cancellationToken);
        var text = await res.Content.ReadAsStringAsync(cancellationToken);
        JToken? json = null;
        try
        {
            json = JToken.Parse(text);
        }
        catch (JsonException)
        {
        }
        if (!res.IsSuccessStatusCode || json is null)
            throw new OneClickException((int)res.StatusCode, json?["message"]?.ToString() ?? $"NEAR Intents returned HTTP {(int)res.StatusCode}");
        return json;
    }
}

public class OneClickException(int? status, string message) : Exception(message)
{
    public int? Status { get; } = status;
}

public class OneClickToken
{
    [JsonProperty("assetId")] public string AssetId { get; set; } = "";
    [JsonProperty("decimals")] public int Decimals { get; set; }
    [JsonProperty("blockchain")] public string Blockchain { get; set; } = "";
    [JsonProperty("symbol")] public string Symbol { get; set; } = "";
    [JsonProperty("price")] public decimal? Price { get; set; }
    [JsonProperty("contractAddress")] public string? ContractAddress { get; set; }
}

public class OneClickQuoteRequest
{
    [JsonProperty("dry")] public bool Dry { get; set; }
    [JsonProperty("swapType")] public string SwapType { get; set; } = "EXACT_OUTPUT";
    [JsonProperty("slippageTolerance")] public int SlippageTolerance { get; set; }
    [JsonProperty("originAsset")] public string OriginAsset { get; set; } = "";
    [JsonProperty("depositType")] public string DepositType { get; set; } = "ORIGIN_CHAIN";
    [JsonProperty("destinationAsset")] public string DestinationAsset { get; set; } = "";
    /// <summary>Integer string in the smallest unit; with EXACT_OUTPUT it is the output (satoshis of BTC).</summary>
    [JsonProperty("amount")] public string Amount { get; set; } = "";
    [JsonProperty("refundTo")] public string RefundTo { get; set; } = "";
    [JsonProperty("refundType")] public string RefundType { get; set; } = "ORIGIN_CHAIN";
    [JsonProperty("recipient")] public string Recipient { get; set; } = "";
    [JsonProperty("recipientType")] public string RecipientType { get; set; } = "DESTINATION_CHAIN";
    [JsonProperty("deadline")] public string Deadline { get; set; } = "";
    [JsonProperty("referral")] public string? Referral { get; set; }
}

public class OneClickQuoteResponse
{
    [JsonProperty("correlationId")] public string? CorrelationId { get; set; }
    [JsonProperty("timestamp")] public string? Timestamp { get; set; }
    /// <summary>The service's signature over the quote; kept in the invoice log for disputes.</summary>
    [JsonProperty("signature")] public string? Signature { get; set; }
    [JsonProperty("quote")] public OneClickQuote Quote { get; set; } = new();
}

public class OneClickQuote
{
    [JsonProperty("depositAddress")] public string? DepositAddress { get; set; }
    [JsonProperty("depositMemo")] public string? DepositMemo { get; set; }
    [JsonProperty("amountIn")] public string AmountIn { get; set; } = "0";
    [JsonProperty("amountInFormatted")] public string? AmountInFormatted { get; set; }
    [JsonProperty("minAmountIn")] public string? MinAmountIn { get; set; }
    [JsonProperty("amountOut")] public string AmountOut { get; set; } = "0";
    [JsonProperty("minAmountOut")] public string? MinAmountOut { get; set; }
    [JsonProperty("deadline")] public DateTimeOffset? Deadline { get; set; }
    [JsonProperty("timeEstimate")] public long? TimeEstimate { get; set; }
}

public class OneClickStatus
{
    /// <summary>PENDING_DEPOSIT, KNOWN_DEPOSIT_TX, INCOMPLETE_DEPOSIT, PROCESSING, SUCCESS, REFUNDED or FAILED.</summary>
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("swapDetails")] public OneClickSwapDetails? SwapDetails { get; set; }
}

public class OneClickSwapDetails
{
    [JsonProperty("depositedAmount")] public string? DepositedAmount { get; set; }
    [JsonProperty("refundedAmount")] public string? RefundedAmount { get; set; }
    [JsonProperty("refundReason")] public string? RefundReason { get; set; }
}
