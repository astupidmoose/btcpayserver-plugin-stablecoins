using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Plugins.Stablecoins.Thorchain;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class ThorQuoteServiceTests
{
    const string BtcAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    static readonly SwapSourceChain Litecoin = SwapSourceChain.Find("LITECOIN")!;

    /// <summary>
    /// A THORNode stand-in: LTC is worth 1/1000 BTC, and a swap loses a fixed 0.0001 BTC outbound fee plus 1%.
    /// </summary>
    class FakeThornode(bool halted = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            string body;
            if (uri.AbsolutePath.EndsWith("/thorchain/inbound_addresses"))
                body = $$"""[{"chain":"LTC","address":"ltc1qvault","halted":{{(halted ? "true" : "false")}},"global_trading_paused":false,"chain_trading_paused":false}]""";
            else if (uri.AbsolutePath.EndsWith("/thorchain/pools"))
                body = """[{"asset":"BTC.BTC","status":"Available","asset_tor_price":"10000000000000"},{"asset":"LTC.LTC","status":"Available","asset_tor_price":"10000000000"}]""";
            else if (uri.AbsolutePath.EndsWith("/thorchain/quote/swap"))
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                var amountIn = long.Parse(q["amount"]!);
                var gross = amountIn / 1000;
                var outUnits = Math.Max(0, gross - gross / 100 - 10_000);
                body = $$"""
                {"inbound_address":"ltc1qvault","memo":"=:BTC.BTC:{{q["destination"]}}:0/1/0","expected_amount_out":"{{outUnits}}",
                 "expiry":{{DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()}},"recommended_min_amount_in":"1000000","total_swap_seconds":1800}
                """;
            }
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"code":5,"message":"not found"}""") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    static SwapToBtcPaymentMethodConfig Config(decimal buffer = 0m) => new()
    {
        Integrator = "my-store",
        BufferPercent = buffer,
        ThornodeUrl = "https://thornode.example.com/chain/thorchain_api"
    };

    [Fact]
    public async Task FindsTheInputThatCoversTheBtcDue()
    {
        var node = new FakeThornode();
        var service = new ThorQuoteService(new ThornodeClient(new HttpClient(node)));

        var deposit = await service.GetDeposit(Litecoin, Config(), BtcAddress, 0.01m, CancellationToken.None);

        Assert.True(deposit.ExpectedBtcOutUnits >= 1_000_000);
        // Not wildly over: within 1% of what is due.
        Assert.True(deposit.ExpectedBtcOutUnits < 1_010_000, $"{deposit.ExpectedBtcOutUnits}");
        Assert.Equal("ltc1qvault", deposit.InboundAddress);
        Assert.Equal($"=:BTC.BTC:{BtcAddress}:0/1/0", deposit.Memo);
        Assert.Equal("LTC", deposit.Ticker);
        Assert.Equal(ThornodeClient.FromUnits(deposit.AmountUnits), deposit.Amount);
        // The base URL's path is kept.
        Assert.All(node.Requests, u => Assert.StartsWith("/chain/thorchain_api/thorchain/", u.AbsolutePath));
        var quote = HttpUtility.ParseQueryString(node.Requests.Last().Query);
        Assert.Equal("LTC.LTC", quote["from_asset"]);
        Assert.Equal("BTC.BTC", quote["to_asset"]);
        Assert.Equal(BtcAddress, quote["destination"]);
        Assert.Equal("300", quote["liquidity_tolerance_bps"]);
        Assert.Null(quote["affiliate"]);
    }

    [Fact]
    public async Task AppliesTheBufferAndCachesUntilExpiry()
    {
        var node = new FakeThornode();
        var service = new ThorQuoteService(new ThornodeClient(new HttpClient(node)));

        var first = await service.GetDeposit(Litecoin, Config(1m), BtcAddress, 0.01m, CancellationToken.None);
        Assert.True(first.ExpectedBtcOutUnits >= 1_010_000);
        var calls = node.Requests.Count;
        var second = await service.GetDeposit(Litecoin, Config(1m), BtcAddress, 0.01m, CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(calls, node.Requests.Count);
    }

    [Fact]
    public async Task RefusesWhenTheChainIsHalted()
    {
        var service = new ThorQuoteService(new ThornodeClient(new HttpClient(new FakeThornode(halted: true))));
        var ex = await Assert.ThrowsAsync<ThornodeException>(() =>
            service.GetDeposit(Litecoin, Config(), BtcAddress, 0.01m, CancellationToken.None));
        Assert.Contains("paused", ex.Message);
    }

    [Theory]
    [InlineData(1_234_567, 8, "0.01234567")]
    [InlineData(1_234_567, 6, "0.012346")]
    public void RoundsUpToWhatTheCoinCanSend(long units, int decimals, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ThorQuoteService.RoundForDisplay(units, decimals));
    }

    [Fact]
    public void FallsBackToThePublicGatewayForBadUrls()
    {
        Assert.Equal(ThornodeClient.DefaultUrl, ThorQuoteService.ThornodeUrl(new SwapToBtcPaymentMethodConfig { ThornodeUrl = "http://insecure" }).ToString().TrimEnd('/'));
        Assert.Equal("https://gateway.liquify.com/chain/thorchain_api/thorchain/pools",
            ThornodeClient.Combine(new Uri(ThornodeClient.DefaultUrl + "/"), "/thorchain/pools").ToString());
    }
}
