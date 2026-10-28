using System.Net;
using System.Text;
using System.Web;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Pegasus;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class PegasusTests
{
    const string BtcAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    const string XmrAddress = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";
    static readonly SwapSourceChain Monero = SwapSourceChain.Find("MONERO")!;

    /// <summary>Pegasus Swap: 1 XMR = 0.004 BTC, every order gets a new deposit address, status is whatever the test sets.</summary>
    class FakePegasus : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<JObject> Created { get; } = new();
        public int Status { get; set; } = PegasusStatus.New;
        public string? MinimumError { get; set; }
        /// <summary>The first orders created pay this share of what the deposit is worth, to test asking again.</summary>
        public decimal PayoutShare { get; set; } = 1m;
        public int ShortOrders { get; set; } = 1;
        /// <summary>Every order pays this much BTC, whatever is deposited (the rate keeps worsening).</summary>
        public decimal? FixedPayout { get; set; }
        public bool SwapEstimateFields { get; set; }
        public const decimal BtcPerXmr = 0.004m;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            JToken json;
            if (path == "/api/private/get-coin-network")
                json = query["coin"] == "xmr"
                    ? JObject.Parse("""{"deposit":[{"subName":"XMR"}],"withdraws":[{"subName":"XMR"}]}""")
                    : JObject.Parse("""{"deposit":[{"subName":"BTC"},{"subName":"LIGHTNING"}],"withdraws":[{"subName":"BTC"},{"subName":"LIGHTNING"}]}""");
            else if (path == "/api/private/exchange-coin" && MinimumError is { } min)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(new JObject { ["message"] = min }.ToString()) };
            else if (path == "/api/private/exchange-coin")
            {
                var btc = decimal.Parse(query["amount"]!, System.Globalization.CultureInfo.InvariantCulture);
                var xmr = btc / BtcPerXmr;
                json = new JObject
                {
                    ["pair"] = "XMR_BTC",
                    ["amount"] = SwapEstimateFields ? btc : xmr,
                    ["receive"] = SwapEstimateFields ? xmr : btc,
                    ["exchangeRate"] = BtcPerXmr,
                    ["minAmount"] = 0.05m,
                    ["maxAmount"] = 500m
                };
            }
            else if (path == "/api/private/create-transaction")
            {
                var body = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Created.Add(body);
                var deposit = body["depositAmount"]!.Value<decimal>();
                json = Order(Created.Count, deposit, FixedPayout ?? deposit * BtcPerXmr * (Created.Count <= ShortOrders ? PayoutShare : 1m), body["receiveAddress"]!.ToString());
            }
            else if (path == "/api/private/get-transaction")
            {
                var n = int.Parse(query["id"]!.TrimStart('O'));
                var body = Created[n - 1];
                var deposit = body["depositAmount"]!.Value<decimal>();
                json = Order(n, deposit, deposit * BtcPerXmr, body["receiveAddress"]!.ToString());
                json["status"] = Status;
                if (Status != PegasusStatus.New)
                    json["txId"] = "xmrdeposittx";
                if (Status == PegasusStatus.Success)
                    json["receiveTransaction"] = new JObject { ["txId"] = "btcpayouttx", ["amount"] = deposit * BtcPerXmr };
            }
            else
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not found"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") };
        }

        static JObject Order(int n, decimal deposit, decimal receive, string receiveAddress) => new()
        {
            ["type"] = new JObject { ["typeSwap"] = 1 },
            ["orderNumber"] = "O" + n,
            ["status"] = PegasusStatus.New,
            ["pairs"] = new JObject
            {
                ["deposit"] = new JObject { ["coin"] = new JObject { ["name"] = "xmr", ["value"] = deposit.ToString(System.Globalization.CultureInfo.InvariantCulture), ["network"] = "XMR" }, ["address"] = "8xmrdeposit" + n, ["memo"] = null },
                ["receive"] = new JObject { ["coin"] = new JObject { ["name"] = "btc", ["value"] = receive, ["network"] = "BTC" }, ["address"] = receiveAddress }
            }
        };
    }

    static SwapToBtcPaymentMethodConfig Config() => new()
    {
        PegasusPublicKey = "pub",
        PegasusSecret = "secret",
        RefundMonero = XmrAddress
    };

    class FakePending : IPendingPayments
    {
        public List<(string Order, decimal Btc)> Added { get; } = new();
        public List<string?> Retired { get; } = new();
        public Task Add(string invoiceId, PaymentMethodId pmi, string orderNumber, decimal btc)
        {
            Added.Add((orderNumber, btc));
            return Task.CompletedTask;
        }
        public Task Retire(string invoiceId, PaymentMethodId pmi, string? orderNumber, string reason)
        {
            Retired.Add(orderNumber);
            return Task.CompletedTask;
        }
    }

    static (PegasusSwapService Service, FakePegasus Fake) Create(FakePegasus? fake = null, IPendingPayments? pending = null)
    {
        fake ??= new FakePegasus();
        var service = new PegasusSwapService(new PegasusClient(new HttpClient(fake)), null!, NullLogger<PegasusSwapService>.Instance, null, pending)
        {
            BaseUrl = new Uri("https://pegasus.example.com")
        };
        return (service, fake);
    }

    async Task<PegasusSwapStatus> Ready(PegasusSwapService service, SwapToBtcPaymentMethodConfig config, decimal btcDue, string invoiceId = "inv1")
    {
        var status = service.Get(config, "store1", Monero.PaymentMethodId, invoiceId, BtcAddress, btcDue);
        await service.Pending(invoiceId);
        return status.State == PegasusState.Preparing ? service.Get(config, "store1", Monero.PaymentMethodId, invoiceId, BtcAddress, btcDue) : status;
    }

    [Fact]
    public void SignsTheEndpointNameWithTheSecret()
    {
        Assert.Equal("9cd7814d023bb144573c4ed52a8ab388ff898a614eaf4a9a5c14d7b3bb7f1b7a0fc39a02306624f9786200d2fb2a49f5fe91b0067de98d6e302ceba8b5710206",
            PegasusClient.Sign("get-transaction", "secret"));
    }

    [Fact]
    public void MoneroIsItsOwnPaymentMethod()
    {
        Assert.Equal("PEGASUS_MONERO", Monero.PaymentMethodId.ToString());
        Assert.Contains(Monero, SwapSourceChain.PaymentMethods);
        Assert.DoesNotContain(Monero, SwapSourceChain.ExchangeOptions);
        Assert.Equal(Monero, SwapSourceChain.Find(new PaymentMethodId("PEGASUS_MONERO")));
    }

    [Fact]
    public async Task ShowsTheDepositOfAFixedRateSwapToTheInvoiceAddress()
    {
        var (service, fake) = Create();
        var status = await Ready(service, Config(), 0.01m);

        Assert.Equal(PegasusState.Ready, status.State);
        Assert.Equal("8xmrdeposit1", status.Payment!.Address);
        Assert.Equal(2.5m, status.Payment.Amount);
        Assert.Equal("O1", status.Payment.OrderNumber);

        var created = Assert.Single(fake.Created);
        Assert.Equal("xmr", created["depositCoin"]!.ToString());
        Assert.Equal("XMR", created["depositNetwork"]!.ToString());
        Assert.Equal("btc", created["receiveCoin"]!.ToString());
        Assert.Equal("BTC", created["receiveNetwork"]!.ToString());
        Assert.Equal(BtcAddress, created["receiveAddress"]!.ToString());
        Assert.Equal(XmrAddress, created["refundAddress"]!.ToString());
        Assert.Equal(1, created["typeSwap"]!.Value<int>());
        Assert.Equal(2.5m, created["depositAmount"]!.Value<decimal>());

        // Every request is signed with the endpoint's name.
        var request = fake.Requests.Last();
        Assert.Equal("pub", request.Headers.GetValues("x-api-public-key").Single());
        Assert.Equal("create-transaction", request.Headers.GetValues("x-api-payload").Single());
        Assert.Equal(PegasusClient.Sign("create-transaction", "secret"), request.Headers.GetValues("x-api-signature").Single());

        // The same invoice and amount keep the same order.
        Assert.Equal("O1", service.Get(Config(), "store1", Monero.PaymentMethodId, "inv1", BtcAddress, 0.01m).Payment!.OrderNumber);
        Assert.Single(fake.Created);
    }

    [Fact]
    public async Task ReadsTheXmrAmountWhicheverFieldCarriesIt()
    {
        var (service, _) = Create(new FakePegasus { SwapEstimateFields = true });
        var status = await Ready(service, Config(), 0.01m);
        Assert.Equal(2.5m, status.Payment!.Amount);

        Assert.Equal(2.5m, PegasusSwapService.DepositFromEstimate(new PegasusEstimate(2.5m, 0.01m, null, null, null), 0.01m));
        Assert.Equal(2.5m, PegasusSwapService.DepositFromEstimate(new PegasusEstimate(0.01m, 2.5m, null, null, null), 0.01m));
        Assert.Null(PegasusSwapService.DepositFromEstimate(new PegasusEstimate(2.5m, 3m, null, null, null), 0.01m));
    }

    [Fact]
    public async Task AsksForMoreXmrWhenTheOrderWouldPayLessThanDue()
    {
        var (service, fake) = Create(new FakePegasus { PayoutShare = 0.99m });
        var status = await Ready(service, Config(), 0.01m);

        Assert.Equal(2, fake.Created.Count);
        Assert.Equal("O2", status.Payment!.OrderNumber);
        Assert.True(status.Payment.Amount > 2.5m * 1.01m);
    }

    [Fact]
    public async Task NeverShowsAnOrderThatStaysShort()
    {
        var (service, fake) = Create(new FakePegasus { FixedPayout = 0.0099m });
        var status = await Ready(service, Config(), 0.01m);
        Assert.Equal(PegasusState.Failed, status.State);
        Assert.Equal(3, fake.Created.Count);
    }

    [Fact]
    public async Task FollowsTheOrderUntilItIsReceived()
    {
        var (service, fake) = Create();
        await Ready(service, Config(), 0.01m);

        fake.Status = PegasusStatus.Exchanging;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);
        var status = service.Get(Config(), "store1", Monero.PaymentMethodId, "inv1", BtcAddress, 0.01m);
        Assert.Equal(PegasusState.Received, status.State);
        Assert.Contains(fake.Requests, r => r.RequestUri!.AbsolutePath == "/api/private/get-transaction" && r.RequestUri.Query.Contains("id=O1"));

        // Even if the amount due changes meanwhile (a partial payment landed), the swap on its way stays on screen.
        Assert.Equal("O1", service.Get(Config(), "store1", Monero.PaymentMethodId, "inv1", BtcAddress, 0.005m).Payment!.OrderNumber);
    }

    [Fact]
    public async Task AReceivedDepositIsAPendingPaymentUntilRefunded()
    {
        var pending = new FakePending();
        var (service, fake) = Create(pending: pending);
        await Ready(service, Config(), 0.01m);

        fake.Status = PegasusStatus.Confirmation;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);
        Assert.Equal(("O1", 0.01m), Assert.Single(pending.Added));

        // Later stages of the same order add nothing more.
        fake.Status = PegasusStatus.Exchanging;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(2);
        await service.FollowAll(CancellationToken.None);
        Assert.Single(pending.Added);
        Assert.Empty(pending.Retired);

        fake.Status = PegasusStatus.Refund;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(3);
        await service.FollowAll(CancellationToken.None);
        Assert.Equal("O1", Assert.Single(pending.Retired));
    }

    [Fact]
    public async Task AnOrderThatNeverPaysOutStopsHoldingTheInvoice()
    {
        var pending = new FakePending();
        var (service, fake) = Create(pending: pending);
        await Ready(service, Config(), 0.01m);
        fake.Status = PegasusStatus.Stuck;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);
        Assert.Single(pending.Added);
        Assert.Empty(pending.Retired);

        service.Now = () => DateTimeOffset.UtcNow.AddHours(25);
        await service.FollowAll(CancellationToken.None);
        await service.FollowAll(CancellationToken.None);
        Assert.Equal("O1", Assert.Single(pending.Retired));
    }

    [Fact]
    public async Task AShortPayoutGetsANewOrderForTheRest()
    {
        var (service, fake) = Create();
        await Ready(service, Config(), 0.01m);
        fake.Status = PegasusStatus.Success;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);
        Assert.Equal(PegasusState.Received, service.Get(Config(), "store1", Monero.PaymentMethodId, "inv1", BtcAddress, 0.01m).State);

        fake.Status = PegasusStatus.New;
        var rest = await Ready(service, Config(), 0.0001m);
        Assert.Equal(PegasusState.Ready, rest.State);
        Assert.Equal("O2", rest.Payment!.OrderNumber);
    }

    [Fact]
    public void MoneroHasNoDirectPaymentLink()
    {
        var handler = new SwapToBtcPaymentMethodHandler(Monero, null!, null!, null!, null!, null!);
        var prompt = new BTCPayServer.Services.Invoices.PaymentPrompt { Destination = BtcAddress };
        Assert.Null(new SwapToBtcPaymentLinkExtension(handler).GetPaymentLink(prompt, null));
    }

    [Fact]
    public async Task AnOverdueOrderGetsReplaced()
    {
        var (service, fake) = Create();
        await Ready(service, Config(), 0.01m);
        fake.Status = PegasusStatus.Overdue;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);

        var status = await Ready(service, Config(), 0.01m);
        Assert.Equal("O2", status.Payment!.OrderNumber);
    }

    [Fact]
    public async Task ARefundedOrderShowsAsRefunded()
    {
        var (service, fake) = Create();
        await Ready(service, Config(), 0.01m);
        fake.Status = PegasusStatus.Refund;
        service.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        await service.FollowAll(CancellationToken.None);
        Assert.Equal(PegasusState.Refunded, service.Get(Config(), "store1", Monero.PaymentMethodId, "inv1", BtcAddress, 0.01m).State);
    }

    [Fact]
    public async Task ResumesSavedOrders()
    {
        var (service, fake) = Create();
        service.Restore("inv9", "store1", Monero.PaymentMethodId, Config(),
        [
            new PersistedPegasusSwap { OrderNumber = "O1", Address = "8saved", Amount = "1.5", TargetSats = 1_000_000, Status = PegasusStatus.New, Created = DateTimeOffset.UtcNow }
        ]);
        fake.Created.Add(new JObject { ["depositAmount"] = 1.5m, ["receiveAddress"] = BtcAddress });
        fake.Status = PegasusStatus.Confirmation;
        await service.FollowAll(CancellationToken.None);
        var status = service.Get(Config(), "store1", Monero.PaymentMethodId, "inv9", BtcAddress, 0.01m);
        Assert.Equal(PegasusState.Received, status.State);
        Assert.Equal("8saved", status.Payment!.Address);
    }

    [Fact]
    public async Task MoneroIsUnavailableOutsideTheExchangeLimits()
    {
        var (service, fake) = Create(new FakePegasus { MinimumError = "Minimum amount to exchange is 0.1" });
        Assert.Equal("Below the exchange's minimum", await service.Unavailable("store1", Config(), 0.0001m, CancellationToken.None));
        var asked = fake.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/private/exchange-coin");
        // Remembered: a smaller order is refused without asking again.
        Assert.Equal("Below the exchange's minimum", await service.Unavailable("store1", Config(), 0.00005m, CancellationToken.None));
        Assert.Equal(asked, fake.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/private/exchange-coin"));

        fake.MinimumError = null;
        Assert.Null(await service.Unavailable("store1", Config(), 0.01m, CancellationToken.None));
    }

    [Fact]
    public async Task MoneroNeedsKeysAndARefundAddress()
    {
        var (service, _) = Create();
        var noKeys = Config();
        noKeys.PegasusSecret = null;
        Assert.NotNull(await service.Unavailable("store1", noKeys, 0.01m, CancellationToken.None));
        var noRefund = Config();
        noRefund.RefundMonero = null;
        Assert.NotNull(await service.Unavailable("store1", noRefund, 0.01m, CancellationToken.None));
    }

    [Fact]
    public void ChecksMoneroRefundAddresses()
    {
        Assert.True(AddressChecks.IsMoneroAddress(XmrAddress));
        Assert.False(AddressChecks.IsMoneroAddress(XmrAddress[..^1] + "B"));
        Assert.False(AddressChecks.IsMoneroAddress(BtcAddress));
        Assert.Contains(SwapToBtcPaymentMethodHandler.Validate(new SwapToBtcPaymentMethodConfig { Integrator = "x", RefundMonero = "4abc" }),
            e => e.Field == nameof(SwapToBtcPaymentMethodConfig.RefundMonero));
        Assert.DoesNotContain(SwapToBtcPaymentMethodHandler.Validate(new SwapToBtcPaymentMethodConfig { Integrator = "x", RefundMonero = XmrAddress }),
            e => e.Field == nameof(SwapToBtcPaymentMethodConfig.RefundMonero));
    }

    [Fact]
    public void ParsesValidationErrorsAndWrappedResponses()
    {
        var tx = PegasusClient.Transaction(JObject.Parse("""
            {"orderNumber":"OEFRW0","status":"12","txId":"dep","pairs":{"deposit":{"coin":{"value":"1.25"},"address":"8a"},"receive":{"coin":{"value":0.005},"address":"bc1q"}},
             "receiveTransaction":{"txId":"pay","amount":0.0049}}
            """));
        Assert.Equal(PegasusStatus.Sending, tx.Status);
        Assert.Equal(1.25m, tx.DepositAmount);
        Assert.Equal(0.005m, tx.ReceiveAmount);
        Assert.Equal("pay", tx.PayoutTxId);
        Assert.Equal("dep", tx.DepositTxId);
    }
}
