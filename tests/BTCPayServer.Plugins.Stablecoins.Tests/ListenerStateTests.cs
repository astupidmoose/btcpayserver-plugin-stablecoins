using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Swap;
using NBitcoin.RPC;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class ListenerStateTests
{
    [Fact]
    public void ReorganizedPaymentGoesBackToProcessing()
    {
        // Settled at 1 confirmation, then the block is reorganized away.
        Assert.Equal((1, PaymentStatus.Settled), SwapToBtcListener.NextState(SpeedPolicy.MediumSpeed, true, 1, true, 6));
        Assert.Equal((0, PaymentStatus.Processing), SwapToBtcListener.NextState(SpeedPolicy.MediumSpeed, true, 0, true, 6));
    }

    [Fact]
    public void ReplacedPaymentStopsCounting()
    {
        Assert.Equal((0, PaymentStatus.Unaccounted), SwapToBtcListener.NextState(SpeedPolicy.HighSpeed, true, 0, false, 6));
        Assert.Equal((-1, PaymentStatus.Unaccounted), SwapToBtcListener.NextState(SpeedPolicy.HighSpeed, false, -1, false, 6));
        // Mined again after all: it counts again.
        Assert.Equal((1, PaymentStatus.Settled), SwapToBtcListener.NextState(SpeedPolicy.HighSpeed, true, 1, true, 6));
    }

    [Fact]
    public void ConfirmationsAreCappedAtTheTrackedMaximum()
    {
        Assert.Equal((6, PaymentStatus.Settled), SwapToBtcListener.NextState(SpeedPolicy.LowSpeed, false, 250, true, 6));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN, true)]
    [InlineData(false, RPCErrorCode.RPC_TRANSACTION_ERROR, false)] // a replacement was mined, or a double spend is in the mempool
    [InlineData(false, RPCErrorCode.RPC_TRANSACTION_REJECTED, false)] // replaced under RBF
    [InlineData(false, RPCErrorCode.RPC_IN_WARMUP, true)] // the node cannot tell: keep counting, as BTCPay does
    public void ReadsTheNodesBroadcastAnswerLikeBtcpay(bool success, RPCErrorCode? code, bool accounted)
    {
        Assert.Equal(accounted, SwapToBtcListener.IsAccounted(new BroadcastResult { Success = success, RPCCode = code }));
    }
}
