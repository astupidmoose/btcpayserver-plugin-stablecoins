#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Payments;
using BTCPayServer.Plugins.Stablecoins.Solana;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Services;

/// <summary>
/// Polls Solana for every invoice BTCPay is monitoring with an active USDC (Solana) prompt:
/// finds new transfers through the prompt's Solana Pay reference, records them as payments,
/// and moves them from Processing to Settled as their commitment level rises.
/// Invoice states (paid, partial, overpaid, expired, paid late) are then computed by
/// BTCPay itself from these payments, exactly as for Bitcoin.
/// </summary>
public class SolanaUsdcListener(
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    EventAggregator eventAggregator,
    PaymentMethodHandlerDictionary handlers,
    SolanaRpcClient rpc,
    SolanaChainConfiguration chain,
    ILogger<SolanaUsdcListener> logger) : BackgroundService
{
    static readonly PaymentMethodId Pmi = StablecoinsPlugin.SolanaUsdcPaymentMethodId;

    // Signatures already examined that did not pay this invoice (failed, wrong mint, wrong recipient).
    // Kept in memory so a poll does not refetch them; a restart simply re-examines them once.
    readonly ConcurrentDictionary<string, byte> _ignored = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("USDC (Solana) listener started on {Cluster} via {Rpc}", chain.Cluster, chain.RpcUri.Host);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Poll(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "USDC (Solana) poll failed");
            }
            try
            {
                await Task.Delay(chain.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task Poll(CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(Pmi, out var h) || h is not SolanaUsdcPaymentMethodHandler handler)
            return;
        var invoices = await invoiceRepository.GetMonitoredInvoices(Pmi, cancellationToken);
        foreach (var invoice in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PollInvoice(invoice, handler, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "USDC (Solana) poll failed for invoice {InvoiceId}", invoice.Id);
            }
        }
    }

    async Task PollInvoice(InvoiceEntity invoice, SolanaUsdcPaymentMethodHandler handler, CancellationToken cancellationToken)
    {
        var prompt = invoice.GetPaymentPrompt(Pmi);
        if (prompt is not { Activated: true, Destination: not null, Details: not null })
            return;
        var details = handler.ParsePaymentPromptDetails(prompt.Details);
        if (string.IsNullOrEmpty(details.Reference))
            return;

        var existing = invoice.GetPayments(false).Where(p => p.PaymentMethodId == Pmi).ToList();
        var updated = await UpdateExistingPayments(existing, handler, invoice.SpeedPolicy, cancellationToken);

        var known = existing.Select(p => p.Id).ToHashSet();
        var signatures = await rpc.GetSignaturesForAddress(details.Reference, cancellationToken: cancellationToken);
        foreach (var sig in signatures.OrderBy(s => s.Slot))
        {
            if (sig.Failed || known.Contains(sig.Signature) || _ignored.ContainsKey(IgnoreKey(invoice, sig.Signature)))
                continue;
            var tx = await rpc.GetTransaction(sig.Signature, cancellationToken);
            if (tx is null)
                continue; // Not served by the node yet, retry on the next poll.
            var (outcome, transfer) = UsdcTransferParser.Parse(tx, prompt.Destination, details.Mint, details.Reference);
            if (outcome != UsdcTransferParser.Outcome.Received || transfer is null)
            {
                logger.LogInformation("Ignoring {Signature} for invoice {InvoiceId}: {Outcome}", sig.Signature, invoice.Id, outcome);
                _ignored.TryAdd(IgnoreKey(invoice, sig.Signature), 0);
                continue;
            }
            await AddPayment(invoice, handler, details, transfer, sig.ConfirmationStatus);
        }

        if (updated)
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }

    async Task<bool> UpdateExistingPayments(List<PaymentEntity> payments, SolanaUsdcPaymentMethodHandler handler,
        SpeedPolicy speedPolicy, CancellationToken cancellationToken)
    {
        var pending = payments.Where(p => p.Status == PaymentStatus.Processing).ToList();
        if (pending.Count == 0)
            return false;
        var statuses = await rpc.GetSignatureStatuses(pending.Select(p => p.Id).ToList(), cancellationToken);
        var changed = new List<PaymentEntity>();
        foreach (var payment in pending)
        {
            if (!statuses.TryGetValue(payment.Id, out var status) || status is null)
                continue; // Unknown to the node right now; keep the current state.
            var data = handler.ParsePaymentDetails(payment.Details);
            var newStatus = status.Failed ? PaymentStatus.Unaccounted : GetStatus(status.ConfirmationStatus, speedPolicy);
            if (newStatus == payment.Status && data.ConfirmationStatus == status.ConfirmationStatus)
                continue;
            data.ConfirmationStatus = status.ConfirmationStatus;
            payment.Status = newStatus;
            payment.Details = JToken.FromObject(data, handler.Serializer);
            changed.Add(payment);
        }
        if (changed.Count == 0)
            return false;
        await paymentService.UpdatePayments(changed);
        return true;
    }

    async Task AddPayment(InvoiceEntity invoice, SolanaUsdcPaymentMethodHandler handler, SolanaUsdcPromptDetails details,
        UsdcTransfer transfer, string? confirmationStatus)
    {
        var data = new SolanaUsdcPaymentData
        {
            Signature = transfer.Signature,
            Slot = transfer.Slot,
            ConfirmationStatus = confirmationStatus,
            Payer = transfer.Payer,
            Reference = details.Reference
        };
        var paymentData = new PaymentData
        {
            // Payments are keyed by (Id, PaymentMethodId), so one transaction can never be
            // credited to two invoices even if it carries both references.
            Id = transfer.Signature,
            Created = transfer.BlockTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : DateTimeOffset.UtcNow,
            Status = GetStatus(confirmationStatus, invoice.SpeedPolicy),
            Amount = transfer.Amount,
            Currency = StablecoinsPlugin.UsdcCurrency,
        }.Set(invoice, handler, data);

        var payment = await paymentService.AddPayment(paymentData, [transfer.Signature]);
        if (payment is null)
        {
            logger.LogWarning("Transaction {Signature} is already recorded as a payment; not crediting invoice {InvoiceId} again",
                transfer.Signature, invoice.Id);
            _ignored.TryAdd(IgnoreKey(invoice, transfer.Signature), 0);
            return;
        }
        logger.LogInformation("Invoice {InvoiceId} received {Amount} USDC in {Signature}", invoice.Id, transfer.Amount, transfer.Signature);
        invoice = await invoiceRepository.GetInvoice(invoice.Id);
        eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    /// <summary>
    /// "confirmed" means a supermajority of stake voted on the block; "finalized" means it is
    /// rooted and cannot be rolled back (about 13 seconds later). Only the high-speed policy
    /// settles on confirmed.
    /// </summary>
    public static PaymentStatus GetStatus(string? confirmationStatus, SpeedPolicy speedPolicy) =>
        confirmationStatus switch
        {
            "finalized" => PaymentStatus.Settled,
            "confirmed" when speedPolicy == SpeedPolicy.HighSpeed => PaymentStatus.Settled,
            _ => PaymentStatus.Processing
        };

    static string IgnoreKey(InvoiceEntity invoice, string signature) => $"{invoice.Id}:{signature}";
}
