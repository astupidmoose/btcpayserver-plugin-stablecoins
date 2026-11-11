#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Runs once after an update: makes the swaps <see cref="SourceVerifier"/> checked before 0.12.2 searchable the way it
/// does now. Before 0.12.1 a swap LI.FI reported failed or refunded got only a log line, and a wallet was only searchable
/// in lower case. The swaps are found by the verifier's own log lines (written by the server after asking LI.FI, never by
/// the customer), and each is asked about again, so the same rules decide as for a new swap: LI.FI must still report it
/// failed, refunded or paying this invoice, and not aimed at another BTC address. Only missing terms are added. Nothing
/// about payments changes.
/// </summary>
public class FailedSwapBackfill(
    ApplicationDbContextFactory dbContextFactory,
    InvoiceRepository invoiceRepository,
    LifiStatusClient lifi,
    ISettingsRepository settingsRepository,
    ILogger<FailedSwapBackfill> logger) : BackgroundService
{
    public const string SettingName = "BTCPayServer.Plugins.Stablecoins.SearchBackfill";
    /// <summary>Raised when a later version needs the backfill to run again.</summary>
    public const int Version = 1;

    public class State
    {
        public int Version { get; set; }
    }

    public record Candidate(string InvoiceId, string ChainName, string TxHash);

    /// <summary>The invoice's BTC address for its LI.FI payment method, and the BTC transactions it received.</summary>
    public record Target(string BtcAddress, HashSet<string> PaymentTxIds);

    // The verifier's own log lines: a failed or refunded swap, and a confirmed one.
    static readonly Regex FailedLine = new(@"^LI\.FI reports the swap of the (?<chain>.+?) transaction the customer reported \((?<tx>[^()\s]+)\) as ",
        RegexOptions.CultureInvariant);
    static readonly Regex ConfirmedLine = new(@"^LI\.FI confirms the customer's (?<chain>.+?) transaction (?<tx>\S+) pays this invoice",
        RegexOptions.CultureInvariant);

    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>Between LI.FI requests, to stay well under its public rate limit.</summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(1);
    const int MaxRuns = 24;

    // Replaced by tests.
    public Func<CancellationToken, Task<List<(string InvoiceId, string Message)>>>? ReadLogLines { get; set; }
    public Func<string, Task<Target?>>? ReadTarget { get; set; }
    public Func<string, Task<HashSet<string>>>? ReadTerms { get; set; }
    public Func<string, List<string>, Task>? WriteTerms { get; set; }

    public static Candidate? Parse(string invoiceId, string message)
    {
        var m = FailedLine.Match(message);
        if (!m.Success)
            m = ConfirmedLine.Match(message);
        return m.Success ? new Candidate(invoiceId, m.Groups["chain"].Value, m.Groups["tx"].Value) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            for (var run = 0; run < MaxRuns; run++)
            {
                if ((await settingsRepository.GetSettingAsync<State>(SettingName))?.Version >= Version)
                    return;
                bool complete;
                try
                {
                    complete = (await RunOnce(stoppingToken)).Complete;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not make earlier LI.FI swaps searchable; trying again later");
                    complete = false;
                }
                if (complete)
                {
                    await settingsRepository.UpdateSetting(new State { Version = Version }, SettingName);
                    return;
                }
                await Task.Delay(RetryAfter, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One pass over every swap the verifier logged. Complete when every one was decided; a swap LI.FI or the database
    /// could not answer for is tried again on the next pass. Added counts the invoices that got new terms.
    /// </summary>
    public async Task<(bool Complete, int Added)> RunOnce(CancellationToken cancellationToken)
    {
        var lines = await (ReadLogLines ?? ReadLogLinesFromDb)(cancellationToken);
        var candidates = lines.Select(l => Parse(l.InvoiceId, l.Message)).OfType<Candidate>()
            .DistinctBy(c => (c.InvoiceId, c.TxHash.ToLowerInvariant())).ToList();
        bool complete = true;
        var added = 0;
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // An invoice that is gone, or has no LI.FI payment method: nothing to make searchable.
                if (await (ReadTarget ?? ReadTargetFromDb)(c.InvoiceId) is not { } target)
                    continue;
                var chain = SwapSourceChain.PaymentMethods.FirstOrDefault(x => x.Provider == SwapProvider.Lifi && x.Name == c.ChainName);
                var status = await lifi.Status(c.TxHash, chain?.LifiChainId, cancellationToken);
                // INVALID: a service LI.FI relies on is down; ask again next pass. Unknown to LI.FI now: nothing to add.
                if (status?.Status == "INVALID")
                {
                    complete = false;
                    continue;
                }
                if (status is null)
                    continue;
                var verdict = SourceVerifier.Judge(status, target.BtcAddress, target.PaymentTxIds);
                var pending = new SourceVerifier.Pending(c.InvoiceId, c.ChainName, c.TxHash, target.BtcAddress, DateTimeOffset.UtcNow);
                if (SourceVerifier.SearchTerms(verdict, status, pending) is not { } terms)
                    continue;
                var existing = await (ReadTerms ?? ReadTermsFromDb)(c.InvoiceId);
                var missing = terms.Where(t => !existing.Contains(t)).ToList();
                if (missing.Count == 0)
                    continue;
                await (WriteTerms ?? invoiceRepository.AddSearchTerms)(c.InvoiceId, missing);
                added++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not make the {Chain} transaction {TxHash} of invoice {InvoiceId} searchable; trying again later",
                    c.ChainName, c.TxHash, c.InvoiceId);
                complete = false;
            }
            if (Pace > TimeSpan.Zero)
                await Task.Delay(Pace, cancellationToken);
        }
        if (candidates.Count > 0)
            logger.LogInformation("Checked {Count} earlier LI.FI swaps for search terms; {Added} invoices got new ones", candidates.Count, added);
        return (complete, added);
    }

    async Task<List<(string InvoiceId, string Message)>> ReadLogLinesFromDb(CancellationToken cancellationToken)
    {
        await using var context = dbContextFactory.CreateContext();
        var rows = await context.Database.GetDbConnection().QueryAsync<InvoiceEventData>(new CommandDefinition(
            "SELECT \"InvoiceDataId\", \"Message\" FROM \"InvoiceEvents\" WHERE \"Message\" LIKE 'LI.FI reports the swap of %' OR \"Message\" LIKE 'LI.FI confirms the customer''s %'",
            cancellationToken: cancellationToken));
        return rows.Select(r => (r.InvoiceDataId, r.Message)).ToList();
    }

    async Task<Target?> ReadTargetFromDb(string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null)
            return null;
        foreach (var chain in SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Lifi))
            if (invoice.GetPaymentPrompt(chain.PaymentMethodId) is { Destination: { Length: > 0 } address })
                return new Target(address, SourceVerifier.AccountedTxIds(invoice.GetPayments(false)));
        return null;
    }

    async Task<HashSet<string>> ReadTermsFromDb(string invoiceId)
    {
        await using var context = dbContextFactory.CreateContext();
        var values = await context.Database.GetDbConnection().QueryAsync<string>(
            "SELECT \"Value\" FROM \"InvoiceSearches\" WHERE \"InvoiceDataId\"=@invoiceId", new { invoiceId });
        return values.ToHashSet(StringComparer.Ordinal);
    }
}
