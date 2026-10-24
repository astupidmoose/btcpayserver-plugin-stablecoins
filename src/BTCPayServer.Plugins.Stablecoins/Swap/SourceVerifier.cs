#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Checks the source transactions customers report with LI.FI before the invoice trusts them.
/// A reported hash is only written as this swap's transaction, and only made searchable, once LI.FI
/// confirms it pays this invoice: its BTC payout is a transaction this invoice received, or it names
/// this invoice's own BTC address. Anything else stays an unverified note.
/// For Bitcoin destinations LI.FI's toAddress is often a placeholder (0x11f1...11f1), not the BTC address,
/// so the payout transaction is what decides.
/// </summary>
public class SourceVerifier(LifiStatusClient lifi, InvoiceRepository invoiceRepository, ILogger<SourceVerifier> logger) : BackgroundService
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(2);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    /// <summary>Pending checks are bounded: beyond this, new reports are dropped (they are still in the log as unverified).</summary>
    public const int MaxPending = 2000;

    public record Pending(string InvoiceId, string ChainName, string TxHash, string BtcAddress, DateTimeOffset Since);

    readonly ConcurrentDictionary<string, Pending> _pending = new();
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public int PendingCount => _pending.Count;

    /// <summary>Where log lines and search terms go; replaced by tests.</summary>
    public Func<string, string, InvoiceEventData.EventSeverity, Task>? WriteLog { get; set; }
    public Func<string, List<string>, Task>? WriteSearchTerms { get; set; }
    /// <summary>The BTC transaction ids this invoice has received; replaced by tests.</summary>
    public Func<string, Task<HashSet<string>>>? GetPaymentTxIds { get; set; }

    public enum Verdict { Wait, Confirmed, PaysElsewhere, Failed }

    /// <summary>What a LI.FI status says about a reported transaction, given the BTC transactions the invoice received.</summary>
    public static Verdict Judge(LifiTransferStatus status, string btcAddress, ISet<string> paymentTxIds)
    {
        // A payout the invoice received proves it, whatever the status says.
        if (Normalize(status.Receiving?.TxHash) is { } payout && paymentTxIds.Contains(payout))
            return Verdict.Confirmed;
        if (status.Status is "FAILED" or "INVALID")
            return Verdict.Failed;
        if (string.Equals(status.ToAddress, btcAddress, StringComparison.OrdinalIgnoreCase))
            return Verdict.Confirmed;
        // A real BTC address that is not this invoice's. An EVM-shaped one is LI.FI's placeholder for a non-EVM receiver.
        if (status.ToAddress is { Length: > 0 } to && !to.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Verdict.PaysElsewhere;
        // Pending, or done but the BTC has not reached this invoice yet.
        return Verdict.Wait;
    }

    /// <summary>A BTC transaction id as BTCPay writes it: lower case, no 0x.</summary>
    public static string? Normalize(string? txHash)
    {
        if (string.IsNullOrWhiteSpace(txHash))
            return null;
        var h = txHash.Trim().ToLowerInvariant();
        return h.StartsWith("0x", StringComparison.Ordinal) ? h[2..] : h;
    }

    /// <summary>False when the hash is already being checked or the queue is full.</summary>
    public bool Enqueue(string invoiceId, string chainName, string txHash, string btcAddress)
    {
        if (_pending.Count >= MaxPending)
            return false;
        return _pending.TryAdd(invoiceId + ":" + txHash.ToLowerInvariant(), new Pending(invoiceId, chainName, txHash, btcAddress, Now()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAll(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not check reported source transactions");
            }
            await Task.Delay(CheckEvery, stoppingToken);
        }
    }

    public async Task CheckAll(CancellationToken cancellationToken)
    {
        foreach (var (key, p) in _pending.ToArray())
        {
            LifiTransferStatus? status;
            try
            {
                status = await lifi.Status(p.TxHash, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "LI.FI status of {TxHash} failed", p.TxHash);
                status = null;
            }
            var expired = Now() - p.Since > GiveUpAfter;
            if (status is null)
            {
                if (expired)
                {
                    _pending.TryRemove(key, out _);
                    await Log(p.InvoiceId, $"LI.FI never saw the {p.ChainName} transaction the customer reported ({p.TxHash}). It is not this swap's transaction.",
                        InvoiceEventData.EventSeverity.Warning);
                }
                continue;
            }
            // The invoice could not be read: try again later rather than judge without its payments.
            if (await PaymentTxIds(p.InvoiceId) is not { } received)
                continue;
            var verdict = Judge(status, p.BtcAddress, received);
            if (verdict == Verdict.Wait && !expired)
                continue;
            _pending.TryRemove(key, out _);
            if (verdict == Verdict.PaysElsewhere)
            {
                await Log(p.InvoiceId, $"The {p.ChainName} transaction the customer reported ({p.TxHash}) does not pay this invoice: LI.FI shows it paying {status.ToAddress}.",
                    InvoiceEventData.EventSeverity.Warning);
                continue;
            }
            if (verdict == Verdict.Failed)
            {
                await Log(p.InvoiceId, $"LI.FI reports the swap of the {p.ChainName} transaction the customer reported ({p.TxHash}) as {status.Status}" +
                                       (status.Substatus is { Length: > 0 } sub ? $" ({sub})" : "") + $". Details at https://scan.li.fi/tx/{p.TxHash}",
                    InvoiceEventData.EventSeverity.Warning);
                continue;
            }
            if (verdict == Verdict.Wait)
            {
                await Log(p.InvoiceId, $"Could not confirm that the {p.ChainName} transaction the customer reported ({p.TxHash}) pays this invoice: " +
                                       (status.Receiving?.TxHash is { Length: > 0 } btc
                                           ? $"LI.FI paid BTC in {btc}, which this invoice did not receive."
                                           : $"LI.FI still shows it as {status.Status}.") +
                                       $" Details at https://scan.li.fi/tx/{p.TxHash}", InvoiceEventData.EventSeverity.Warning);
                continue;
            }
            await Log(p.InvoiceId, $"LI.FI confirms the customer's {p.ChainName} transaction {p.TxHash} pays this invoice" +
                                   (status.FromAddress is { Length: > 0 } from ? $", from wallet {from}" : "") +
                                   (status.Receiving?.TxHash is { Length: > 0 } paid ? $", with BTC transaction {Normalize(paid)}" : "") +
                                   $". Track it at https://scan.li.fi/tx/{p.TxHash}", InvoiceEventData.EventSeverity.Info);
            var terms = new List<string> { p.TxHash };
            if (!string.IsNullOrEmpty(status.FromAddress)) terms.Add(status.FromAddress);
            if (Normalize(status.Receiving?.TxHash) is { } payout) terms.Add(payout);
            try
            {
                var distinct = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                await (WriteSearchTerms?.Invoke(p.InvoiceId, distinct) ?? invoiceRepository.AddSearchTerms(p.InvoiceId, distinct));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not add search terms to invoice {InvoiceId}", p.InvoiceId);
            }
        }
    }

    /// <summary>
    /// The BTC transactions that count toward the invoice. A payout replaced by fee bump is
    /// unaccounted and must not confirm a swap, or a stale payout would hide that the new one went elsewhere.
    /// </summary>
    public static HashSet<string> AccountedTxIds(IEnumerable<PaymentEntity> payments) =>
        // Payment ids of on-chain payments are outpoints: txid-index.
        payments.Where(p => p.Accounted).Select(p => Normalize(p.Id.Split('-')[0])!).ToHashSet();

    /// <summary>Null when the invoice could not be read.</summary>
    async Task<HashSet<string>?> PaymentTxIds(string invoiceId)
    {
        try
        {
            if (GetPaymentTxIds is not null)
                return await GetPaymentTxIds(invoiceId);
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            return invoice is null ? [] : AccountedTxIds(invoice.GetPayments(false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the payments of invoice {InvoiceId}", invoiceId);
            return null;
        }
    }

    async Task Log(string invoiceId, string message, InvoiceEventData.EventSeverity severity)
    {
        try
        {
            if (WriteLog is not null)
            {
                await WriteLog(invoiceId, message, severity);
                return;
            }
            var logs = new InvoiceLogs();
            logs.Write(message, severity);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write to the log of invoice {InvoiceId}", invoiceId);
        }
    }
}
