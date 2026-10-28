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
    Task Add(string invoiceId, PaymentMethodId pmi, string orderNumber, decimal btc);
    Task Retire(string invoiceId, PaymentMethodId pmi, string? orderNumber, string reason);
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

    /// <summary>Records the pending payment, unless BTC already arrived for the invoice or it was recorded before.</summary>
    public async Task Add(string invoiceId, PaymentMethodId pmi, string orderNumber, decimal btc)
    {
        try
        {
            if (!handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler || btc <= 0m)
                return;
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null || invoice.Status != InvoiceStatus.New)
                return;
            var payments = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi).ToList();
            if (payments.Any(p => p.Id == PaymentId(orderNumber)) || payments.Any(p => p.Accounted && !IsPending(p, handler)))
                return;
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
                return;
            await Log(invoiceId, $"Pegasus Swap order {orderNumber} received the customer's XMR. Recorded as a pending payment of {btc} BTC " +
                                 "until the BTC arrives; it never settles on its own.");
            invoice = await invoiceRepository.GetInvoice(invoiceId);
            eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the pending Monero payment of invoice {InvoiceId}", invoiceId);
        }
    }

    /// <summary>Stops counting the invoice's pending payments (only <paramref name="orderNumber"/>'s, when given).</summary>
    public async Task Retire(string invoiceId, PaymentMethodId pmi, string? orderNumber, string reason)
    {
        try
        {
            if (!handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler)
                return;
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return;
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
                return;
            await paymentService.UpdatePayments(retired);
            await Log(invoiceId, $"Pending Monero payment no longer counted: {reason}.");
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not retire the pending Monero payment of invoice {InvoiceId}", invoiceId);
        }
    }

    async Task Log(string invoiceId, string message)
    {
        var logs = new InvoiceLogs();
        logs.Write(message, InvoiceEventData.EventSeverity.Info);
        await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
    }
}
