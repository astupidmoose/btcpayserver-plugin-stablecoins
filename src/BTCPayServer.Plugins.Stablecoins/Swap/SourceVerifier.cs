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
/// confirms it pays this invoice's own BTC address. Anything else stays an unverified note.
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
            if (status is null)
            {
                if (Now() - p.Since > GiveUpAfter)
                {
                    _pending.TryRemove(key, out _);
                    await Log(p.InvoiceId, $"LI.FI never saw the {p.ChainName} transaction the customer reported ({p.TxHash}). It is not this swap's transaction.",
                        InvoiceEventData.EventSeverity.Warning);
                }
                continue;
            }
            _pending.TryRemove(key, out _);
            if (!string.Equals(status.ToAddress, p.BtcAddress, StringComparison.OrdinalIgnoreCase))
            {
                await Log(p.InvoiceId, $"The {p.ChainName} transaction the customer reported ({p.TxHash}) does not pay this invoice: LI.FI shows it paying {status.ToAddress ?? "another address"}.",
                    InvoiceEventData.EventSeverity.Warning);
                continue;
            }
            await Log(p.InvoiceId, $"LI.FI confirms the customer's {p.ChainName} transaction {p.TxHash} pays this invoice" +
                                   (status.FromAddress is { Length: > 0 } from ? $", from wallet {from}" : "") +
                                   $" (status {status.Status}). Track it at https://scan.li.fi/tx/{p.TxHash}", InvoiceEventData.EventSeverity.Info);
            var terms = new List<string> { p.TxHash };
            if (!string.IsNullOrEmpty(status.FromAddress)) terms.Add(status.FromAddress);
            if (!string.IsNullOrEmpty(status.Receiving?.TxHash)) terms.Add(status.Receiving.TxHash);
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
