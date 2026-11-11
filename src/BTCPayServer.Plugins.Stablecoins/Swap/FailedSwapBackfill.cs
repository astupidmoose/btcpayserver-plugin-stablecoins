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
/// failed, refunded or paying this invoice, and not aimed at a BTC address that is not this invoice's. Only missing terms
/// are added. Nothing about payments changes. Optional work: no failure here ever stops BTCPay.
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

    /// <summary>
    /// The BTC addresses of the invoice's LI.FI payment methods (one per chain it offered; the customer may have paid
    /// from another network than the one picked), and the BTC transactions it received.
    /// </summary>
    public record Target(IReadOnlyList<string> BtcAddresses, HashSet<string> PaymentTxIds);

    // The verifier's own log lines: a failed or refunded swap, and a confirmed one.
    static readonly Regex FailedLine = new(@"^LI\.FI reports the swap of the (?<chain>.+?) transaction the customer reported \((?<tx>[^()\s]+)\) as ",
        RegexOptions.CultureInvariant);
    static readonly Regex ConfirmedLine = new(@"^LI\.FI confirms the customer's (?<chain>.+?) transaction (?<tx>\S+) pays this invoice",
        RegexOptions.CultureInvariant);

    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>After every LI.FI request, to stay well under its public rate limit.</summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(1);
    const int MaxAttempts = 24;

    // Replaced by tests.
    public Func<CancellationToken, Task<List<(string InvoiceId, string Message)>>>? ReadLogLines { get; set; }
    public Func<string, Task<Target?>>? ReadTarget { get; set; }
    public Func<string, Task<HashSet<string>>>? ReadTerms { get; set; }
    public Func<string, List<string>, Task>? WriteTerms { get; set; }
    public Func<Task<State?>>? ReadState { get; set; }
    public Func<State, Task>? WriteState { get; set; }
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Swaps already decided in an earlier pass of this run: a retry asks only about the others.</summary>
    readonly HashSet<(string InvoiceId, string TxHash)> _decided = new();
    /// <summary>Every swap was decided, but recording that failed: the next attempt only records it.</summary>
    bool _passComplete;

    public static Candidate? Parse(string invoiceId, string message)
    {
        var m = FailedLine.Match(message);
        if (!m.Success)
            m = ConfirmedLine.Match(message);
        return m.Success ? new Candidate(invoiceId, m.Groups["chain"].Value, m.Groups["tx"].Value) : null;
    }

    /// <summary>
    /// The invoice address a swap was aimed at: the one LI.FI names, when it is one of this invoice's. Otherwise any of
    /// them (LI.FI's placeholder or no receiver are judged the same against each; a real address that is not this
    /// invoice's is refused against each).
    /// </summary>
    public static string AddressFor(LifiTransferStatus status, IReadOnlyList<string> invoiceAddresses) =>
        invoiceAddresses.FirstOrDefault(a => string.Equals(a, status.ToAddress, StringComparison.OrdinalIgnoreCase)) ?? invoiceAddresses[0];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Delay(StartDelay, stoppingToken);
            for (var attempt = 0; attempt < MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
            {
                if (await Attempt(stoppingToken))
                    return;
                await Delay(RetryAfter, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One attempt: true once the backfill is done (now or by an earlier run). Never throws for a failure of its own
    /// (database, LI.FI, settings): it returns false and is tried again later.
    /// </summary>
    public async Task<bool> Attempt(CancellationToken cancellationToken)
    {
        try
        {
            if (!_passComplete)
            {
                if ((await (ReadState ?? (() => settingsRepository.GetSettingAsync<State>(SettingName)))())?.Version >= Version)
                    return true;
                _passComplete = (await RunOnce(cancellationToken)).Complete;
                if (!_passComplete)
                    return false;
            }
            var done = new State { Version = Version };
            await (WriteState ?? (s => settingsRepository.UpdateSetting(s, SettingName)))(done);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not make earlier LI.FI swaps searchable; trying again later");
            return false;
        }
    }

    /// <summary>
    /// One pass over every swap the verifier logged and this run has not decided yet. Complete when every one was decided;
    /// a swap LI.FI or the database could not answer for is tried again on the next pass. Added counts the invoices that
    /// got new terms.
    /// </summary>
    public async Task<(bool Complete, int Added)> RunOnce(CancellationToken cancellationToken)
    {
        var lines = await (ReadLogLines ?? ReadLogLinesFromDb)(cancellationToken);
        var candidates = lines.Select(l => Parse(l.InvoiceId, l.Message)).OfType<Candidate>()
            .DistinctBy(c => (c.InvoiceId, c.TxHash.ToLowerInvariant()))
            .Where(c => !_decided.Contains((c.InvoiceId, c.TxHash.ToLowerInvariant())))
            .ToList();
        bool complete = true;
        var added = 0;
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asked = false;
            try
            {
                switch (await Decide(c, () => asked = true, cancellationToken))
                {
                    case null:
                        complete = false;
                        continue;
                    case true:
                        added++;
                        break;
                }
                _decided.Add((c.InvoiceId, c.TxHash.ToLowerInvariant()));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not make the {Chain} transaction {TxHash} of invoice {InvoiceId} searchable; trying again later",
                    c.ChainName, c.TxHash, c.InvoiceId);
                complete = false;
            }
            finally
            {
                // Every request to LI.FI is paced, whatever its answer.
                if (asked && Pace > TimeSpan.Zero)
                    await Delay(Pace, cancellationToken);
            }
        }
        if (candidates.Count > 0)
            logger.LogInformation("Checked {Count} earlier LI.FI swaps for search terms; {Added} invoices got new ones", candidates.Count, added);
        return (complete, added);
    }

    /// <summary>True when terms were added, false when nothing is to be added, null when it must be asked again later.</summary>
    async Task<bool?> Decide(Candidate c, Action askingLifi, CancellationToken cancellationToken)
    {
        // An invoice that is gone, or has no LI.FI payment method: nothing to make searchable.
        if (await (ReadTarget ?? ReadTargetFromDb)(c.InvoiceId) is not { BtcAddresses.Count: > 0 } target)
            return false;
        // The chain the customer paid from, as the verifier logged it: LI.FI needs it to find some (Tron) transactions.
        var chain = SwapSourceChain.PaymentMethods.FirstOrDefault(x => x.Provider == SwapProvider.Lifi && x.Name == c.ChainName);
        askingLifi();
        var status = await lifi.Status(c.TxHash, chain?.LifiChainId, cancellationToken);
        // INVALID: a service LI.FI relies on is down. Unknown to LI.FI now: nothing to add.
        if (status?.Status == "INVALID")
            return null;
        if (status is null)
            return false;
        var address = AddressFor(status, target.BtcAddresses);
        var verdict = SourceVerifier.Judge(status, address, target.PaymentTxIds);
        var pending = new SourceVerifier.Pending(c.InvoiceId, c.ChainName, c.TxHash, address, DateTimeOffset.UtcNow);
        if (SourceVerifier.SearchTerms(verdict, status, pending) is not { } terms)
            return false;
        var existing = await (ReadTerms ?? ReadTermsFromDb)(c.InvoiceId);
        var missing = terms.Where(t => !existing.Contains(t)).ToList();
        if (missing.Count == 0)
            return false;
        await (WriteTerms ?? invoiceRepository.AddSearchTerms)(c.InvoiceId, missing);
        return true;
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
        var addresses = SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Lifi)
            .Select(c => invoice.GetPaymentPrompt(c.PaymentMethodId)?.Destination)
            .OfType<string>().Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return addresses.Count == 0 ? null : new Target(addresses, SourceVerifier.AccountedTxIds(invoice.GetPayments(false)));
    }

    async Task<HashSet<string>> ReadTermsFromDb(string invoiceId)
    {
        await using var context = dbContextFactory.CreateContext();
        var values = await context.Database.GetDbConnection().QueryAsync<string>(
            "SELECT \"Value\" FROM \"InvoiceSearches\" WHERE \"InvoiceDataId\"=@invoiceId", new { invoiceId });
        return values.ToHashSet(StringComparer.Ordinal);
    }
}
