#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>LI.FI's public status endpoint: what a source transaction is, and where it pays.</summary>
public class LifiStatusClient(HttpClient http)
{
    public const string HttpClientName = "stablecoins-lifi";
    public const string BaseUrl = "https://li.quest";

    /// <summary>Null while LI.FI has not indexed the transaction yet.</summary>
    public async Task<LifiTransferStatus?> Status(string txHash, CancellationToken cancellationToken)
    {
        using var res = await http.GetAsync($"{BaseUrl}/v1/status?txHash={Uri.EscapeDataString(txHash)}", cancellationToken);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        res.EnsureSuccessStatusCode();
        var json = JToken.Parse(await res.Content.ReadAsStringAsync(cancellationToken));
        var status = json.ToObject<LifiTransferStatus>();
        return status is null || status.Status == "NOT_FOUND" ? null : status;
    }
}

public class LifiTransferStatus
{
    /// <summary>PENDING, DONE, FAILED, INVALID or NOT_FOUND.</summary>
    [JsonProperty("status")] public string? Status { get; set; }
    [JsonProperty("substatus")] public string? Substatus { get; set; }
    [JsonProperty("fromAddress")] public string? FromAddress { get; set; }
    [JsonProperty("toAddress")] public string? ToAddress { get; set; }
    [JsonProperty("sending")] public LifiTransferLeg? Sending { get; set; }
    [JsonProperty("receiving")] public LifiTransferLeg? Receiving { get; set; }
}

public class LifiTransferLeg
{
    [JsonProperty("txHash")] public string? TxHash { get; set; }
    [JsonProperty("chainId")] public long? ChainId { get; set; }
    /// <summary>In the token's smallest unit: satoshis for a Bitcoin payout.</summary>
    [JsonProperty("amount")] public string? Amount { get; set; }
}
