#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Usdc.Solana;

public class SolanaRpcException(string message) : Exception(message);

public record SignatureInfo(string Signature, ulong Slot, bool Failed, string? ConfirmationStatus, long? BlockTime);

public record SignatureStatus(string Signature, bool Failed, string? ConfirmationStatus);

/// <summary>
/// Minimal JSON-RPC client for the handful of Solana calls the plugin needs.
/// See https://solana.com/docs/rpc/http
/// </summary>
public class SolanaRpcClient(HttpClient httpClient, SolanaChainConfiguration chain)
{
    public const string HttpClientName = "usdc-solana-rpc";
    int _id;

    async Task<JToken?> Call(string method, JArray parameters, CancellationToken cancellationToken)
    {
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id),
            ["method"] = method,
            ["params"] = parameters
        };
        using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(chain.RpcUri, content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SolanaRpcException($"{method} failed with HTTP {(int)response.StatusCode}: {Truncate(text)}");
        var json = JObject.Parse(text);
        if (json["error"] is JObject error)
            throw new SolanaRpcException($"{method} failed: {error["message"]}");
        return json["result"];
    }

    public async Task<ulong> GetSlot(CancellationToken cancellationToken = default)
    {
        var result = await Call("getSlot", new JArray(new JObject { ["commitment"] = "confirmed" }), cancellationToken);
        return result?.Value<ulong>() ?? 0;
    }

    /// <summary>
    /// Signatures of confirmed transactions that include <paramref name="address"/>, newest first.
    /// </summary>
    public async Task<IReadOnlyList<SignatureInfo>> GetSignaturesForAddress(string address, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var result = await Call("getSignaturesForAddress",
            new JArray(address, new JObject { ["limit"] = limit, ["commitment"] = "confirmed" }), cancellationToken);
        if (result is not JArray arr)
            return [];
        return arr.OfType<JObject>().Select(o => new SignatureInfo(
            o.Value<string>("signature")!,
            o.Value<ulong>("slot"),
            o["err"] is { Type: not JTokenType.Null },
            o.Value<string>("confirmationStatus"),
            o["blockTime"]?.Type == JTokenType.Integer ? o.Value<long>("blockTime") : null)).ToList();
    }

    /// <summary>
    /// The transaction in jsonParsed encoding, or null if the node does not have it (yet).
    /// </summary>
    public async Task<JObject?> GetTransaction(string signature, CancellationToken cancellationToken = default)
    {
        var result = await Call("getTransaction", new JArray(signature, new JObject
        {
            ["encoding"] = "jsonParsed",
            ["commitment"] = "confirmed",
            ["maxSupportedTransactionVersion"] = 0
        }), cancellationToken);
        return result as JObject;
    }

    public async Task<IReadOnlyDictionary<string, SignatureStatus?>> GetSignatureStatuses(IReadOnlyList<string> signatures,
        CancellationToken cancellationToken = default)
    {
        var statuses = new Dictionary<string, SignatureStatus?>();
        // The RPC accepts at most 256 signatures per call.
        foreach (var chunk in signatures.Chunk(256))
        {
            var result = await Call("getSignatureStatuses",
                new JArray(new JArray(chunk), new JObject { ["searchTransactionHistory"] = true }), cancellationToken);
            var values = result?["value"] as JArray;
            for (var i = 0; i < chunk.Length; i++)
            {
                var v = values?.ElementAtOrDefault(i) as JObject;
                statuses[chunk[i]] = v is null
                    ? null
                    : new SignatureStatus(chunk[i], v["err"] is { Type: not JTokenType.Null }, v.Value<string>("confirmationStatus"));
            }
        }
        return statuses;
    }

    /// <summary>
    /// Token accounts held by <paramref name="owner"/> for <paramref name="mint"/>.
    /// Used by the settings page to warn when the merchant has no USDC account yet.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTokenAccountsByOwner(string owner, string mint,
        CancellationToken cancellationToken = default)
    {
        var result = await Call("getTokenAccountsByOwner", new JArray(owner,
            new JObject { ["mint"] = mint },
            new JObject { ["encoding"] = "jsonParsed", ["commitment"] = "confirmed" }), cancellationToken);
        return (result?["value"] as JArray)?.Select(v => v.Value<string>("pubkey")!).ToList() ?? [];
    }

    static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
