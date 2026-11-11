using System.Net;
using System.Text;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class FailedSwapBackfillTests
{
    const string InvoiceAddress = "bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh";
    const string Refunded = "0x1111111111111111111111111111111111111111111111111111111111111111";
    const string Confirmed = "0x2222222222222222222222222222222222222222222222222222222222222222";
    const string PayoutTx = "3333333333333333333333333333333333333333333333333333333333333333";
    // EIP-55 test vector: LI.FI gives it in lower case, wallets show it checksummed.
    const string WalletLower = "0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed";
    const string WalletMixed = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    // The verifier's log lines, exactly as it writes them.
    static string RefundedLine(string tx) =>
        $"LI.FI reports the swap of the Ethereum transaction the customer reported ({tx}) as DONE (REFUNDED). Details at https://scan.li.fi/tx/{tx}";
    static string ConfirmedLine(string tx) =>
        $"LI.FI confirms the customer's Ethereum transaction {tx} pays this invoice, from wallet {WalletLower}, with BTC transaction {PayoutTx}. Track it at https://scan.li.fi/tx/{tx}";

    class FakeLifi : HttpMessageHandler
    {
        public Dictionary<string, JObject> Statuses { get; } = new();
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hash = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["txHash"]!;
            Asked.Add(hash);
            return Task.FromResult(Statuses.TryGetValue(hash, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"not found"}""") });
        }
    }

    class Db
    {
        public List<(string InvoiceId, string Message)> Lines { get; } = new();
        public Dictionary<string, HashSet<string>> Terms { get; } = new();
        public int FailWrites { get; set; }
    }

    static (FailedSwapBackfill Backfill, FakeLifi Lifi, Db Db) Create()
    {
        var lifi = new FakeLifi();
        var db = new Db();
        var backfill = new FailedSwapBackfill(null!, null!, new LifiStatusClient(new HttpClient(lifi)), null!, NullLogger<FailedSwapBackfill>.Instance)
        {
            Pace = TimeSpan.Zero,
            ReadLogLines = _ => Task.FromResult(db.Lines.ToList()),
            ReadTarget = id => Task.FromResult<FailedSwapBackfill.Target?>(id == "gone"
                ? null
                : new FailedSwapBackfill.Target(InvoiceAddress, new HashSet<string> { PayoutTx })),
            ReadTerms = id => Task.FromResult(db.Terms.TryGetValue(id, out var t) ? t.ToHashSet() : new HashSet<string>()),
            WriteTerms = (id, terms) =>
            {
                if (db.FailWrites > 0 && db.FailWrites-- > 0)
                    throw new InvalidOperationException("database unavailable");
                if (!db.Terms.TryGetValue(id, out var t))
                    db.Terms[id] = t = new HashSet<string>();
                t.UnionWith(terms);
                return Task.CompletedTask;
            }
        };
        return (backfill, lifi, db);
    }

    static JObject RefundedStatus(string toAddress = InvoiceAddress) => new()
    {
        ["status"] = "DONE", ["substatus"] = "REFUNDED", ["fromAddress"] = WalletLower, ["toAddress"] = toAddress,
        // A refund's receiving leg is the refund on the source chain, not a BTC payout.
        ["receiving"] = new JObject { ["txHash"] = "0x4444444444444444444444444444444444444444444444444444444444444444", ["chainId"] = 1 }
    };

    [Fact]
    public void ReadsOnlyTheVerifiersOwnLines()
    {
        Assert.Equal(new FailedSwapBackfill.Candidate("inv", "Ethereum", Refunded), FailedSwapBackfill.Parse("inv", RefundedLine(Refunded)));
        Assert.Equal(new FailedSwapBackfill.Candidate("inv", "Ethereum", Confirmed), FailedSwapBackfill.Parse("inv", ConfirmedLine(Confirmed)));
        Assert.Equal("BNB Chain", FailedSwapBackfill.Parse("inv",
            $"LI.FI reports the swap of the BNB Chain transaction the customer reported ({Refunded}) as FAILED. Details at x")!.ChainName);
        // What the customer's browser reports is never taken from the log.
        Assert.Null(FailedSwapBackfill.Parse("inv", $"Customer-reported, unverified: Ethereum transaction {Refunded}. Checking it with LI.FI."));
        Assert.Null(FailedSwapBackfill.Parse("inv", $"Customer-reported, unverified: started paying 0.1 ETH on Ethereum from wallet {WalletMixed}."));
    }

    [Fact]
    public async Task MakesAnEarlierRefundedSwapSearchableByItsTransactionAndWalletOnce()
    {
        var (backfill, lifi, db) = Create();
        db.Lines.Add(("inv1", $"Customer-reported, unverified: Ethereum transaction {Refunded}. Checking it with LI.FI."));
        db.Lines.Add(("inv1", RefundedLine(Refunded)));
        lifi.Statuses[Refunded] = RefundedStatus();

        var (complete, added) = await backfill.RunOnce(CancellationToken.None);
        Assert.True(complete);
        Assert.Equal(1, added);
        Assert.Equal(new HashSet<string> { Refunded, WalletLower, WalletMixed }, db.Terms["inv1"]);
        Assert.Equal(Refunded, Assert.Single(lifi.Asked));

        // Run again: nothing missing, nothing written.
        (complete, added) = await backfill.RunOnce(CancellationToken.None);
        Assert.True(complete);
        Assert.Equal(0, added);
    }

    [Fact]
    public async Task AddsOnlyTheChecksummedWalletToAnEarlierConfirmedSwap()
    {
        var (backfill, lifi, db) = Create();
        db.Lines.Add(("inv1", ConfirmedLine(Confirmed)));
        db.Terms["inv1"] = [Confirmed, WalletLower, PayoutTx];
        lifi.Statuses[Confirmed] = new JObject
        {
            ["status"] = "DONE", ["fromAddress"] = WalletLower, ["toAddress"] = "0x11f111f111f111f111f111f111f111f111f111f1",
            ["receiving"] = new JObject { ["txHash"] = PayoutTx }
        };
        string[]? written = null;
        var write = backfill.WriteTerms!;
        backfill.WriteTerms = (id, t) => { written = t.ToArray(); return write(id, t); };

        await backfill.RunOnce(CancellationToken.None);
        Assert.Equal(new[] { WalletMixed }, written);
    }

    [Fact]
    public async Task LeavesAloneWhatTheLiveCheckWouldNotMakeSearchable()
    {
        var (backfill, lifi, db) = Create();
        const string Elsewhere = "0x5555555555555555555555555555555555555555555555555555555555555555";
        const string Unknown = "0x6666666666666666666666666666666666666666666666666666666666666666";
        db.Lines.Add(("inv1", RefundedLine(Elsewhere)));
        db.Lines.Add(("inv2", RefundedLine(Unknown)));
        db.Lines.Add(("gone", RefundedLine(Refunded)));
        // Refunded, but LI.FI shows it was sent to another BTC address: another payment.
        lifi.Statuses[Elsewhere] = RefundedStatus(toAddress: "bc1qsomeoneelse");

        var (complete, added) = await backfill.RunOnce(CancellationToken.None);
        Assert.True(complete);
        Assert.Equal(0, added);
        Assert.Empty(db.Terms);
        // An invoice that no longer exists is not asked about.
        Assert.DoesNotContain(Refunded, lifi.Asked);
    }

    [Fact]
    public async Task IsIncompleteAndRetriesWhenLifiOrTheDatabaseCannotAnswer()
    {
        var (backfill, lifi, db) = Create();
        db.Lines.Add(("inv1", RefundedLine(Refunded)));
        lifi.Statuses[Refunded] = new JObject { ["status"] = "INVALID" };
        Assert.False((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Empty(db.Terms);

        lifi.Statuses[Refunded] = RefundedStatus();
        db.FailWrites = 1;
        Assert.False((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Empty(db.Terms);

        Assert.True((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Contains(Refunded, db.Terms["inv1"]);
    }

    [Theory]
    [InlineData(WalletLower, WalletMixed)]
    [InlineData("0x52908400098527886e0f7030069857d2e4169ee7", "0x52908400098527886E0F7030069857D2E4169EE7")]
    [InlineData("0xwallet", null)]
    [InlineData("TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", null)]
    public void ChecksumsEvmWallets(string address, string? expected) => Assert.Equal(expected, SourceVerifier.Checksummed(address));

    [Fact]
    public void ALiveRefundedSwapIsSearchableByBothSpellingsOfItsWallet()
    {
        var p = new SourceVerifier.Pending("inv", "Ethereum", Refunded, InvoiceAddress, DateTimeOffset.UtcNow);
        var status = RefundedStatus().ToObject<LifiTransferStatus>()!;
        Assert.Equal(new[] { Refunded, WalletLower, WalletMixed }, SourceVerifier.SearchTerms(SourceVerifier.Verdict.Failed, status, p));
    }
}
