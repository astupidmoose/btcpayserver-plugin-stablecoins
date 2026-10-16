using System.Net;
using System.Text;
using System.Web;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Plugins.Stablecoins.Thorchain;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class ThorDepositServiceTests
{
    const string BtcAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    static readonly SwapSourceChain Litecoin = SwapSourceChain.Find("LITECOIN")!;

    /// <summary>
    /// THORNode plus the memoless API: LTC is worth 1/1000 BTC; registration hands out reference 00042;
    /// the first preflight fails as if the registration were not in a block yet.
    /// </summary>
    class FakeThorchain(string? registeredMemoSuffix = null, int usesAfterPreflights = int.MaxValue, string? registeredMemo = null) : HttpMessageHandler
    {
        public List<(string Path, string? Body)> Requests { get; } = new();
        int _preflights;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((uri.AbsolutePath, body));
            var status = HttpStatusCode.OK;
            string json;
            if (uri.AbsolutePath.EndsWith("/thorchain/inbound_addresses"))
                json = """[{"chain":"LTC","address":"ltc1qvault","halted":false,"global_trading_paused":false,"chain_trading_paused":false}]""";
            else if (uri.AbsolutePath.EndsWith("/thorchain/pools"))
                json = """[{"asset":"BTC.BTC","status":"Available","asset_tor_price":"10000000000000"},{"asset":"LTC.LTC","status":"Available","asset_tor_price":"10000000000"}]""";
            else if (uri.AbsolutePath.EndsWith("/thorchain/quote/swap"))
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                var gross = long.Parse(q["amount"]!) / 1000;
                var outUnits = Math.Max(0, gross - gross / 100 - 10_000);
                json = $$"""
                {"inbound_address":"ltc1qvault","memo":"=:b:{{q["destination"]}}:0/1/0","expected_amount_out":"{{outUnits}}",
                 "expiry":{{DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()}},"recommended_min_amount_in":"1000000","total_swap_seconds":600}
                """;
            }
            else if (uri.AbsolutePath.EndsWith("/api/v1/register"))
            {
                var req = JObject.Parse(body!);
                json = new JObject
                {
                    ["success"] = true,
                    ["internal_api_id"] = "reg-1",
                    ["asset"] = req["asset"],
                    ["memo"] = registeredMemo ?? req["memo"] + (registeredMemoSuffix ?? ""),
                    ["reference"] = "00042",
                    ["reference_length"] = 5,
                    ["height"] = 100,
                    ["decimals"] = 8,
                    ["minimum_amount_to_send"] = "0.00010042",
                    ["suggested_in_asset_amount"] = AmountEnding42(req["requested_in_asset_amount"]!.ToString())
                }.ToString();
                status = HttpStatusCode.Created;
            }
            else if (uri.AbsolutePath.EndsWith("/api/v1/preflight"))
            {
                _preflights++;
                if (_preflights == 1)
                {
                    json = """{"success":false,"error":{"code":"PREFLIGHT_FAILED","message":"reference memo not found"}}""";
                    status = HttpStatusCode.BadRequest;
                }
                else
                    json = new JObject
                    {
                        ["success"] = true,
                        ["data"] = new JObject
                        {
                            ["current_uses"] = _preflights > usesAfterPreflights ? 1 : 0,
                            ["max_uses"] = 1,
                            ["inbound_address"] = "ltc1qvault",
                            ["seconds_remaining"] = 21000
                        }
                    }.ToString();
            }
            else
            {
                json = """{"message":"not found"}""";
                status = HttpStatusCode.NotFound;
            }
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        static string AmountEnding42(string requested)
        {
            var units = (long)decimal.Ceiling(decimal.Parse(requested, System.Globalization.CultureInfo.InvariantCulture) * 100_000_000m);
            var c = units - units % 100_000 + 42;
            if (c < units) c += 100_000;
            return (c / 100_000_000m).ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    static (ThorDepositService Service, FakeThorchain Fake) Create(FakeThorchain? fake = null)
    {
        fake ??= new FakeThorchain();
        var http = new HttpClient(fake);
        var service = new ThorDepositService(
            new ThorQuoteService(new ThornodeClient(http)),
            new MemolessClient(http),
            null!,
            NullLogger<ThorDepositService>.Instance) { RetryDelay = TimeSpan.Zero };
        return (service, fake);
    }

    static SwapToBtcPaymentMethodConfig Config() => new()
    {
        Integrator = "my-store",
        ThornodeUrl = "https://thornode.example.com",
        MemolessUrl = "https://memoless.example.com/memoless"
    };

    static async Task<ThorPaymentStatus> Settle(ThorDepositService service, string invoiceId = "inv1", decimal btcDue = 0.01m)
    {
        var first = service.Get(Litecoin, Config(), invoiceId, BtcAddress, btcDue);
        Assert.Equal(ThorPaymentState.Preparing, first.State);
        await service.Pending(invoiceId, Litecoin);
        return service.Get(Litecoin, Config(), invoiceId, BtcAddress, btcDue);
    }

    [Fact]
    public async Task PreparesAnAddressAndExactAmountWithoutAMemo()
    {
        var (service, fake) = Create();

        var status = await Settle(service);

        Assert.Equal(ThorPaymentState.Ready, status.State);
        var payment = status.Payment!;
        Assert.Equal("ltc1qvault", payment.Address);
        Assert.False(payment.Used);
        // The amount ends in the reference and covers what the quote needs (about 10.2 LTC for 0.01 BTC).
        Assert.True(ThorDepositService.HasReference(payment.Amount, "00042", 8));
        Assert.InRange(payment.Amount, 10m, 10.5m);
        // Registered with the quote's memo, to this invoice's address; preflight retried until the registration was on chain.
        var register = fake.Requests.Single(r => r.Path.EndsWith("/api/v1/register"));
        Assert.StartsWith("/memoless/api/v1/register", register.Path);
        Assert.Contains(BtcAddress, register.Body);
        Assert.Equal(2, fake.Requests.Count(r => r.Path.EndsWith("/api/v1/preflight")));
    }

    [Fact]
    public async Task RefusesARegistrationForAnotherAddress()
    {
        var (service, _) = Create(new FakeThorchain(registeredMemo: "=:b:bc1qsomeoneelse:0/1/0"));
        var status = await Settle(service, "inv2");
        Assert.Equal(ThorPaymentState.Failed, status.State);
        Assert.Null(status.Payment);
        Assert.DoesNotContain("THORChain", status.Error);
    }

    [Fact]
    public async Task ShowsWhenTheDepositWasAlreadyMade()
    {
        var (service, _) = Create(new FakeThorchain(usesAfterPreflights: 2));
        var ready = await Settle(service, "inv5");
        Assert.False(ready.Payment!.Used);
        // Force a recheck as if two minutes had passed.
        var stale = ready.Payment with { CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        service.Seed("inv5", Litecoin, stale);
        service.Get(Litecoin, Config(), "inv5", BtcAddress, 0.01m);
        await service.Pending("inv5", Litecoin);
        Assert.True(service.Get(Litecoin, Config(), "inv5", BtcAddress, 0.01m).Payment!.Used);
    }

    [Fact]
    public async Task AcceptsAnAffiliateTheApiAdds()
    {
        var (service, _) = Create(new FakeThorchain(registeredMemoSuffix: ":tc:5"));
        var status = await Settle(service, "inv3");
        Assert.Equal(ThorPaymentState.Ready, status.State);
        Assert.EndsWith(":tc:5", status.Payment!.Memo);
    }

    [Fact]
    public async Task ANewAmountDueGetsANewPayment()
    {
        var (service, fake) = Create();
        await Settle(service, "inv4", 0.01m);
        var second = await Settle(service, "inv4", 0.005m);
        Assert.Equal(ThorPaymentState.Ready, second.State);
        Assert.InRange(second.Payment!.Amount, 5m, 5.3m);
        Assert.Equal(2, fake.Requests.Count(r => r.Path.EndsWith("/api/v1/register")));
    }

    [Theory]
    [InlineData("0.30953451", "00042", 8, "0.31000042")]
    [InlineData("0.30900041", "00042", 8, "0.30900042")]
    [InlineData("0.30900042", "00042", 8, "0.30900042")]
    [InlineData("12.3", "00042", 6, "12.300042")]
    [InlineData("12.399999", "00042", 6, "12.400042")]
    public void PutsTheReferenceInTheLastDigits(string atLeast, string reference, int decimals, string expected)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var amount = ThorDepositService.AmountWithReference(decimal.Parse(atLeast, c), reference, decimals);
        Assert.Equal(decimal.Parse(expected, c), amount);
        Assert.True(ThorDepositService.HasReference(amount, reference, decimals));
    }

    [Theory]
    [InlineData("0.31000042", 8, "0.31000042")]
    [InlineData("12.300042", 6, "12.300042")]
    [InlineData("1.5", 8, "1.5")]
    public void FormatsLikeACoinAmount(string amount, int decimals, string expected)
    {
        Assert.Equal(expected, SwapToBtcCheckoutModelExtension.Format(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), decimals));
    }

    [Fact]
    public void LocalHttpIsOnlyAllowedForThisMachine()
    {
        Assert.Equal("http://127.0.0.1:5000/", ThorQuoteService.SafeUrl("http://127.0.0.1:5000", "https://d.example").ToString());
        Assert.Equal("https://d.example/", ThorQuoteService.SafeUrl("http://evil.example", "https://d.example").ToString());
    }
}
