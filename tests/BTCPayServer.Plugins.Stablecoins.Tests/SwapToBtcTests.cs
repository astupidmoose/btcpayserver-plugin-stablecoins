using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Swap;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class SwapToBtcTests
{
    [Fact]
    public void EveryChainHasItsOwnWooCommerceSafePaymentMethodId()
    {
        var ids = SwapSourceChain.All.Select(c => c.PaymentMethodId.ToString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // The WooCommerce plugin turns the id into a PHP class name: letters, digits, '-' and '_' only.
        Assert.All(ids, id => Assert.Matches(new Regex("^[A-Z0-9_-]+$"), id));
        Assert.Equal("LIFI-ETHEREUM", SwapSourceChain.Find("ethereum")!.PaymentMethodId.ToString());
        Assert.Same(SwapSourceChain.Find("BASE"), SwapSourceChain.Find(SwapSourceChain.Find("BASE")!.PaymentMethodId));
        Assert.Null(SwapSourceChain.Find("BTC"));
    }

    [Fact]
    public void UsesLifiChainIds()
    {
        Assert.Equal(1, SwapSourceChain.Find("ETHEREUM")!.LifiChainId);
        Assert.Equal(8453, SwapSourceChain.Find("BASE")!.LifiChainId);
        Assert.Equal(1151111081099710, SwapSourceChain.Find("SOLANA")!.LifiChainId);
        Assert.False(SwapSourceChain.Find("SOLANA")!.IsEvm);
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
    [InlineData("coincards", 0, 0.5, true)]
    [InlineData("btcpay-stablecoins", 0.005, 0, true)]
    [InlineData("", 0, 0.5, false)]
    [InlineData("has space", 0, 0.5, false)]
    [InlineData("this-name-is-far-too-long", 0, 0.5, false)]
    [InlineData("coincards", 0.1, 0.5, false)]
    [InlineData("coincards", -0.01, 0.5, false)]
    [InlineData("coincards", 0, 6, false)]
    public void ValidatesSettings(string integrator, double fee, double buffer, bool valid)
    {
        var config = new SwapToBtcPaymentMethodConfig { Integrator = integrator, Fee = (decimal)fee, BufferPercent = (decimal)buffer };
        Assert.Equal(valid, !SwapToBtcPaymentMethodHandler.Validate(config).Any());
    }
}
