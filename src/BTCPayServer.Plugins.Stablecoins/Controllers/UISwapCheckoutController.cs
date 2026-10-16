#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Plugins.Stablecoins.Thorchain;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Controllers;

/// <summary>
/// Endpoints the swap checkouts call. Public like the checkout itself: they only quote swaps to the
/// invoice's own BTC address for the amount still due, and only add notes to the invoice's log.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("plugins/stablecoins")]
public class UISwapCheckoutController(
    InvoiceRepository invoiceRepository,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    ThorQuoteService quotes,
    ILogger<UISwapCheckoutController> logger) : Controller
{
    // Keeps one invoice from filling its log: a handful of source transactions and quotes is plenty.
    static readonly ConcurrentDictionary<string, byte> Logged = new();
    const int MaxNotesPerInvoice = 10;

    /// <summary>The THORChain deposit for an invoice: how much of the coin to send, to which vault, with which memo.</summary>
    [HttpGet("thorchain/{invoiceId}/{paymentMethodId}/deposit")]
    public async Task<IActionResult> Deposit(string invoiceId, string paymentMethodId, CancellationToken cancellationToken)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) ||
            SwapSourceChain.Find(pmi) is not { Provider: SwapProvider.Thorchain } chain ||
            !handlers.TryGetValue(pmi, out var h) || h is not SwapToBtcPaymentMethodHandler handler)
            return NotFound();
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice?.GetPaymentPrompt(pmi) is not { Activated: true, Destination: { } address } prompt)
            return NotFound();
        if (invoice.Status != InvoiceStatus.New)
            return Json(new { error = "This invoice can no longer be paid." });
        var due = prompt.Calculate().Due;
        if (due <= 0m)
            return Json(new { error = "Nothing is due on this invoice." });

        var store = await storeRepository.FindStore(invoice.StoreId);
        var configToken = store?.GetPaymentMethodConfig(pmi);
        var config = configToken is null ? new SwapToBtcPaymentMethodConfig() : handler.ParsePaymentMethodConfig(configToken);
        try
        {
            var deposit = await quotes.GetDeposit(chain, config, address, due, cancellationToken);
            var amount = deposit.Amount.ToString(CultureInfo.InvariantCulture);
            await Note(invoiceId, $"quote:{deposit.InboundAddress}:{deposit.Memo}:{amount}",
                $"THORChain quote for {chain.Name}: send {amount} {deposit.Ticker} to {deposit.InboundAddress} with memo {deposit.Memo}, " +
                $"valid until {deposit.Expiry:u}. Expected {ThornodeClient.FromUnits(deposit.ExpectedBtcOutUnits)} BTC to {address}.");
            return Json(new
            {
                ticker = deposit.Ticker,
                amount,
                inboundAddress = deposit.InboundAddress,
                memo = deposit.Memo,
                memoField = chain.MemoField,
                expectedBtc = ThornodeClient.FromUnits(deposit.ExpectedBtcOutUnits).ToString(CultureInfo.InvariantCulture),
                expiry = deposit.Expiry.ToUnixTimeSeconds(),
                totalSwapSeconds = deposit.TotalSwapSeconds,
                warning = deposit.Warning,
                // Wallets do not carry the memo in a payment URI, so the QR only fills in address and amount.
                paymentUri = chain.UriScheme is { } scheme ? $"{scheme}:{deposit.InboundAddress}?amount={amount}" : deposit.InboundAddress
            });
        }
        catch (ThornodeException ex)
        {
            return Json(new { error = ex.Message });
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            logger.LogWarning(ex, "THORNode unreachable while quoting invoice {InvoiceId}", invoiceId);
            return Json(new { error = "THORChain could not be reached. Try again in a moment." });
        }
    }

    public class SourceTransaction
    {
        public string? TxHash { get; set; }
        public long? ChainId { get; set; }
    }

    /// <summary>
    /// The checkout reports the customer's own transaction (signed in the LI.FI panel, or pasted for THORChain),
    /// so support can follow a swap that is slow, refunded or stuck from the invoice page.
    /// </summary>
    [HttpPost("swaps/{invoiceId}/{paymentMethodId}/source")]
    public async Task<IActionResult> Source(string invoiceId, string paymentMethodId, [FromBody] SourceTransaction body)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) || SwapSourceChain.Find(pmi) is not { } chain)
            return NotFound();
        var txHash = body?.TxHash?.Trim();
        if (!IsTransactionId(txHash))
            return BadRequest(new { error = "That does not look like a transaction ID." });
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice?.GetPaymentPrompt(pmi) is not { Activated: true })
            return NotFound();
        await Note(invoiceId, $"tx:{txHash}",
            $"Customer's {chain.Name} transaction for this swap: {txHash}. Track it at {ExplorerLink(chain, txHash!)}");
        return Json(new { ok = true });
    }

    public static bool IsTransactionId(string? txHash) =>
        txHash is { Length: >= 32 and <= 128 } && txHash.All(c => char.IsAsciiLetterOrDigit(c) || c is 'x' or 'X');

    /// <summary>LI.FI's explorer follows every step of a route; THORChain's explorer shows the swap and any refund.</summary>
    public static string ExplorerLink(SwapSourceChain chain, string txHash) => chain.Provider == SwapProvider.Lifi
        ? $"https://scan.li.fi/tx/{txHash}"
        : $"https://runescan.io/tx/{txHash}";

    async Task Note(string invoiceId, string key, string message)
    {
        var prefix = invoiceId + ":";
        if (Logged.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) >= MaxNotesPerInvoice ||
            !Logged.TryAdd(prefix + key, 0))
            return;
        var logs = new InvoiceLogs();
        logs.Write(message, InvoiceEventData.EventSeverity.Info);
        await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
    }
}
