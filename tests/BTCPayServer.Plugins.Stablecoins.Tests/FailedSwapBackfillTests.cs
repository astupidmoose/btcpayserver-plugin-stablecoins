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
        public List<string?> FromChains { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var hash = query["txHash"]!;
            Asked.Add(hash);
            FromChains.Add(query["fromChain"]);
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
        public List<string> Addresses { get; } = [InvoiceAddress];
        public FailedSwapBackfill.State? State { get; set; }
        public int FailStateReads { get; set; }
        public int FailStateWrites { get; set; }
        public List<TimeSpan> Delays { get; } = new();
    }

    static (FailedSwapBackfill Backfill, FakeLifi Lifi, Db Db) Create()
    {
        var lifi = new FakeLifi();
        var db = new Db();
        var backfill = new FailedSwapBackfill(null!, null!, new LifiStatusClient(new HttpClient(lifi)), null!, NullLogger<FailedSwapBackfill>.Instance)
        {
            Pace = TimeSpan.FromSeconds(1),
            Delay = (d, _) => { db.Delays.Add(d); return Task.CompletedTask; },
            ReadLogLines = _ => Task.FromResult(db.Lines.ToList()),
            ReadTarget = id => Task.FromResult<FailedSwapBackfill.Target?>(id == "gone"
                ? null
                : new FailedSwapBackfill.Target(db.Addresses.ToList(), new HashSet<string> { PayoutTx })),
            ReadState = () => db.FailStateReads > 0 && db.FailStateReads-- > 0
                ? throw new InvalidOperationException("database unavailable")
                : Task.FromResult(db.State),
            WriteState = state =>
            {
                if (db.FailStateWrites > 0 && db.FailStateWrites-- > 0)
                    throw new InvalidOperationException("database unavailable");
                db.State = state;
                return Task.CompletedTask;
            },
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
        // Asked with the chain it was paid from.
        Assert.Equal("1", Assert.Single(lifi.FromChains));

        // A fresh run (after a restart, say) finds nothing missing and writes nothing.
        var fresh = new FailedSwapBackfill(null!, null!, new LifiStatusClient(new HttpClient(lifi)), null!, NullLogger<FailedSwapBackfill>.Instance)
        {
            Pace = TimeSpan.Zero, ReadLogLines = backfill.ReadLogLines, ReadTarget = backfill.ReadTarget, ReadTerms = backfill.ReadTerms,
            WriteTerms = (_, _) => throw new InvalidOperationException("nothing should be written")
        };
        var (complete2, added2) = await fresh.RunOnce(CancellationToken.None);
        Assert.True(complete2);
        Assert.Equal(0, added2);
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

    [Fact]
    public async Task ChecksTheRefundAgainstEveryBtcAddressOfTheInvoice()
    {
        const string EthereumAddress = "bc1qethereumprompt0000000000000000000000";
        const string BaseAddress = "bc1qbaseprompt000000000000000000000000000";
        const string Other = "0x7777777777777777777777777777777777777777777777777777777777777777";
        var (backfill, lifi, db) = Create();
        db.Addresses.Clear();
        db.Addresses.AddRange([EthereumAddress, BaseAddress]);
        // Refunded to the invoice's Base address, though Ethereum's is listed first.
        db.Lines.Add(("inv1", RefundedLine(Refunded)));
        lifi.Statuses[Refunded] = RefundedStatus(toAddress: BaseAddress);
        // Still refused: aimed at an address that is none of this invoice's.
        db.Lines.Add(("inv2", RefundedLine(Other)));
        lifi.Statuses[Other] = RefundedStatus(toAddress: "bc1qsomeoneelse");

        Assert.True((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Contains(Refunded, db.Terms["inv1"]);
        Assert.False(db.Terms.ContainsKey("inv2"));
    }

    [Fact]
    public async Task APaymentFromAnotherNetworkIsAskedAboutOnTheNetworkItWasPaidFrom()
    {
        var (backfill, lifi, db) = Create();
        // The customer switched the Ethereum checkout to Base: the verifier logged the chain they paid from.
        db.Lines.Add(("inv1", $"LI.FI reports the swap of the Base transaction the customer reported ({Refunded}) as FAILED. Details at x"));
        lifi.Statuses[Refunded] = new JObject { ["status"] = "FAILED", ["fromAddress"] = WalletLower, ["toAddress"] = InvoiceAddress };

        Assert.True((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Equal(SwapSourceChain.PaymentMethods.Single(c => c.Name == "Base").LifiChainId.ToString(), Assert.Single(lifi.FromChains));
        Assert.Contains(Refunded, db.Terms["inv1"]);
    }

    [Fact]
    public async Task PacesEveryLifiRequestWhateverTheAnswer()
    {
        const string Invalid = "0x8888888888888888888888888888888888888888888888888888888888888888";
        const string Unknown = "0x9999999999999999999999999999999999999999999999999999999999999999";
        const string Elsewhere = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var (backfill, lifi, db) = Create();
        db.Lines.AddRange([("inv1", RefundedLine(Invalid)), ("inv2", RefundedLine(Unknown)), ("inv3", RefundedLine(Elsewhere)),
            ("inv4", RefundedLine(Refunded)), ("gone", RefundedLine(Confirmed))]);
        lifi.Statuses[Invalid] = new JObject { ["status"] = "INVALID" };
        lifi.Statuses[Elsewhere] = RefundedStatus(toAddress: "bc1qsomeoneelse");
        lifi.Statuses[Refunded] = RefundedStatus();
        db.Terms["inv4"] = [Refunded, WalletLower, WalletMixed];

        await backfill.RunOnce(CancellationToken.None);
        // Four requests (the invoice that is gone is not asked about), four pauses.
        Assert.Equal(4, lifi.Asked.Count);
        Assert.Equal(4, db.Delays.Count);
    }

    [Fact]
    public async Task ARetryAsksOnlyAboutSwapsNotDecidedYet()
    {
        const string Invalid = "0x8888888888888888888888888888888888888888888888888888888888888888";
        var (backfill, lifi, db) = Create();
        db.Lines.AddRange([("inv1", RefundedLine(Invalid)), ("inv2", RefundedLine(Refunded))]);
        lifi.Statuses[Invalid] = new JObject { ["status"] = "INVALID" };
        lifi.Statuses[Refunded] = RefundedStatus();
        Assert.False((await backfill.RunOnce(CancellationToken.None)).Complete);
        lifi.Asked.Clear();

        lifi.Statuses[Invalid] = RefundedStatus();
        Assert.True((await backfill.RunOnce(CancellationToken.None)).Complete);
        Assert.Equal(Invalid, Assert.Single(lifi.Asked));
    }

    [Fact]
    public async Task ASettingsFailureNeverEscapesAndIsTriedAgain()
    {
        var (backfill, lifi, db) = Create();
        db.Lines.Add(("inv1", RefundedLine(Refunded)));
        lifi.Statuses[Refunded] = RefundedStatus();

        // Reading whether it already ran fails: nothing done, nothing thrown.
        db.FailStateReads = 1;
        Assert.False(await backfill.Attempt(CancellationToken.None));
        Assert.Empty(lifi.Asked);

        // The pass completes, but recording that fails: not done yet, nothing thrown.
        db.FailStateWrites = 1;
        Assert.False(await backfill.Attempt(CancellationToken.None));
        Assert.Contains(Refunded, db.Terms["inv1"]);
        Assert.Null(db.State);

        // The next attempt only records it, without asking LI.FI again.
        var asked = lifi.Asked.Count;
        Assert.True(await backfill.Attempt(CancellationToken.None));
        Assert.Equal(FailedSwapBackfill.Version, db.State!.Version);
        Assert.Equal(asked, lifi.Asked.Count);
    }

    [Fact]
    public async Task DoesNothingOnceItHasRun()
    {
        var (backfill, lifi, db) = Create();
        db.Lines.Add(("inv1", RefundedLine(Refunded)));
        lifi.Statuses[Refunded] = RefundedStatus();
        db.State = new FailedSwapBackfill.State { Version = FailedSwapBackfill.Version };

        Assert.True(await backfill.Attempt(CancellationToken.None));
        Assert.Empty(lifi.Asked);
        Assert.Empty(db.Terms);
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
