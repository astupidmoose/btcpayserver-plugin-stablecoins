#nullable enable
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Thorchain;

/// <summary>
/// THORChain's hosted memoless API (https://api.thorchain.org/memoless). It registers a swap memo
/// on THORChain and returns a short reference number; a deposit whose amount ends in that
/// reference is swapped with the registered memo. The customer then only needs an address and an
/// exact amount, like any plain payment. The API's own wallet pays the registration.
/// </summary>
public class MemolessClient(HttpClient http)
{
    public const string HttpClientName = "stablecoins-memoless";
    public const string DefaultUrl = "https://api.thorchain.org/memoless";

    public async Task<MemolessRegistration> Register(Uri baseUrl, string asset, string memo, decimal requestedAmount, int decimals,
        CancellationToken cancellationToken)
    {
        var json = await Post(baseUrl, "/api/v1/register", new JObject
        {
            ["asset"] = asset,
            ["memo"] = memo,
            ["requested_in_asset_amount"] = Format(requestedAmount, decimals)
        }, cancellationToken);
        return json.ToObject<MemolessRegistration>() ?? throw new ThornodeException("Empty registration");
    }

    public async Task<MemolessPreflight> Preflight(Uri baseUrl, string registrationId, decimal amount, int decimals,
        CancellationToken cancellationToken)
    {
        var json = await Post(baseUrl, "/api/v1/preflight", new JObject
        {
            ["internal_api_id"] = registrationId,
            ["amount"] = Format(amount, decimals)
        }, cancellationToken);
        return json["data"]?.ToObject<MemolessPreflight>() ?? throw new ThornodeException("Empty preflight");
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public static string Format(decimal amount, int decimals) =>
        decimal.Round(amount, Math.Min(decimals, 8), MidpointRounding.ToPositiveInfinity).ToString("0." + new string('0', Math.Min(decimals, 8)), CultureInfo.InvariantCulture);

    async Task<JToken> Post(Uri baseUrl, string path, JObject body, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ThornodeClient.Combine(baseUrl, path))
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-client-id", "btcpay-stablecoins");
        using var res = await http.SendAsync(req, cancellationToken);
        var text = await res.Content.ReadAsStringAsync(cancellationToken);
        JToken json;
        try
        {
            json = JToken.Parse(text);
        }
        catch (JsonException)
        {
            throw new ThornodeException($"The memoless API returned HTTP {(int)res.StatusCode}");
        }
        if (!res.IsSuccessStatusCode || json["success"]?.Value<bool>() == false)
        {
            var error = json["error"];
            var code = error?["code"]?.ToString();
            var message = error?["message"]?.ToString() ?? error?.ToString() ?? $"HTTP {(int)res.StatusCode}";
            // The short message rarely says why; keep the whole answer for the log.
            throw new MemolessException(code, $"{message} (HTTP {(int)res.StatusCode}: {Truncate(text, 600)})");
        }
        return json;
    }
}

public class MemolessException(string? code, string message) : ThornodeException(message)
{
    public string? Code { get; } = code;
}

public class MemolessRegistration
{
    [JsonProperty("internal_api_id")] public string? Id { get; set; }
    [JsonProperty("asset")] public string? Asset { get; set; }
    /// <summary>The memo as registered. The API may add its own affiliate to it, so it is checked, not assumed.</summary>
    [JsonProperty("memo")] public string? Memo { get; set; }
    [JsonProperty("reference")] public string? Reference { get; set; }
    [JsonProperty("reference_length")] public int ReferenceLength { get; set; } = 5;
    [JsonProperty("height")] public long Height { get; set; }
    [JsonProperty("decimals")] public int? Decimals { get; set; }
    [JsonProperty("minimum_amount_to_send")] public decimal? MinimumAmountToSend { get; set; }
    [JsonProperty("suggested_in_asset_amount")] public decimal? SuggestedAmount { get; set; }
}

public class MemolessPreflight
{
    [JsonProperty("current_uses")] public int CurrentUses { get; set; }
    [JsonProperty("max_uses")] public int MaxUses { get; set; } = 1;
    [JsonProperty("memo")] public string? Memo { get; set; }
    [JsonProperty("inbound_address")] public string? InboundAddress { get; set; }
    [JsonProperty("seconds_remaining")] public long? SecondsRemaining { get; set; }
}
