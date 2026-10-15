using BTCPayServer.Plugins.Stablecoins.Solana;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class UsdcTransferParserTests
{
    const string Merchant = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
    const string Customer = "7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU";
    const string Reference = "82ZJ7nbGpixjeDCmEhUcmwXYfvurzAgGdtSMuHnUgyny";
    const string Mint = SolanaChainConfiguration.MainnetUsdcMint;
    const string OtherMint = "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB"; // USDT

    // Shape of getTransaction(sig, { encoding: "jsonParsed" }) for a Solana Pay SPL transfer.
    static JObject Tx(string mint = Mint, string recipientOwner = Merchant, long pre = 1_000_000, long post = 26_500_000,
        bool includeReference = true, object? err = null, bool referenceInLookupTable = false)
    {
        var keys = new JArray
        {
            new JObject { ["pubkey"] = Customer, ["signer"] = true, ["writable"] = true },
            new JObject { ["pubkey"] = "CustomerUsdcAta1111111111111111111111111111", ["signer"] = false, ["writable"] = true },
            new JObject { ["pubkey"] = "MerchantUsdcAta1111111111111111111111111111", ["signer"] = false, ["writable"] = true },
            new JObject { ["pubkey"] = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", ["signer"] = false, ["writable"] = false },
        };
        if (includeReference && !referenceInLookupTable)
            keys.Add(new JObject { ["pubkey"] = Reference, ["signer"] = false, ["writable"] = false });
        return new JObject
        {
            ["slot"] = 312_000_123,
            ["blockTime"] = 1_760_560_000,
            ["meta"] = new JObject
            {
                ["err"] = err is null ? JValue.CreateNull() : JToken.FromObject(err),
                ["fee"] = 5000,
                ["preTokenBalances"] = new JArray
                {
                    Balance(1, mint, Customer, 50_000_000),
                    Balance(2, mint, recipientOwner, pre)
                },
                ["postTokenBalances"] = new JArray
                {
                    Balance(1, mint, Customer, 50_000_000 - (post - pre)),
                    Balance(2, mint, recipientOwner, post)
                },
                ["loadedAddresses"] = new JObject
                {
                    ["readonly"] = referenceInLookupTable && includeReference ? new JArray(Reference) : new JArray(),
                    ["writable"] = new JArray()
                }
            },
            ["transaction"] = new JObject
            {
                ["signatures"] = new JArray("5h6xBEauJ3PK6SWCZ1PGjBvj8vDdWG3KpwATGy1ARAXFSDwt8GFXM7W5Ncn16wmqokgpiKRLuS83KUxyZyv2sUYv"),
                ["message"] = new JObject { ["accountKeys"] = keys }
            }
        };
    }

    static JObject Balance(int index, string mint, string owner, long amount) => new()
    {
        ["accountIndex"] = index,
        ["mint"] = mint,
        ["owner"] = owner,
        ["programId"] = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA",
        ["uiTokenAmount"] = new JObject { ["amount"] = amount.ToString(), ["decimals"] = 6 }
    };

    [Fact]
    public void ReadsAmountReceivedByMerchant()
    {
        var (outcome, transfer) = UsdcTransferParser.Parse(Tx(), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.Received, outcome);
        Assert.NotNull(transfer);
        Assert.Equal(25.5m, transfer.Amount);
        Assert.Equal(Customer, transfer.Payer);
        Assert.Equal(312_000_123UL, transfer.Slot);
        Assert.StartsWith("5h6x", transfer.Signature);
    }

    [Fact]
    public void FindsReferenceInAddressLookupTable()
    {
        var (outcome, _) = UsdcTransferParser.Parse(Tx(referenceInLookupTable: true), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.Received, outcome);
    }

    [Fact]
    public void CountsNewlyCreatedTokenAccount()
    {
        // No pre balance entry when the merchant's token account is created in the same transaction.
        var tx = Tx(pre: 0);
        ((JArray)tx["meta"]!["preTokenBalances"]!).RemoveAt(1);
        var (outcome, transfer) = UsdcTransferParser.Parse(tx, Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.Received, outcome);
        Assert.Equal(26.5m, transfer!.Amount);
    }

    [Fact]
    public void RejectsFailedTransaction()
    {
        var (outcome, transfer) = UsdcTransferParser.Parse(Tx(err: new { InstructionError = new object[] { 0, "Custom" } }), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.TransactionFailed, outcome);
        Assert.Null(transfer);
    }

    [Fact]
    public void RejectsWrongMint()
    {
        var (outcome, _) = UsdcTransferParser.Parse(Tx(mint: OtherMint), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.NothingReceived, outcome);
    }

    [Fact]
    public void RejectsTransferToSomeoneElse()
    {
        var (outcome, _) = UsdcTransferParser.Parse(Tx(recipientOwner: Customer), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.NothingReceived, outcome);
    }

    [Fact]
    public void RejectsOutgoingTransfer()
    {
        var (outcome, _) = UsdcTransferParser.Parse(Tx(pre: 30_000_000, post: 10_000_000), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.NothingReceived, outcome);
    }

    [Fact]
    public void RejectsTransactionWithoutReference()
    {
        var (outcome, _) = UsdcTransferParser.Parse(Tx(includeReference: false), Merchant, Mint, Reference);
        Assert.Equal(UsdcTransferParser.Outcome.ReferenceMissing, outcome);
    }
}
