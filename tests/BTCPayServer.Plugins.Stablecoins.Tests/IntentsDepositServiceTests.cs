using System.Net;
using System.Numerics;
using System.Text;
using System.Web;
using BTCPayServer.Plugins.Stablecoins.Intents;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class IntentsDepositServiceTests
{
    const string BtcAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    static readonly SwapSourceChain Usdc = SwapSourceChain.Find("USDC")!;

    /// <summary>1Click: BTC at 60,000 USD, every quote gets a new deposit address, status is whatever the test sets.</summary>
    class FakeOneClick(long? amountOutOverride = null) : HttpMessageHandler
    {
        public List<JObject> Quotes { get; } = new();
        public string Status { get; set; } = "PENDING_DEPOSIT";
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path == "/v0/tokens")
                json = """
                [{"assetId":"nep141:btc.omft.near","decimals":8,"blockchain":"btc","symbol":"BTC"},
                 {"assetId":"nep141:pol-usdc","decimals":6,"blockchain":"pol","symbol":"USDC"},
                 {"assetId":"nep141:pol-usdce","decimals":6,"blockchain":"pol","symbol":"USDC.e"},
                 {"assetId":"nep141:bsc-usdc","decimals":18,"blockchain":"bsc","symbol":"USDC"},
                 {"assetId":"nep141:eth-usdc-a","decimals":6,"blockchain":"eth","symbol":"USDC"},
                 {"assetId":"nep141:eth-usdc-b","decimals":6,"blockchain":"eth","symbol":"USDC"}]
                """;
            else if (path == "/v0/quote")
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Quotes.Add(body);
                var sats = long.Parse(body["amount"]!.ToString());
                var dec = body["originAsset"]!.ToString().Contains("bsc") ? 18 : 6;
                var amountIn = new BigInteger(sats) * 600 * BigInteger.Pow(10, dec) / 1_000_000 * 1005 / 1000;
                json = new JObject
                {
                    ["correlationId"] = "c1",
                    ["signature"] = "sig",
                    ["quote"] = new JObject
                    {
                        ["depositAddress"] = "0xdeposit" + Quotes.Count,
                        ["amountIn"] = amountIn.ToString(),
                        ["amountOut"] = (amountOutOverride ?? sats).ToString(),
                        ["deadline"] = body["deadline"],
                        ["timeEstimate"] = 120
                    }
                }.ToString();
            }
            else if (path == "/v0/status")
                json = new JObject { ["status"] = Status, ["swapDetails"] = new JObject { ["depositedAmount"] = "10000000" } }.ToString();
            else
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static (IntentsDepositService Service, FakeOneClick Fake) Create(FakeOneClick? fake = null)
    {
        fake ??= new FakeOneClick();
        return (new IntentsDepositService(new OneClickClient(new HttpClient(fake)), null!, NullLogger<IntentsDepositService>.Instance), fake);
    }

    static SwapToBtcPaymentMethodConfig Config() => new()
    {
        IntentsApiKey = "jwt",
        IntentsUrl = "https://oneclick.example.com",
        RefundEvm = "0x2527D02599Ba641c19FEa793cD0F167589a0f10D"
    };

    static async Task<IntentsStatus> Settle(IntentsDepositService service, string invoiceId = "inv1", decimal btcDue = 0.001m)
    {
        service.Get(Usdc, Config(), invoiceId, BtcAddress, btcDue, DateTimeOffset.UtcNow.AddHours(1));
        await service.Pending(invoiceId, Usdc);
        return service.Get(Usdc, Config(), invoiceId, BtcAddress, btcDue, DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public void OffersOnlyNetworksWithARefundAddress()
    {
        var networks = IntentsDepositService.Networks(Usdc, Config()).Select(n => n.Code).ToList();
        Assert.Contains("pol", networks);
        Assert.DoesNotContain("sol", networks);
        Assert.Empty(IntentsDepositService.Networks(SwapSourceChain.Find("USDT")!, new SwapToBtcPaymentMethodConfig { RefundTron = "" }));
    }

    [Fact]
    public async Task WaitsForANetworkThenQuotesTheBtcDueToTheInvoiceAddress()
    {
        var (service, fake) = Create();
        Assert.Equal(IntentsState.ChooseNetwork, (await Settle(service)).State);
        Assert.Empty(fake.Quotes);

        Assert.True(service.Select(Usdc, Config(), "inv1", "pol"));
        var status = await Settle(service);

        Assert.Equal(IntentsState.Ready, status.State);
        Assert.Equal("0xdeposit1", status.Payment!.Address);
        Assert.Equal(60.3m, status.Payment.Amount);
        var quote = fake.Quotes.Single();
        Assert.Equal("EXACT_OUTPUT", quote["swapType"]!.ToString());
        Assert.Equal("nep141:pol-usdc", quote["originAsset"]!.ToString());
        Assert.Equal("nep141:btc.omft.near", quote["destinationAsset"]!.ToString());
        Assert.Equal("100000", quote["amount"]!.ToString());
        Assert.Equal(BtcAddress, quote["recipient"]!.ToString());
        Assert.Equal(Config().RefundEvm, quote["refundTo"]!.ToString());
        Assert.Equal("Bearer jwt", fake.Authorization);
    }

    [Fact]
    public async Task RefusesNetworksItCannotRefundTo() =>
        Assert.False(Create().Service.Select(Usdc, Config(), "inv2", "sol"));

    [Fact]
    public async Task RefusesAQuoteThatPaysLessThanDue()
    {
        var (service, _) = Create(new FakeOneClick(amountOutOverride: 99_000));
        service.Select(Usdc, Config(), "inv3", "pol");
        Assert.Equal(IntentsState.Failed, (await Settle(service, "inv3")).State);
    }

    [Fact]
    public async Task AmbiguousTokensAreNotOffered()
    {
        var (service, fake) = Create();
        service.Select(Usdc, Config(), "inv4", "eth");
        Assert.Equal(IntentsState.Failed, (await Settle(service, "inv4")).State);
        Assert.Empty(fake.Quotes);
    }

    [Fact]
    public async Task FollowsTheDepositAndKeepsItWhenTheCustomerSwitches()
    {
        var (service, fake) = Create();
        service.Now = () => DateTimeOffset.UtcNow;
        service.Select(Usdc, Config(), "inv5", "pol");
        await Settle(service, "inv5");
        fake.Status = "PROCESSING";
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        var status = await Settle(service, "inv5");
        Assert.Equal(IntentsState.Received, status.State);
        // A deposit on its way is not dropped for another network.
        Assert.False(service.Select(Usdc, Config(), "inv5", "bsc"));
    }

    [Fact]
    public async Task EighteenDecimalTokensRoundUp()
    {
        var (service, _) = Create();
        service.Select(Usdc, Config(), "inv6", "bsc");
        var status = await Settle(service, "inv6");
        Assert.Equal(18, status.Payment!.Decimals);
        Assert.Equal(60.3m, status.Payment.Amount);
        Assert.Equal(new BigInteger(1), BigInteger.Parse("1"));
        Assert.Equal(0.00000001m, IntentsDepositService.ToDecimal(BigInteger.One, 18));
    }
}
