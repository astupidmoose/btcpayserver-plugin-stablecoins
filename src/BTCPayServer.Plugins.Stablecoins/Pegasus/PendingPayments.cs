#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

public interface IPendingPayments
{
    /// <summary>
    /// Records the order's pending payment, or makes it count again, while settled BTC does not cover the order; retires it
    /// once settled BTC does. False when it could not be done and should be tried again; true when done or not needed.
    /// </summary>
    Task<bool> Sync(string invoiceId, PaymentMethodId pmi, string orderNumber, decimal btc, DateTimeOffset orderCreated, decimal? payoutBtc, bool first);
    /// <summary>False when it could not be done and should be tried again; true when done or not needed.</summary>
    Task<bool> Retire(string invoiceId, PaymentMethodId pmi, string? orderNumber, string reason);
    /// <summary>
    /// Retires each pending payment that the settled BTC received since it was recorded covers in full. Other BTC reaching
    /// the address (a direct payment, a late payout of an older order) leaves a larger pending payment in place, and BTC
    /// that could still be replaced does not count until it settles.
    /// </summary>
    Task RetireCovered(string invoiceId, PaymentMethodId pmi, string reason);
}

/// <summary>
/// A Monero payment the exchange has received but not yet paid out in BTC, recorded on the invoice as a Processing payment
/// for the BTC the order will pay. The invoice then shows as Processing (and does not expire) while the swap runs.
/// It never settles: the BTC that arrives replaces it, and it stops counting if the order is refunded, goes overdue,
/// or the BTC never comes.
/// </summary>
public class PendingPayments(
    PaymentService paymentService,
    InvoiceRepository invoiceRepository,
    PaymentMethodHandlerDictionary handlers,
    EventAggregator eventAggregator,
    ILogger<PendingPayments> logger) : IPendingPayments
{
    public static string PaymentId(string orderNumber) => "pegasus-" + orderNumber;

    public static bool IsPending(PaymentEntity payment, SwapToBtcPaymentMethodHandler handler) =>
        payment.PaymentMethodId == handler.PaymentMethodId && handler.ParsePaymentDetails(payment.Details).PendingOrder is not null;

    /// <summary>
    /// Whether the real BTC received since <paramref name="since"/> adds up to <paramref name="btc"/>. Only then can it be
    /// the order's payout: dust, or a late payout of an older (smaller) order, is not enough.
    /// </summary>
    public static bool Covers(IEnumerable<(DateTimeOffset Received, decimal Value)> real, DateTimeOffset since, decimal btc) =>
        real.Where(p => p.Received >= since).Sum(p => p.Value) >= btc;

    /// <summary>
    /// Real BTC that can no longer be replaced: only this retires a pending payment, which nothing would bring back if the
    /// BTC that retired it were double spent.
    /// </summary>
    static IEnumerable<(DateTimeOffset, decimal)> Settled(IEnumerable<PaymentEntity> payments, SwapToBtcPaymentMethodHandler handler) =>
        payments.Where(p => p.Status == PaymentStatus.Settled && !IsPending(p, handler)).Select(p => (p.ReceivedTime, p.Value));

    /// <summary>
    /// Brings the order's pending payment in line with the BTC on the invoice. It counts while the settled BTC received
    /// since the order was made does not cover the order (what the exchange says it paid out when it says so, so a short
    /// payout lets the customer pay the rest, otherwise <paramref name="btc"/>), and stops counting once it does. Settled
    /// BTC can still be demoted (a reorganization): the pending payment then counts again while the invoice still waits
    /// for payment. Unconfirmed BTC never stands in for it, since it could be replaced. A pending payment is recorded or
    /// made to count again on an invoice still waiting for payment (New, or Processing, which BTC that could still be
    /// replaced may have caused), and on one that expired: XMR the exchange only reports after expiry (sent just before
    /// it, or late) then shows the invoice as paid late straight away, as BTCPay does for any payment seen after expiry,
    /// for the merchant to accept, instead of only once the BTC arrives. Never on a settled or invalid invoice. Safe to
    /// call any number of times.
    /// </summary>
    public async Task<bool> Sync(string invoiceId, PaymentMethodId pmi, string orderNumber, decimal btc, DateTimeOffset orderCreated,
        decimal? payoutBtc, bool first)
    {
        try
        {
            if (!handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler || btc <= 0m)
                return true;
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return true;
            var payments = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi).ToList();
            var existing = payments.FirstOrDefault(p => p.Id == PaymentId(orderNumber));
            var need = payoutBtc is > 0m and var paid ? Math.Min(paid, btc) : btc;
            if (Covers(Settled(payments, handler), orderCreated, need))
                return existing is not { Accounted: true } ||
                       await Retire(invoiceId, pmi, orderNumber, $"the BTC of Pegasus Swap order {orderNumber} arrived and settled");
            var expired = invoice.Status == InvoiceStatus.Expired;
            var waiting = invoice.Status is InvoiceStatus.New or InvoiceStatus.Processing || expired;
            if (existing is { Accounted: true })
            {
                // Recorded by an earlier try that failed afterwards: make sure the invoice is looked at again.
                if (first)
                    eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
                return true;
            }
            if (!waiting)
                return true;
            if (existing is not null)
            {
                existing.Status = PaymentStatus.Processing;
                await paymentService.UpdatePayments([existing]);
                eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
                await Log(invoiceId, $"Pending payment of Pegasus Swap order {orderNumber} counted again: the BTC that replaced it no longer counts as settled.");
                return true;
            }
            var data = new SwapToBtcPaymentData { PendingOrder = orderNumber };
            var paymentData = new PaymentData
            {
                Id = PaymentId(orderNumber),
                Created = DateTimeOffset.UtcNow,
                Status = PaymentStatus.Processing,
                Amount = btc,
                Currency = handler.Network.CryptoCode
            }.Set(invoice, handler, data);
            var payment = await paymentService.AddPayment(paymentData);
            if (payment is null)
                return true;
            // The event first: the invoice turns Processing even if the log write below fails.
            eventAggregator.Publish(new InvoiceEvent(await invoiceRepository.GetInvoice(invoiceId) ?? invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
            await Log(invoiceId, $"Pegasus Swap order {orderNumber} received the customer's XMR. Recorded as a pending payment of {btc} BTC " +
                                 "until the BTC arrives and settles; it never settles on its own." +
                                 (expired ? " The exchange reported the XMR after the invoice expired, so the invoice shows as paid late: if the customer paid in time, mark it settled once the BTC arrives." : ""),
                expired ? InvoiceEventData.EventSeverity.Warning : InvoiceEventData.EventSeverity.Info);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the pending Monero payment of invoice {InvoiceId}; trying again later", invoiceId);
            return false;
        }
    }

    /// <summary>Stops counting the invoice's pending payments (only <paramref name="orderNumber"/>'s, when given).</summary>
    public async Task<bool> Retire(string invoiceId, PaymentMethodId pmi, string? orderNumber, string reason)
    {
        try
        {
            if (!handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler)
                return true;
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return true;
            var retired = new List<PaymentEntity>();
            foreach (var payment in invoice.GetPayments(false))
            {
                if (payment.Status == PaymentStatus.Unaccounted || !IsPending(payment, handler) ||
                    orderNumber is not null && payment.Id != PaymentId(orderNumber))
                    continue;
                payment.Status = PaymentStatus.Unaccounted;
                retired.Add(payment);
            }
            if (retired.Count == 0)
                return true;
            await paymentService.UpdatePayments(retired);
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
            await Log(invoiceId, $"Pending Monero payment no longer counted: {reason}.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not retire the pending Monero payment of invoice {InvoiceId}; trying again later", invoiceId);
            return false;
        }
    }

    public async Task RetireCovered(string invoiceId, PaymentMethodId pmi, string reason)
    {
        try
        {
            if (!handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler)
                return;
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return;
            var payments = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi && p.Accounted).ToList();
            var real = Settled(payments, handler).ToList();
            foreach (var pending in payments.Where(p => IsPending(p, handler)))
            {
                if (Covers(real, pending.ReceivedTime, pending.Value))
                    await Retire(invoiceId, pmi, handler.ParsePaymentDetails(pending.Details).PendingOrder, reason);
            }
        }
        catch (Exception ex)
        {
            // The swap follower retires it later (once the order has paid out), so nothing is lost.
            logger.LogWarning(ex, "Could not retire the pending Monero payments of invoice {InvoiceId}", invoiceId);
        }
    }

    /// <summary>Best effort: a log that cannot be written never undoes or repeats the change it describes.</summary>
    async Task Log(string invoiceId, string message, InvoiceEventData.EventSeverity severity = InvoiceEventData.EventSeverity.Info)
    {
        try
        {
            var logs = new InvoiceLogs();
            logs.Write(message, severity);
            await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not log to invoice {InvoiceId}", invoiceId);
        }
    }
}
