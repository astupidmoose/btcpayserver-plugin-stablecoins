#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Usdc.Solana;

public record UsdcTransfer(string Signature, decimal Amount, ulong Slot, string? Payer, long? BlockTime);

/// <summary>
/// Reads a getTransaction (jsonParsed) result and works out how much of one mint
/// arrived at one owner. Uses the token balance deltas rather than instruction
/// parsing, so it counts transfers, transferChecked, swaps routed through other
/// programs, and Token-2022, and is not fooled by instructions that fail.
/// </summary>
public static class UsdcTransferParser
{
    public enum Outcome
    {
        Received,
        TransactionFailed,
        ReferenceMissing,
        NothingReceived
    }

    public static (Outcome Outcome, UsdcTransfer? Transfer) Parse(JObject transaction, string recipientOwner,
        string mint, string reference, int decimals = UsdcPlugin.UsdcDivisibility)
    {
        var signature = transaction["transaction"]?["signatures"]?.FirstOrDefault()?.Value<string>() ?? "";
        var meta = transaction["meta"] as JObject;
        if (meta is null || meta["err"] is { Type: not JTokenType.Null })
            return (Outcome.TransactionFailed, null);

        var accountKeys = GetAccountKeys(transaction, meta);
        if (!accountKeys.Contains(reference))
            return (Outcome.ReferenceMissing, null);

        var pre = SumByAccount(meta["preTokenBalances"], recipientOwner, mint);
        var post = SumByAccount(meta["postTokenBalances"], recipientOwner, mint);
        BigInteger delta = 0;
        foreach (var account in pre.Keys.Union(post.Keys))
        {
            delta += post.GetValueOrDefault(account) - pre.GetValueOrDefault(account);
        }
        if (delta <= 0)
            return (Outcome.NothingReceived, null);

        var amount = (decimal)delta / Pow10(decimals);
        var payer = FirstSigner(transaction);
        var slot = transaction.Value<ulong?>("slot") ?? 0;
        long? blockTime = transaction["blockTime"]?.Type == JTokenType.Integer ? transaction.Value<long>("blockTime") : null;
        return (Outcome.Received, new UsdcTransfer(signature, amount, slot, payer, blockTime));
    }

    static HashSet<string> GetAccountKeys(JObject transaction, JObject meta)
    {
        var keys = new HashSet<string>();
        if (transaction["transaction"]?["message"]?["accountKeys"] is JArray accountKeys)
        {
            foreach (var key in accountKeys)
            {
                // jsonParsed returns objects ({ pubkey, signer, writable }), other encodings plain strings.
                var pubkey = key.Type == JTokenType.Object ? key.Value<string>("pubkey") : key.Value<string>();
                if (pubkey is not null)
                    keys.Add(pubkey);
            }
        }
        // Versioned transactions can pull accounts from lookup tables.
        if (meta["loadedAddresses"] is JObject loaded)
        {
            foreach (var key in (loaded["readonly"] as JArray ?? []).Concat(loaded["writable"] as JArray ?? []))
            {
                if (key.Value<string>() is { } k)
                    keys.Add(k);
            }
        }
        return keys;
    }

    static Dictionary<int, BigInteger> SumByAccount(JToken? balances, string owner, string mint)
    {
        var result = new Dictionary<int, BigInteger>();
        if (balances is not JArray arr)
            return result;
        foreach (var b in arr.OfType<JObject>())
        {
            if (b.Value<string>("owner") != owner || b.Value<string>("mint") != mint)
                continue;
            var raw = b["uiTokenAmount"]?.Value<string>("amount");
            if (raw is null || !BigInteger.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                continue;
            result[b.Value<int>("accountIndex")] = value;
        }
        return result;
    }

    static string? FirstSigner(JObject transaction)
    {
        if (transaction["transaction"]?["message"]?["accountKeys"] is not JArray keys)
            return null;
        var first = keys.FirstOrDefault(k => k.Type != JTokenType.Object || k.Value<bool?>("signer") is true);
        return first is null ? null : first.Type == JTokenType.Object ? first.Value<string>("pubkey") : first.Value<string>();
    }

    static decimal Pow10(int decimals)
    {
        decimal r = 1;
        for (var i = 0; i < decimals; i++)
            r *= 10;
        return r;
    }
}
