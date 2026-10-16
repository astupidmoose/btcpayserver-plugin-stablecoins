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
        var ids = SwapSourceChain.All.Select(c => c.PaymentMethodId.ToString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // The WooCommerce plugin sends the id back with every '-' turned into '_', so the ids must not contain '-'.
        Assert.All(ids, id => Assert.Matches(new Regex("^[A-Z0-9_]+$"), id));
        Assert.Equal("LIFI_ETHEREUM", SwapSourceChain.Find("ethereum")!.PaymentMethodId.ToString());
        Assert.Equal("THOR_LITECOIN", SwapSourceChain.Find("litecoin")!.PaymentMethodId.ToString());
        // What BTCPay's Greenfield API does with the id WooCommerce sends.
        Assert.All(SwapSourceChain.All, c =>
        {
            Assert.True(BTCPayServer.Payments.PaymentMethodId.TryParse(c.PaymentMethodId.ToString().Replace('-', '_'), out var parsed));
            Assert.Equal(c.PaymentMethodId, parsed);
        });
        Assert.Same(SwapSourceChain.Find("BASE"), SwapSourceChain.Find(SwapSourceChain.Find("BASE")!.PaymentMethodId));
        Assert.Null(SwapSourceChain.Find("BTC"));
    }

    [Fact]
    public void UsesLifiChainIds()
    {
        Assert.Equal(1, SwapSourceChain.Find("ETHEREUM")!.LifiChainId);
        Assert.Equal(8453, SwapSourceChain.Find("BASE")!.LifiChainId);
        Assert.Equal(1151111081099710, SwapSourceChain.Find("SOLANA")!.LifiChainId);
        Assert.Equal(728126428, SwapSourceChain.Find("TRON")!.LifiChainId);
    }

    [Fact]
    public void ThorchainCoversNativeCoinsButNotBitcoinCash()
    {
        var thor = SwapSourceChain.All.Where(c => c.Provider == SwapProvider.Thorchain).ToList();
        Assert.Equal(["LTC.LTC", "DOGE.DOGE", "ZEC.ZEC", "GAIA.ATOM", "XRP.XRP"], thor.Select(c => c.ThorAsset));
        Assert.DoesNotContain(SwapSourceChain.All, c => c.ThorAsset?.StartsWith("BCH.") is true || c.Code.Contains("BCH"));
        Assert.All(thor, c => Assert.False(string.IsNullOrEmpty(c.Ticker)));
        Assert.Equal("GAIA", SwapSourceChain.Find("COSMOS")!.ThorChain);
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
    [InlineData(null, null, 0, 300, true)]
    [InlineData("https://thornode.example.com", "my-store", 0, 300, true)]
    [InlineData("http://thornode.example.com", null, 0, 300, false)]
    [InlineData(null, "has space", 0, 300, false)]
    [InlineData(null, "my-store", 1001, 300, false)]
    [InlineData(null, null, 0, 5, false)]
    public void ValidatesThorchainSettings(string? url, string? affiliate, int bps, int tolerance, bool valid)
    {
        var config = new SwapToBtcPaymentMethodConfig
        {
            Integrator = "my-store", ThornodeUrl = url, ThorAffiliate = affiliate, ThorAffiliateBps = bps, ThorToleranceBps = tolerance
        };
        Assert.Equal(valid, !SwapToBtcPaymentMethodHandler.Validate(config).Any());
    }

    [Fact]
    public void ReadsThorchainOutboundMemo()
    {
        var inbound = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";
        var tx = Network.Main.CreateTransaction();
        tx.Outputs.Add(Money.Coins(0.01m), new Key().GetScriptPubKey(ScriptPubKeyType.Segwit));
        tx.Outputs.Add(Money.Zero, TxNullDataTemplate.Instance.GenerateScriptPubKey(System.Text.Encoding.ASCII.GetBytes($"OUT:{inbound}")));
        Assert.Equal(inbound, SwapToBtcListener.ThorOutboundTxId(tx));

        var plain = Network.Main.CreateTransaction();
        plain.Outputs.Add(Money.Coins(0.01m), new Key().GetScriptPubKey(ScriptPubKeyType.Segwit));
        Assert.Null(SwapToBtcListener.ThorOutboundTxId(plain));
        Assert.Null(SwapToBtcListener.ThorOutboundTxId(null));
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

    [Fact]
    public void LinksToTheRightExplorer()
    {
        Assert.StartsWith("https://scan.li.fi/tx/", UISwapCheckoutController.ExplorerLink(SwapSourceChain.Find("BASE")!, "0xabc"));
        Assert.StartsWith("https://runescan.io/tx/", UISwapCheckoutController.ExplorerLink(SwapSourceChain.Find("LITECOIN")!, "abc"));
    }
}
