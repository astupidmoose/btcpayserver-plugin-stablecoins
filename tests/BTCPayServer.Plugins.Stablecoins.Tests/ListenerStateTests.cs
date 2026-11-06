using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services.Invoices;
using NBitcoin.RPC;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class ListenerStateTests
{
    [Fact]
    public void ReplacedPayoutDoesNotConfirmASwap()
    {
        var payments = new[]
        {
            new PaymentEntity { Id = "AA11-0", Status = PaymentStatus.Unaccounted, Value = 0.001m },
            new PaymentEntity { Id = "bb22-1", Status = PaymentStatus.Processing, Value = 0.00012345m },
            new PaymentEntity { Id = "cc33-0", Status = PaymentStatus.Settled, Value = 0.5m },
        };
        Assert.Equal(new HashSet<string> { "bb22", "bb22:12345", "cc33", "cc33:50000000" }, SourceVerifier.AccountedTxIds(payments));
    }

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

    [Theory]
    [InlineData(1, null, PaymentStatus.Unaccounted, true)] // mined: counts whatever the node says
    [InlineData(0, false, PaymentStatus.Processing, false)] // replaced
    [InlineData(0, true, PaymentStatus.Unaccounted, true)] // broadcastable again
    [InlineData(0, null, PaymentStatus.Unaccounted, false)] // node unreachable: stays replaced
    [InlineData(-1, null, PaymentStatus.Processing, true)] // node unreachable: keeps counting, as in BTCPay
    public void KeepsTheCurrentStateWhenTheNodeCannotBeAsked(long confirmations, bool? broadcastable, PaymentStatus current, bool accounted)
    {
        Assert.Equal(accounted, SwapToBtcListener.IsAccounted(confirmations, broadcastable, current));
    }
}
