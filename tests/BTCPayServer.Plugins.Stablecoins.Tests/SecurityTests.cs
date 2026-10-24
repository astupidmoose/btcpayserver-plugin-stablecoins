using System.Net;
using System.Text;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Controllers;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class SecurityTests
{
    const string InvoiceAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    const string InvoicePayoutTx = "9d3cf9b0000000000000000000000000000000000000000000000000000787b0";
    // What LI.FI reports as toAddress for a Bitcoin destination.
    const string LifiPlaceholder = "0x11f111f111f111f111f111f111f111f111f111f1";

    class FakeLifi : HttpMessageHandler
    {
        public Dictionary<string, JObject> Statuses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hash = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["txHash"]!;
            return Task.FromResult(Statuses.TryGetValue(hash, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") });
        }
    }

    static (SourceVerifier Verifier, FakeLifi Lifi, List<string> Logs, List<string> Terms) Verifier()
    {
        var lifi = new FakeLifi();
        var logs = new List<string>();
        var terms = new List<string>();
        var verifier = new SourceVerifier(new LifiStatusClient(new HttpClient(lifi)), null!, NullLogger<SourceVerifier>.Instance)
        {
            WriteLog = (_, message, _) => { logs.Add(message); return Task.CompletedTask; },
            WriteSearchTerms = (_, t) => { terms.AddRange(t); return Task.CompletedTask; },
            GetPaymentTxIds = _ => Task.FromResult(new HashSet<string> { InvoicePayoutTx })
        };
        return (verifier, lifi, logs, terms);
    }

    [Fact]
    public async Task KeepsReportedTransactionsOnTheInvoiceUntilDecided()
    {
        var (verifier, lifi, _, _) = Verifier();
        var saved = new List<List<ReportedSource>>();
        verifier.WriteReports = (_, _, reports) => { saved.Add(reports); return Task.CompletedTask; };
        var pmi = SwapSourceChain.PaymentMethods.First(c => c.Provider == SwapProvider.Lifi).PaymentMethodId;
        lifi.Statuses["0xbase"] = new JObject { ["status"] = "PENDING", ["toAddress"] = LifiPlaceholder };

        await verifier.Report("inv", pmi, "Base", "0xbase", InvoiceAddress);
        Assert.Equal("0xbase", Assert.Single(saved[^1]).TxHash);
        await verifier.CheckAll(CancellationToken.None);
        Assert.Single(saved);

        lifi.Statuses["0xbase"] = new JObject { ["status"] = "DONE", ["toAddress"] = LifiPlaceholder, ["receiving"] = new JObject { ["txHash"] = InvoicePayoutTx } };
        await verifier.CheckAll(CancellationToken.None);
        Assert.Empty(saved[^1]);
    }

    [Fact]
    public void ReportedTransactionsReadBackWithBtcpaysSerializer()
    {
        var serializer = BlobSerializer.CreateSerializer().Serializer;
        var since = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var details = JObject.FromObject(new SwapToBtcPromptDetails { ReportedSources = [new ReportedSource { TxHash = "0xbase", Since = since }] }, serializer);
        var back = details.ToObject<SwapToBtcPromptDetails>(serializer)!;
        Assert.Equal(since, Assert.Single(back.ReportedSources!).Since);
    }

    [Fact]
    public async Task ConfirmsByTheBtcPayoutWhenLifiShowsAPlaceholderReceiver()
    {
        var (verifier, lifi, logs, terms) = Verifier();
        lifi.Statuses["0xbase"] = new JObject { ["status"] = "PENDING", ["fromAddress"] = "0xwallet", ["toAddress"] = LifiPlaceholder };
        verifier.Enqueue("inv", "Base", "0xbase", InvoiceAddress);
        await verifier.CheckAll(CancellationToken.None);
        // In flight: no warning, still checked.
        Assert.Empty(logs);
        Assert.Equal(1, verifier.PendingCount);

        lifi.Statuses["0xbase"] = new JObject { ["status"] = "DONE", ["fromAddress"] = "0xwallet", ["toAddress"] = LifiPlaceholder, ["receiving"] = new JObject { ["txHash"] = "0x" + InvoicePayoutTx.ToUpperInvariant() } };
        await verifier.CheckAll(CancellationToken.None);
        Assert.Equal(0, verifier.PendingCount);
        Assert.Single(logs);
        Assert.StartsWith("LI.FI confirms", logs[0]);
        Assert.Equal(new[] { "0xbase", "0xwallet", InvoicePayoutTx }, terms);
    }

    [Fact]
    public async Task KeepsCheckingWhenTheInvoiceCannotBeRead()
    {
        var (verifier, lifi, logs, terms) = Verifier();
        lifi.Statuses["0xbase"] = new JObject { ["status"] = "DONE", ["toAddress"] = LifiPlaceholder, ["receiving"] = new JObject { ["txHash"] = InvoicePayoutTx } };
        verifier.Enqueue("inv", "Base", "0xbase", InvoiceAddress);
        verifier.GetPaymentTxIds = _ => throw new InvalidOperationException("database unavailable");
        verifier.Now = () => DateTimeOffset.UtcNow + SourceVerifier.GiveUpAfter + TimeSpan.FromMinutes(1);
        await verifier.CheckAll(CancellationToken.None);
        Assert.Empty(logs);
        Assert.Equal(1, verifier.PendingCount);

        verifier.GetPaymentTxIds = _ => Task.FromResult(new HashSet<string> { InvoicePayoutTx });
        await verifier.CheckAll(CancellationToken.None);
        Assert.StartsWith("LI.FI confirms", Assert.Single(logs));
        Assert.Contains(InvoicePayoutTx, terms);
    }

    [Fact]
    public async Task WarnsWhenLifisBtcPayoutNeverReachesTheInvoice()
    {
        var (verifier, lifi, logs, terms) = Verifier();
        lifi.Statuses["0xbase"] = new JObject { ["status"] = "DONE", ["toAddress"] = LifiPlaceholder, ["receiving"] = new JObject { ["txHash"] = "otherbtctx" } };
        // LI.FI may keep the intended BTC address on a failed swap: still a failure.
        lifi.Statuses["0xfailed"] = new JObject { ["status"] = "FAILED", ["substatus"] = "REFUNDED", ["toAddress"] = InvoiceAddress };
        verifier.Enqueue("inv", "Base", "0xbase", InvoiceAddress);
        verifier.Enqueue("inv", "Base", "0xfailed", InvoiceAddress);
        await verifier.CheckAll(CancellationToken.None);
        Assert.Contains(logs, l => l.Contains("0xfailed") && l.Contains("FAILED (REFUNDED)"));
        // The payout may still be on its way to the invoice: wait.
        Assert.Equal(1, verifier.PendingCount);

        verifier.Now = () => DateTimeOffset.UtcNow + SourceVerifier.GiveUpAfter + TimeSpan.FromMinutes(1);
        await verifier.CheckAll(CancellationToken.None);
        Assert.Contains(logs, l => l.Contains("0xbase") && l.Contains("otherbtctx") && l.Contains("did not receive"));
        Assert.DoesNotContain(logs, l => l.Contains("does not pay this invoice"));
        Assert.Empty(terms);
    }

    [Fact]
    public async Task OnlyATransactionLifiConfirmsPaysTheInvoiceBecomesSearchable()
    {
        var (verifier, lifi, logs, terms) = Verifier();
        lifi.Statuses["0xgood"] = new JObject { ["status"] = "DONE", ["fromAddress"] = "0xwallet", ["toAddress"] = InvoiceAddress, ["receiving"] = new JObject { ["txHash"] = "btctx" } };
        lifi.Statuses["0xother"] = new JObject { ["status"] = "DONE", ["toAddress"] = "bc1qsomeoneelse" };
        verifier.Enqueue("inv", "Base", "0xgood", InvoiceAddress);
        verifier.Enqueue("inv", "Base", "0xother", InvoiceAddress);
        verifier.Enqueue("inv", "Base", "0xunknown", InvoiceAddress);
        await verifier.CheckAll(CancellationToken.None);

        Assert.Equal(new[] { "0xgood", "0xwallet", "btctx" }, terms);
        Assert.Contains(logs, l => l.StartsWith("LI.FI confirms") && l.Contains("0xgood"));
        Assert.Contains(logs, l => l.Contains("0xother") && l.Contains("does not pay this invoice"));
        // Not indexed yet: still pending, nothing logged.
        Assert.Equal(1, verifier.PendingCount);

        verifier.Now = () => DateTimeOffset.UtcNow + SourceVerifier.GiveUpAfter + TimeSpan.FromMinutes(1);
        await verifier.CheckAll(CancellationToken.None);
        Assert.Equal(0, verifier.PendingCount);
        Assert.Contains(logs, l => l.Contains("never saw") && l.Contains("0xunknown"));
        Assert.Equal(3, terms.Count);
    }

    [Fact]
    public void BoundsNotesPerInvoiceAndRequestsPerCaller()
    {
        using var notes = new CheckoutNotes();
        for (var i = 0; i < CheckoutNotes.MaxNotesPerInvoice; i++)
            Assert.True(notes.TryAdd("inv", "k" + i));
        Assert.False(notes.TryAdd("inv", "one more"));
        Assert.False(notes.TryAdd("inv", "k0"));
        Assert.True(notes.TryAdd("other", "k0"));

        for (var i = 0; i < CheckoutNotes.MaxRequestsPerCaller; i++)
            Assert.True(notes.AllowCaller("1.2.3.4"));
        Assert.False(notes.AllowCaller("1.2.3.4"));
        Assert.True(notes.AllowCaller("5.6.7.8"));
    }

    [Theory]
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7", true)] // EIP-55 test vector
    [InlineData("0x52908400098527886e0f7030069857d2e4169ee7", true)] // all lower case: no checksum
    [InlineData("0x52908400098527886E0F7030069857D2E4169Ee7", false)] // one letter's case is wrong
    [InlineData("0xde709f2102306220921060314715629080e2fb77", true)]
    [InlineData("0x27b1fdb04752bbc536007a920d24acb045561c26", true)]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", true)]
    [InlineData("0x5aaeb6053F3E94C9b9A09f33669435E7Ef1BeAed", false)]
    public void ChecksEip55Checksums(string address, bool valid) => Assert.Equal(valid, AddressChecks.IsEvmAddress(address));

    [Fact]
    public void TheApiRefusesBadRefundAddressesAndTolerances()
    {
        var good = new SwapToBtcPaymentMethodConfig
        {
            Integrator = "store", RefundEvm = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
            RefundSolana = "7xKXtg2CW87d97TXJSDpbD5jBkheTqA83TZRuJosgAsU", RefundTron = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"
        };
        Assert.Empty(SwapToBtcPaymentMethodHandler.Validate(good));
        var bad = new SwapToBtcPaymentMethodConfig
        {
            Integrator = "store", RefundEvm = "0x5aaeb6053F3E94C9b9A09f33669435E7Ef1BeAed", RefundSolana = "0OIl", RefundTron = "Xnot", IntentsSlippageBps = 5000
        };
        Assert.Equal(new[] { "RefundEvm", "RefundSolana", "RefundTron", "IntentsSlippageBps" },
            SwapToBtcPaymentMethodHandler.Validate(bad).Select(e => e.Field));
    }

    [Fact]
    public void MovesTheKeysOutOfThePaymentMethods()
    {
        var store = new StoreData { Id = "s" };
        store.SetPaymentMethodConfig(new BTCPayServer.Payments.PaymentMethodId("LIFI_BASE"),
            new JObject { ["integrator"] = "my-store", ["apiKey"] = "lifi-key", ["intentsApiKey"] = "jwt", ["intentsUrl"] = "https://evil.example" });
        store.SetPaymentMethodConfig(new BTCPayServer.Payments.PaymentMethodId("LIFI_ETHEREUM"), new JObject { ["integrator"] = "my-store" });

        var moved = LegacyIdMigration.StripSecrets(store);

        Assert.Equal("lifi-key", moved!.Value.ApiKey);
        Assert.Equal("jwt", moved.Value.IntentsApiKey);
        Assert.Equal("my-store", moved.Value.Config.Integrator);
        var config = (JObject)store.GetPaymentMethodConfig(new BTCPayServer.Payments.PaymentMethodId("LIFI_BASE"))!;
        Assert.Null(config["apiKey"]);
        Assert.Null(config["intentsApiKey"]);
        Assert.Null(config["intentsUrl"]);
        Assert.Equal("my-store", config["integrator"]!.ToString());
        Assert.Null(LegacyIdMigration.StripSecrets(store));
    }

    [Fact]
    public void SavedQuotesReadBackWithBtcpaysSerializer()
    {
        var serializer = BlobSerializer.CreateSerializer().Serializer;
        var deadline = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var details = new SwapToBtcPromptDetails { IntentsQuotes = [new Intents.PersistedQuote { Address = "0xabc", Deadline = deadline }] };
        var saved = JObject.FromObject(details, serializer);
        Assert.Equal(JTokenType.Integer, saved["intentsQuotes"]![0]!["deadline"]!.Type);
        // The invoice is stored as text and read back from it.
        var back = JObject.Parse(saved.ToString()).ToObject<SwapToBtcPromptDetails>(serializer)!;
        Assert.Equal(deadline, back.IntentsQuotes![0].Deadline);
        // As read from the invoice database.
        var stored = JObject.Parse($$"""{"intentsQuotes":[{"address":"0xabc","deadline":{{deadline.ToUnixTimeSeconds()}}}]}""");
        Assert.Equal(deadline, stored.ToObject<SwapToBtcPromptDetails>(serializer)!.IntentsQuotes![0].Deadline);
    }
}
