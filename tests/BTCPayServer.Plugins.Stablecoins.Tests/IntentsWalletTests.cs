using System.Net;
using System.Numerics;
using System.Text;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Controllers;
using BTCPayServer.Plugins.Stablecoins.Intents;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

/// <summary>NEAR Intents as a second price, and backup, for payments from the customer's connected wallet.</summary>
public class IntentsWalletTests
{
    // BIP-173 test vector: not on any provider's block list.
    const string BtcAddress = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    const string EvmWallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    const string SolWallet = "7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU";
    const string SolUsdc = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    const string EthUsdc = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
    static readonly SwapSourceChain Solana = SwapSourceChain.Find("SOLANA")!;
    static readonly SwapSourceChain Ethereum = SwapSourceChain.Find("ETHEREUM")!;
    static readonly SwapSourceChain Bsc = SwapSourceChain.Find("BSC")!;
    static readonly PaymentMethodId Pmi = Solana.PaymentMethodId;

    static readonly OneClickToken[] Tokens =
    [
        new() { AssetId = "nep141:btc.omft.near", Blockchain = "btc", Symbol = "BTC", Decimals = 8, Price = 60000m },
        new() { AssetId = "nep141:eth.omft.near", Blockchain = "eth", Symbol = "ETH", Decimals = 18 },
        new() { AssetId = "nep141:eth-usdc", Blockchain = "eth", Symbol = "USDC", Decimals = 6, ContractAddress = EthUsdc },
        new() { AssetId = "nep141:sol.omft.near", Blockchain = "sol", Symbol = "SOL", Decimals = 9 },
        new() { AssetId = "nep141:sol-usdc", Blockchain = "sol", Symbol = "USDC", Decimals = 6, ContractAddress = SolUsdc },
        new() { AssetId = "nep245:bnb-old", Blockchain = "bsc", Symbol = "BNB(DEPRECATED)", Decimals = 18 },
        new() { AssetId = "nep141:bnb.omdep.near", Blockchain = "bsc", Symbol = "BNB", Decimals = 18 },
        new() { AssetId = "nep141:tron.omft.near", Blockchain = "tron", Symbol = "TRX", Decimals = 6 },
        new() { AssetId = "nep141:twin-a", Blockchain = "base", Symbol = "TWIN", Decimals = 18, ContractAddress = "0x1111111111111111111111111111111111111111" },
        new() { AssetId = "nep141:twin-b", Blockchain = "base", Symbol = "TWIN", Decimals = 18, ContractAddress = "0x1111111111111111111111111111111111111111" },
    ];

