using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Usdc.Services;
using BTCPayServer.Plugins.Usdc.Solana;

namespace BTCPayServer.Plugins.Usdc.Tests;

public class SolanaPayTests
{
    [Fact]
    public void NewReferenceIsAValidPublicKeyAndUnique()
    {
        var a = SolanaPay.NewReference();
        var b = SolanaPay.NewReference();
        Assert.True(SolanaPay.IsPublicKey(a));
        Assert.True(SolanaPay.IsPublicKey(b));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(SolanaChainConfiguration.MainnetUsdcMint, true)]
    [InlineData("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM", true)]
    [InlineData("0OIl", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", false)]
    public void ValidatesPublicKeys(string? value, bool expected)
    {
        Assert.Equal(expected, SolanaPay.IsPublicKey(value));
    }

    [Theory]
    [InlineData("12.5", "12.5")]
    [InlineData("1", "1")]
    [InlineData("0.0000001", "0.000001")] // rounds up to USDC's 6 decimals, never under-asks
    [InlineData("10.1234561", "10.123457")]
    [InlineData("100.000000", "100")]
    public void FormatsAmountsPerSpec(string input, string expected)
    {
        Assert.Equal(expected, SolanaPay.FormatAmount(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BuildsTransferRequestUrl()
    {
        var url = SolanaPay.BuildTransferUrl(
            "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM", 25.5m, SolanaChainConfiguration.MainnetUsdcMint,
            "82ZJ7nbGpixjeDCmEhUcmwXYfvurzAgGdtSMuHnUgyny", label: "MyStore", message: "Invoice ABC 1");
        Assert.Equal(
            "solana:9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM?amount=25.5" +
            "&spl-token=EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v" +
            "&reference=82ZJ7nbGpixjeDCmEhUcmwXYfvurzAgGdtSMuHnUgyny&label=MyStore&message=Invoice%20ABC%201", url);
    }

    [Theory]
    [InlineData("finalized", SpeedPolicy.LowSpeed, PaymentStatus.Settled)]
    [InlineData("finalized", SpeedPolicy.HighSpeed, PaymentStatus.Settled)]
    [InlineData("confirmed", SpeedPolicy.HighSpeed, PaymentStatus.Settled)]
    [InlineData("confirmed", SpeedPolicy.MediumSpeed, PaymentStatus.Processing)]
    [InlineData("processed", SpeedPolicy.HighSpeed, PaymentStatus.Processing)]
    [InlineData(null, SpeedPolicy.HighSpeed, PaymentStatus.Processing)]
    public void MapsCommitmentToPaymentStatus(string? commitment, SpeedPolicy policy, PaymentStatus expected)
    {
        Assert.Equal(expected, SolanaUsdcListener.GetStatus(commitment, policy));
    }

    [Fact]
    public void ChainDefaultsFollowNetwork()
    {
        var main = SolanaChainConfiguration.Create(true, null, null);
        Assert.Equal("mainnet-beta", main.Cluster);
        Assert.Equal(SolanaChainConfiguration.MainnetUsdcMint, main.UsdcMint);
        Assert.Equal("https://explorer.solana.com/tx/{0}", main.TransactionLinkFormat);
        var dev = SolanaChainConfiguration.Create(false, new Uri("https://rpc.example/"), 3);
        Assert.Equal("devnet", dev.Cluster);
        Assert.Equal(SolanaChainConfiguration.DevnetUsdcMint, dev.UsdcMint);
        Assert.Equal(new Uri("https://rpc.example/"), dev.RpcUri);
        Assert.Equal(TimeSpan.FromSeconds(3), dev.PollInterval);
    }
}

public class RateRuleTests
{
    [Fact]
    public void DefaultRateRulesResolveUsdcToStoreCurrency()
    {
        var rules = new DefaultRules(UsdcPlugin.DefaultRateRules).Rules;
        var rule = rules.GetRuleFor(new BTCPayServer.Rating.CurrencyPair("USDC", "CAD"));
        var text = rule.ToString();
        Assert.Contains("kraken(USDC_USD)", text);
        Assert.Contains("kraken(BTC_USD)", text);
        Assert.Contains("BTC_CAD", text);
    }
}
