using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Controllers;
using BTCPayServer.Plugins.Stablecoins.Swap;
using NBitcoin;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class SwapToBtcTests
{
    [Fact]
    public void EveryChainHasItsOwnWooCommerceSafePaymentMethodId()
    {
        var ids = SwapSourceChain.PaymentMethods.Select(c => c.PaymentMethodId.ToString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // The WooCommerce plugin sends the id back with every '-' turned into '_', so the ids must not contain '-'.
        Assert.All(ids, id => Assert.Matches(new Regex("^[A-Z0-9_]+$"), id));
        Assert.Equal("LIFI_ETHEREUM", SwapSourceChain.Find("ethereum")!.PaymentMethodId.ToString());
        // What BTCPay's Greenfield API does with the id WooCommerce sends.
        Assert.All(SwapSourceChain.PaymentMethods, c =>
        {
            Assert.True(BTCPayServer.Payments.PaymentMethodId.TryParse(c.PaymentMethodId.ToString().Replace('-', '_'), out var parsed));
            Assert.Equal(c.PaymentMethodId, parsed);
        });
        Assert.Same(SwapSourceChain.Find("BASE"), SwapSourceChain.Find(SwapSourceChain.Find("BASE")!.PaymentMethodId));
        Assert.Null(SwapSourceChain.Find("BTC"));
    }

    [Fact]
    public void OnlyTokenChainsAndMoneroAreOffered()
    {
        Assert.All(SwapSourceChain.PaymentMethods, c => Assert.Contains(c.Provider, new[] { SwapProvider.Lifi, SwapProvider.Pegasus }));
        Assert.Equal(SwapSourceChain.All.Length, SwapSourceChain.All.Select(c => c.Code).Distinct().Count());
        Assert.Equal(SwapSourceChain.All.Length, SwapSourceChain.All.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void UsesLifiChainIds()
    {
        Assert.Equal(1, SwapSourceChain.Find("ETHEREUM")!.LifiChainId);
        Assert.Equal(8453, SwapSourceChain.Find("BASE")!.LifiChainId);
        Assert.Equal(1151111081099710, SwapSourceChain.Find("SOLANA")!.LifiChainId);
        Assert.Equal(728126428, SwapSourceChain.Find("TRON")!.LifiChainId);
    }

    [Theory]
    [InlineData(SpeedPolicy.HighSpeed, false, 0, PaymentStatus.Settled)]
    [InlineData(SpeedPolicy.HighSpeed, true, 0, PaymentStatus.Processing)]
    [InlineData(SpeedPolicy.HighSpeed, true, 1, PaymentStatus.Settled)]
    [InlineData(SpeedPolicy.MediumSpeed, false, 0, PaymentStatus.Processing)]
    [InlineData(SpeedPolicy.MediumSpeed, false, 1, PaymentStatus.Settled)]
    [InlineData(SpeedPolicy.LowMediumSpeed, false, 1, PaymentStatus.Processing)]
    [InlineData(SpeedPolicy.LowMediumSpeed, false, 2, PaymentStatus.Settled)]
    [InlineData(SpeedPolicy.LowSpeed, false, 5, PaymentStatus.Processing)]
    [InlineData(SpeedPolicy.LowSpeed, false, 6, PaymentStatus.Settled)]
    public void FollowsBtcPayConfirmationPolicy(SpeedPolicy policy, bool rbf, long confirmations, PaymentStatus expected)
    {
        var data = new SwapToBtcPaymentData { RBF = rbf, ConfirmationCount = confirmations };
        Assert.Equal(expected, SwapToBtcListener.GetStatus(policy, data));
    }

    [Theory]
    [InlineData("my-store", 0, 0.5, true)]
    [InlineData("btcpay-stablecoins", 0.005, 0, true)]
    [InlineData("", 0, 0.5, false)]
    [InlineData("has space", 0, 0.5, false)]
    [InlineData("a-name-that-is-longer-than-sixty-four-characters-is-not-accepted-by-lifi", 0, 0.5, false)]
    [InlineData("my-store", 0.1, 0.5, false)]
    [InlineData("my-store", -0.01, 0.5, false)]
    [InlineData("my-store", 0, 6, false)]
    public void ValidatesSettings(string integrator, double fee, double buffer, bool valid)
    {
        var config = new SwapToBtcPaymentMethodConfig { Integrator = integrator, Fee = (decimal)fee, BufferPercent = (decimal)buffer };
        Assert.Equal(valid, !SwapToBtcPaymentMethodHandler.Validate(config).Any());
    }

    [Theory]
    [InlineData("0x5f2a3c0d8a4b1e6f7c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d", true)]
    [InlineData("5VERv8NMvzbJMEkV8xnrLkEaWRtSz9CosKDYjCJjBRnbJLgp8uirBgmQpjKhoR4tjF3ZpRzrFmBV6UjKdiSZkQUW", true)]
    [InlineData("short", false)]
    [InlineData("<script>alert(1)</script>0000000000000000000000000", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyTransactionIds(string? txHash, bool expected)
    {
        Assert.Equal(expected, UISwapCheckoutController.IsTransactionId(txHash));
    }

    [Theory]
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7", true)]
    [InlineData("7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU", true)]
    [InlineData("TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", true)]
    [InlineData("0x5290<b>", false)]
    [InlineData("short", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyWalletAddresses(string? address, bool expected)
    {
        Assert.Equal(expected, UISwapCheckoutController.IsWalletAddress(address));
    }

    [Fact]
    public void ReadsTheDepositAndPayoutTransactionsFromNearIntents()
    {
        var status = Newtonsoft.Json.JsonConvert.DeserializeObject<BTCPayServer.Plugins.Stablecoins.Intents.OneClickStatus>(
            """{"status":"SUCCESS","swapDetails":{"originChainTxHashes":[{"hash":"0xabc","explorerUrl":"https://basescan.org/tx/0xabc"}],"destinationChainTxHashes":[{"hash":"f00d","explorerUrl":""}]}}""")!;
        Assert.Equal("0xabc", status.SwapDetails!.OriginChainTxHashes!.Single().Hash);
        Assert.Equal("f00d", status.SwapDetails.DestinationChainTxHashes!.Single().Hash);
    }

    [Fact]
    public void LinksToTheRightExplorer()
    {
        Assert.StartsWith("https://scan.li.fi/tx/", UISwapCheckoutController.ExplorerLink(SwapSourceChain.Find("BASE")!, "0xabc"));
    }

    [Fact]
    public void ExchangeOptionsAreNotPaymentMethods()
    {
        // WooCommerce lists every payment method as a gateway: USDC and USDT from an exchange live inside the token checkouts.
        Assert.DoesNotContain(SwapSourceChain.PaymentMethods, c => c.Provider == SwapProvider.Intents);
        Assert.Equal(new[] { "USDC", "USDT" }, SwapSourceChain.ExchangeOptions.Select(c => c.Code));
        Assert.Null(SwapSourceChain.Find(BTCPayServer.Payments.PaymentMethodId.Parse("NEAR_USDC")));

        var config = new SwapToBtcPaymentMethodConfig { ExchangeOptions = ["USDC", "USDT"], RefundTron = "TXYZ" };
        // Only USDT can be sent on Tron, the only network with a refund address.
        Assert.Equal(new[] { "USDT" }, config.EnabledExchangeOptions().Select(c => c.Code));
        config.RefundEvm = "0x" + new string('a', 40);
        Assert.Equal(new[] { "USDC", "USDT" }, config.EnabledExchangeOptions().Select(c => c.Code));
        // Each token checkout offers only what NEAR Intents takes on that same chain.
        Assert.Equal(new[] { "USDC" }, config.ExchangeOptionsFor(SwapSourceChain.Find("BASE")!).Select(c => c.Code));
        Assert.Equal(new[] { "USDC", "USDT" }, config.ExchangeOptionsFor(SwapSourceChain.Find("ETHEREUM")!).Select(c => c.Code));
        Assert.Equal(new[] { "USDT" }, config.ExchangeOptionsFor(SwapSourceChain.Find("TRON")!).Select(c => c.Code));
        Assert.Empty(config.ExchangeOptionsFor(SwapSourceChain.Find("SOLANA")!)); // no Solana refund address
        Assert.All(SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Lifi), c =>
            Assert.Contains(SwapSourceChain.ExchangeOptions.SelectMany(o => o.Networks), n => n.Code == c.IntentsNetworkCode));
        config.ExchangeOptions = [];
        Assert.Empty(config.EnabledExchangeOptions());
    }
}
