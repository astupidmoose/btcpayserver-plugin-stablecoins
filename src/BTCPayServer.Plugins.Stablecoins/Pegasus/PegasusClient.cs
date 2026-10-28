#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

/// <summary>
/// Pegasus Swap's partner API (https://pegasusswap.com/api-docs). A swap returns a one-time deposit address;
/// what arrives there is exchanged and the output is sent to the receive address.
/// Every request is signed: the payload is the endpoint's name, the signature its hex HMAC-SHA512 under the secret key.
/// </summary>
public class PegasusClient(HttpClient http)
{
    public const string HttpClientName = "stablecoins-pegasus";
    public const string DefaultUrl = "https://api.pegasusswap.com";
    public const int TypeFixed = 1;

    public record Credentials(string PublicKey, string Secret);

    public static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>The networks a coin is deposited and withdrawn on, by their sub names.</summary>
    public async Task<(string[] Deposit, string[] Withdraw)> GetCoinNetworks(Uri baseUrl, Credentials keys, string coin, CancellationToken cancellationToken)
    {
        var json = Unwrap(await Send(HttpMethod.Get, baseUrl, "get-coin-network", keys, new() { ["coin"] = coin }, null, cancellationToken), "deposit");
        static string[] Names(JToken? list) =>
            list is JArray a ? a.Select(n => n is JObject o ? o["subName"]?.ToString() : n.ToString()).OfType<string>().Where(s => s.Length > 0).ToArray() : [];
        return (Names(json["deposit"]), Names(json["withdraws"] ?? json["withdraw"]));
    }

    /// <summary>
    /// An estimate. With <paramref name="lastSource"/> "receive", <paramref name="amount"/> is what is to arrive;
    /// with "deposit", what is sent.
    /// </summary>
    public async Task<PegasusEstimate> Estimate(Uri baseUrl, Credentials keys, string coinFrom, string networkFrom, string coinTo, string networkTo,
        decimal amount, string lastSource, CancellationToken cancellationToken)
    {
        var json = Unwrap(await Send(HttpMethod.Get, baseUrl, "exchange-coin", keys, new()
        {
            ["amount"] = Number(amount),
            ["coinFrom"] = coinFrom,
            ["coinTo"] = coinTo,
            ["networkFrom"] = networkFrom,
            ["networkTo"] = networkTo,
            ["lastSource"] = lastSource,
            ["typeSwap"] = TypeFixed.ToString(CultureInfo.InvariantCulture)
        }, null, cancellationToken), "receive");
        return new PegasusEstimate(Decimal(json["amount"]), Decimal(json["receive"]), Decimal(json["exchangeRate"]),
            Decimal(json["minAmount"]), Decimal(json["maxAmount"]));
    }