    /// <summary>1Click: every exact-output quote costs 2 units of the token per satoshi; every real one gets a new deposit address.</summary>
    class FakeOneClick : HttpMessageHandler
    {
        public List<JObject> Quotes { get; } = new();
        public long? AmountOutOverride { get; set; }
        public string? Memo { get; set; }
        public string Status { get; set; } = "PENDING_DEPOSIT";
        int _deposits;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JToken json;
            if (path == "/v0/tokens")
                json = JArray.FromObject(Tokens);
            else if (path == "/v0/quote")
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Quotes.Add(body);
                var sats = long.Parse(body["amount"]!.ToString());
                var quote = new JObject
                {
                    ["amountIn"] = (new BigInteger(sats) * 2).ToString(),
                    ["amountInUsd"] = "76.17",
                    ["amountOut"] = (AmountOutOverride ?? sats).ToString(),
                    ["deadline"] = body["deadline"],
                    ["timeEstimate"] = 120
                };
                if (body["dry"]!.Value<bool>() == false)
                {
                    _deposits++;
                    quote["depositAddress"] = body["originAsset"]!.ToString().StartsWith("nep141:sol") ? $"Dep{_deposits}osit11111111111111111111111111111111" : $"0x{_deposits:x40}";
                    if (Memo is not null)
                        quote["depositMemo"] = Memo;
                }
                json = new JObject { ["correlationId"] = "c" + Quotes.Count, ["signature"] = "sig", ["quote"] = quote };
            }
            else if (path == "/v0/status")
                json = new JObject { ["status"] = Status, ["swapDetails"] = new JObject { ["originChainTxHashes"] = new JArray(new JObject { ["hash"] = "5solTx" }) } };
            else
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") };
        }
    }

    static (IntentsDepositService Service, FakeOneClick Fake, List<List<PersistedQuote>> Saved) Create()
    {
        var fake = new FakeOneClick();
        var saved = new List<List<PersistedQuote>>();
        var service = new IntentsDepositService(new OneClickClient(new HttpClient(fake)), null!, NullLogger<IntentsDepositService>.Instance)
        {
            BaseUrl = new Uri("https://oneclick.example.com"),
            WriteQuotes = (_, _, quotes) => { saved.Add(quotes); return Task.CompletedTask; }
        };
        return (service, fake, saved);
    }

    static SwapToBtcPaymentMethodConfig Config() => new() { IntentsApiKey = "jwt", BufferPercent = 1m };

    static Task<IntentsDepositService.WalletDeposit> Deposit(IntentsDepositService service, decimal btcDue = 0.001m, string token = SolUsdc,
        int decimals = 6, string from = SolWallet) =>
        service.CreateWalletDeposit(Config(), "store", Pmi, "inv1", Solana, token, decimals, from, BtcAddress, btcDue, DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void MatchesOnlyTheSameTokenWithTheSameDecimals()
    {
        Assert.Equal("nep141:eth.omft.near", IntentsDepositService.MatchToken(Tokens, "eth", AddressFamily.Evm, "0x0000000000000000000000000000000000000000", 18)?.AssetId);
        Assert.Equal("nep141:eth.omft.near", IntentsDepositService.MatchToken(Tokens, "eth", AddressFamily.Evm, "0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE", 18)?.AssetId);
        // EVM contracts in any case.
        Assert.Equal("nep141:eth-usdc", IntentsDepositService.MatchToken(Tokens, "eth", AddressFamily.Evm, "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48", 6)?.AssetId);
        Assert.Equal("nep141:sol.omft.near", IntentsDepositService.MatchToken(Tokens, "sol", AddressFamily.Solana, "11111111111111111111111111111111", 9)?.AssetId);
        Assert.Equal("nep141:sol-usdc", IntentsDepositService.MatchToken(Tokens, "sol", AddressFamily.Solana, SolUsdc, 6)?.AssetId);
        Assert.Equal("nep141:tron.omft.near", IntentsDepositService.MatchToken(Tokens, "tron", AddressFamily.Tron, "T9yD14Nj9j7xAB4dbGeiX9h8unkKHxuWwb", 6)?.AssetId);
        // A deprecated listing is never used.
        Assert.Equal("nep141:bnb.omdep.near", IntentsDepositService.MatchToken(Tokens, "bsc", AddressFamily.Evm, "0x0000000000000000000000000000000000000000", 18)?.AssetId);

        // Solana addresses are case-sensitive: another case is another token.
        Assert.Null(IntentsDepositService.MatchToken(Tokens, "sol", AddressFamily.Solana, SolUsdc.ToLowerInvariant(), 6));
        // Same token, other decimals: an amount would be read in the wrong unit.
        Assert.Null(IntentsDepositService.MatchToken(Tokens, "sol", AddressFamily.Solana, SolUsdc, 9));
        // Not listed on that chain, or listed twice.
        Assert.Null(IntentsDepositService.MatchToken(Tokens, "eth", AddressFamily.Evm, "0x2222222222222222222222222222222222222222", 18));
        Assert.Null(IntentsDepositService.MatchToken(Tokens, "base", AddressFamily.Evm, "0x1111111111111111111111111111111111111111", 18));
        Assert.Null(IntentsDepositService.MatchToken(Tokens, "pol", AddressFamily.Evm, "0x0000000000000000000000000000000000000000", 18));
    }

    [Fact]
    public void KnowsEachTokenChainsAddressFamily()
    {
        Assert.Equal(AddressFamily.Solana, Solana.Family);
        Assert.Equal(AddressFamily.Tron, SwapSourceChain.Find("TRON")!.Family);
        Assert.Equal(AddressFamily.Evm, Ethereum.Family);
        Assert.Equal(AddressFamily.Evm, Bsc.Family);
    }

    [Fact]
    public async Task PricesTheBtcDueToTheInvoiceWithoutMakingADeposit()
    {
        var (service, fake, saved) = Create();
        var quote = await service.QuoteWallet(Config(), "store", Solana, SolUsdc, 6, SolWallet, BtcAddress, 0.001m, CancellationToken.None);

        // 0.001 BTC plus the store's 1% buffer, in satoshis.
        Assert.Equal(101_000, quote.TargetSats);
        Assert.Equal(new BigInteger(202_000), quote.AmountIn);
        Assert.Equal(6, quote.Decimals);
        Assert.Equal(76.17m, quote.AmountInUsd);
        var request = Assert.Single(fake.Quotes);
        Assert.True(request["dry"]!.Value<bool>());
        Assert.Equal("EXACT_OUTPUT", request["swapType"]!.ToString());
        Assert.Equal("101000", request["amount"]!.ToString());
        Assert.Equal("nep141:sol-usdc", request["originAsset"]!.ToString());
        Assert.Equal(BtcAddress, request["recipient"]!.ToString());
        Assert.Equal(SolWallet, request["refundTo"]!.ToString());
        Assert.Empty(saved);

        // Asked again right away: the same price, without asking NEAR Intents again.
        await service.QuoteWallet(Config(), "store", Solana, SolUsdc, 6, SolWallet, BtcAddress, 0.001m, CancellationToken.None);
        Assert.Single(fake.Quotes);
    }

    [Fact]
    public async Task RefusesWhatCouldNotPayTheInvoice()
    {
        var (service, fake, _) = Create();
        // A wallet of another chain family, a token NEAR Intents does not take.
        await Assert.ThrowsAsync<IntentsWalletException>(() =>
            service.QuoteWallet(Config(), "store", Solana, SolUsdc, 6, EvmWallet, BtcAddress, 0.001m, CancellationToken.None));
        await Assert.ThrowsAsync<IntentsWalletException>(() =>
            service.QuoteWallet(Config(), "store", Solana, "JUPyiwrYJFskUPiHa7hkeR8VUtAeFoSYbKedZNsDvCN", 6, SolWallet, BtcAddress, 0.001m, CancellationToken.None));
        Assert.Empty(fake.Quotes);
        // A quote that would deliver less BTC than is due.
        fake.AmountOutOverride = 100_999;
        await Assert.ThrowsAsync<IntentsWalletException>(() =>
            service.QuoteWallet(Config(), "store", Solana, SolUsdc, 6, SolWallet, BtcAddress, 0.001m, CancellationToken.None));
        await Assert.ThrowsAsync<IntentsWalletException>(() => Deposit(service));
    }

    [Fact]
    public async Task MakesADepositRefundingToTheCustomersWalletAndSavesItFirst()
    {
        var (service, fake, saved) = Create();
        var deposit = await Deposit(service);

        Assert.Equal("Dep1osit11111111111111111111111111111111", deposit.Address);
        Assert.Equal(new BigInteger(202_000), deposit.AmountIn);
        var request = Assert.Single(fake.Quotes);
        Assert.False(request["dry"]!.Value<bool>());
        Assert.Equal(SolWallet, request["refundTo"]!.ToString());
        Assert.Equal("ORIGIN_CHAIN", request["refundType"]!.ToString());
        Assert.Equal(BtcAddress, request["recipient"]!.ToString());

        var kept = Assert.Single(Assert.Single(saved));
        Assert.Equal(IntentsDepositService.WalletOption, kept.Option);
        Assert.Equal("nep141:sol-usdc", kept.AssetId);
        Assert.Equal("USDC", kept.Symbol);
        Assert.Equal(SolWallet, kept.RefundTo);
        Assert.Equal("SOLANA", kept.PaidFrom);
        Assert.Equal(deposit.Address, kept.Address);
        Assert.True(service.HasWalletDeposit("inv1", deposit.Address));
        Assert.False(service.HasWalletDeposit("inv2", deposit.Address));
    }

    [Fact]
    public async Task PressingPayAgainReusesTheDeposit()
    {
        var (service, fake, _) = Create();
        var first = await Deposit(service);
        var second = await Deposit(service);
        Assert.Equal(first.Address, second.Address);
        Assert.Single(fake.Quotes);

        // Two presses at once still make one deposit.
        var (other, otherFake, _) = Create();
        var both = await Task.WhenAll(Deposit(other), Deposit(other));
        Assert.Equal(both[0].Address, both[1].Address);
        Assert.Single(otherFake.Quotes);
    }

    [Fact]
    public async Task ANewAmountDueGetsANewDepositUpToALimit()
    {
        var (service, fake, _) = Create();
        var addresses = new HashSet<string>();
        for (var i = 0; i < 6; i++)
            addresses.Add((await Deposit(service, 0.001m + i * 0.0001m)).Address);
        Assert.Equal(6, addresses.Count);
        await Assert.ThrowsAsync<IntentsWalletException>(() => Deposit(service, 0.002m));
        Assert.Equal(6, fake.Quotes.Count);
    }

    [Fact]
    public async Task ADepositThatCouldNotBeSavedIsNeverHandedOut()
    {
        var (service, fake, _) = Create();
        service.WriteQuotes = (_, _, _) => throw new InvalidOperationException("database unavailable");
        await Assert.ThrowsAsync<IntentsWalletException>(() => Deposit(service));
        Assert.False(service.HasWalletDeposit("inv1", "Dep1osit11111111111111111111111111111111"));

        // Once saving works, a new deposit is made and handed out.
        var saved = new List<List<PersistedQuote>>();
        service.WriteQuotes = (_, _, quotes) => { saved.Add(quotes); return Task.CompletedTask; };
        var deposit = await Deposit(service);
        Assert.Equal("Dep2osit11111111111111111111111111111111", deposit.Address);
        Assert.Single(Assert.Single(saved));
    }

    [Fact]
    public async Task ADepositThatNeedsAMemoIsRefused()
    {
        var (service, fake, saved) = Create();
        fake.Memo = "12345";
        await Assert.ThrowsAsync<IntentsWalletException>(() => Deposit(service));
        Assert.Empty(saved);
    }

    [Fact]
    public async Task AnEvmPaymentNeedsAnEvmWalletAndGetsAnEvmDeposit()
    {
        var (service, _, saved) = Create();
        var deposit = await service.CreateWalletDeposit(Config(), "store", Ethereum.PaymentMethodId, "inv1", Ethereum, EthUsdc, 6, EvmWallet, BtcAddress,
            0.001m, DateTimeOffset.UtcNow.AddHours(1));
        Assert.StartsWith("0x", deposit.Address);
        Assert.Equal("ETHEREUM", Assert.Single(Assert.Single(saved)).PaidFrom);
        await Assert.ThrowsAsync<IntentsWalletException>(() => service.CreateWalletDeposit(Config(), "store", Ethereum.PaymentMethodId, "inv1", Ethereum,
            EthUsdc, 6, SolWallet, BtcAddress, 0.001m, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task ResumedWalletDepositsAreFollowedUntilSettled()
    {
        var (service, fake, saved) = Create();
        service.RestoreWallet("inv1", "store", Solana, Config(),
        [
            new PersistedQuote
            {
                Option = IntentsDepositService.WalletOption, AssetId = "nep141:sol-usdc", Symbol = "USDC", RefundTo = SolWallet, PaidFrom = "SOLANA",
                Network = "sol", Address = "Dep9osit11111111111111111111111111111111", AmountUnits = "202000", Decimals = 6, TargetSats = 101_000,
                Deadline = DateTimeOffset.UtcNow.AddHours(1)
            },
            // Saved by the exchange buttons: not a wallet payment.
            new PersistedQuote { Option = "USDC", Network = "sol", Address = "Exch1111111111111111111111111111111111", AmountUnits = "1", Decimals = 6 }
        ]);
        Assert.True(service.HasWalletDeposit("inv1", "Dep9osit11111111111111111111111111111111"));
        Assert.False(service.HasWalletDeposit("inv1", "Exch1111111111111111111111111111111111"));

        fake.Status = "SUCCESS";
        await service.FollowAll(CancellationToken.None);
        var kept = Assert.Single(saved[^1]);
        Assert.Equal("SUCCESS", kept.Status);
        Assert.Equal(SolWallet, kept.RefundTo);
        Assert.Contains("5solTx", kept.LoggedTxs);
    }

    [Fact]
    public void WalletDepositsReadBackWithBtcpaysSerializer()
    {
        var serializer = BlobSerializer.CreateSerializer().Serializer;
        var details = JObject.FromObject(new SwapToBtcPromptDetails
        {
            IntentsQuotes = [new PersistedQuote { Option = IntentsDepositService.WalletOption, AssetId = "nep141:sol-usdc", Symbol = "USDC", RefundTo = SolWallet, PaidFrom = "SOLANA" }]
        }, serializer);
        var back = Assert.Single(JObject.Parse(details.ToString()).ToObject<SwapToBtcPromptDetails>(serializer)!.IntentsQuotes!);
        Assert.Equal(("nep141:sol-usdc", "USDC", SolWallet, "SOLANA"), (back.AssetId, back.Symbol, back.RefundTo, back.PaidFrom));
    }

    [Fact]
    public void StoresSavedBeforeThisFeatureGetItOn()
    {
        var config = JObject.Parse("""{"integrator":"store"}""").ToObject<SwapToBtcPaymentMethodConfig>()!;
        Assert.True(config.IntentsForWallets);
        Assert.False(JObject.Parse("""{"intentsForWallets":false}""").ToObject<SwapToBtcPaymentMethodConfig>()!.IntentsForWallets);
    }

    [Fact]
    public void PriceChecksHaveTheirOwnBudget()
    {
        using var notes = new CheckoutNotes();
        for (var i = 0; i < CheckoutNotes.MaxRequestsPerCaller; i++)
            Assert.True(notes.AllowCaller("1.2.3.4"));
        Assert.False(notes.AllowCaller("1.2.3.4"));
        // Notes and deposits used up: price checks still go through, up to their own limit.
        for (var i = 0; i < CheckoutNotes.MaxQuotesPerCaller; i++)
            Assert.True(notes.AllowQuote("1.2.3.4"));
        Assert.False(notes.AllowQuote("1.2.3.4"));
    }
}