    public async Task<PegasusTransaction> Create(Uri baseUrl, Credentials keys, PegasusCreateRequest request, CancellationToken cancellationToken)
    {
        var body = JObject.FromObject(request, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        return Transaction(Unwrap(await Send(HttpMethod.Post, baseUrl, "create-transaction", keys, null, body, cancellationToken), "orderNumber"));
    }

    public async Task<PegasusTransaction> Get(Uri baseUrl, Credentials keys, string orderNumber, CancellationToken cancellationToken) =>
        Transaction(Unwrap(await Send(HttpMethod.Get, baseUrl, "get-transaction", keys, new() { ["id"] = orderNumber }, null, cancellationToken), "orderNumber"));

    public static PegasusTransaction Transaction(JToken json)
    {
        var deposit = json.SelectToken("pairs.deposit");
        var receive = json.SelectToken("pairs.receive");
        var payout = json["receiveTransaction"];
        return new PegasusTransaction
        {
            OrderNumber = json["orderNumber"]?.ToString() ?? "",
            Status = json["status"] is { Type: JTokenType.Integer or JTokenType.String } s && int.TryParse(s.ToString(), out var status) ? status : PegasusStatus.New,
            DepositAddress = Text(deposit?["address"]),
            DepositMemo = Text(deposit?["memo"]),
            DepositAmount = Decimal(deposit?.SelectToken("coin.value")),
            ReceiveAmount = Decimal(receive?.SelectToken("coin.value")),
            ReceiveAddress = Text(receive?["address"]),
            DepositTxId = Text(json["txId"]),
            PayoutTxId = Text(payout?["txId"]),
            PayoutAmount = Decimal(payout?["amount"])
        };
    }

    /// <summary>Some responses wrap the object in "data" or "result"; use the one that has <paramref name="field"/>.</summary>
    static JToken Unwrap(JToken json, string field)
    {
        if (json is JObject o && o[field] is null)
            foreach (var name in new[] { "data", "result" })
                if (o[name] is JObject inner && inner[field] is not null)
                    return inner;
        return json;
    }

    static string? Text(JToken? token) => token is null || token.Type == JTokenType.Null || token.ToString().Length == 0 ? null : token.ToString();

    /// <summary>Amounts come as numbers or as strings.</summary>
    public static decimal? Decimal(JToken? token) =>
        token is { Type: JTokenType.Integer or JTokenType.Float or JTokenType.String } &&
        decimal.TryParse(token.ToString(Formatting.None).Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static string Number(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    async Task<JToken> Send(HttpMethod method, Uri baseUrl, string endpoint, Credentials keys, Dictionary<string, string>? query, JObject? body,
        CancellationToken cancellationToken)
    {
        var url = baseUrl.ToString().TrimEnd('/') + "/api/private/" + endpoint +
                  (query is null ? "" : "?" + string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
        using var req = new HttpRequestMessage(method, new Uri(url));
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-api-public-key", keys.PublicKey);
        req.Headers.Add("x-api-payload", endpoint);
        req.Headers.Add("x-api-signature", Sign(endpoint, keys.Secret));
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
            throw new PegasusException((int)res.StatusCode, $"Pegasus Swap returned HTTP {(int)res.StatusCode}" +
                (Message(json) is { Length: > 0 } message ? $": {message}" : ""));
        return json;
    }

    /// <summary>"message" is a string, or a list of validation errors.</summary>
    static string? Message(JToken? json)
    {
        var m = json is JObject o ? o["message"] ?? o["error"] : null;
        var text = m switch
        {
            JArray a => string.Join("; ", a.Select(e => e is JObject eo ? (eo["msg"] ?? eo["message"])?.ToString() ?? eo.ToString(Formatting.None) : e.ToString())),
            null => null,
            _ => m.ToString()
        };
        // Shown in the invoice log: keep it short.
        return text is { Length: > 300 } ? text[..300] : text;
    }
}

public class PegasusException(int? status, string message) : Exception(message)
{
    public int? Status { get; } = status;
}

public static class PegasusStatus
{
    public const int Confirmation = 1;
    public const int Exchanging = 3;
    public const int Success = 4;
    public const int Overdue = 5;
    public const int Error = 6;
    public const int New = 9;
    public const int Pause = 10;
    public const int Stuck = 11;
    public const int Sending = 12;
    public const int Refund = 13;

    public static string Name(int status) => status switch
    {
        Confirmation => "Confirmation",
        Exchanging => "Exchanging",
        Success => "Success",
        Overdue => "Overdue",
        Error => "Error",
        New => "New",
        Pause => "Pause",
        Stuck => "Stuck",
        Sending => "Sending",
        Refund => "Refund",
        _ => $"status {status}"
    };
}

public record PegasusEstimate(decimal? Amount, decimal? Receive, decimal? ExchangeRate, decimal? MinAmount, decimal? MaxAmount);

public class PegasusCreateRequest
{
    [JsonProperty("depositCoin")] public string DepositCoin { get; set; } = "";
    [JsonProperty("depositAmount")] public decimal DepositAmount { get; set; }
    [JsonProperty("depositNetwork")] public string DepositNetwork { get; set; } = "";
    [JsonProperty("receiveAddress")] public string ReceiveAddress { get; set; } = "";
    [JsonProperty("receiveCoin")] public string ReceiveCoin { get; set; } = "";
    [JsonProperty("receiveNetwork")] public string ReceiveNetwork { get; set; } = "";
    [JsonProperty("typeSwap")] public int TypeSwap { get; set; } = PegasusClient.TypeFixed;
    [JsonProperty("lastSource")] public string LastSource { get; set; } = "deposit";
    [JsonProperty("refundAddress")] public string? RefundAddress { get; set; }
}

public class PegasusTransaction
{
    public string OrderNumber { get; set; } = "";
    public int Status { get; set; }
    public string? DepositAddress { get; set; }
    public string? DepositMemo { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? ReceiveAmount { get; set; }
    public string? ReceiveAddress { get; set; }
    /// <summary>The customer's XMR transaction.</summary>
    public string? DepositTxId { get; set; }
    /// <summary>The BTC payout transaction.</summary>
    public string? PayoutTxId { get; set; }
    public decimal? PayoutAmount { get; set; }
}
