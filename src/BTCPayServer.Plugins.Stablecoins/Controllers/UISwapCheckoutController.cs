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
/// The endpoint the token checkout calls. Public like the checkout itself: it only adds notes to the invoice's log.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("plugins/stablecoins")]
public class UISwapCheckoutController(
    InvoiceRepository invoiceRepository,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    Intents.IntentsDepositService intents,
    ILogger<UISwapCheckoutController> logger) : Controller
{
    // Keeps one invoice from filling its log: a handful of source transactions and quotes is plenty.
    static readonly ConcurrentDictionary<string, byte> Logged = new();
    const int MaxNotesPerInvoice = 10;

    public class SourceTransaction
    {
        public string? TxHash { get; set; }
        public long? ChainId { get; set; }
    }

    /// <summary>
    /// The checkout reports the customer's own transaction (signed in the wallet panel),
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

    public class NetworkChoice
    {
        public string? Network { get; set; }
    }

    /// <summary>The USDC or USDT checkout's network picker. The deposit address for that network is prepared in the background.</summary>
    [HttpPost("deposits/{invoiceId}/{paymentMethodId}/network")]
    public async Task<IActionResult> Network(string invoiceId, string paymentMethodId, [FromBody] NetworkChoice body)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) || SwapSourceChain.Find(pmi) is not { Provider: SwapProvider.Intents } chain)
            return NotFound();
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.Status != InvoiceStatus.New || invoice.GetPaymentPrompt(pmi) is not { Activated: true })
            return NotFound();
        var store = await storeRepository.FindStore(invoice.StoreId);
        var config = store?.GetPaymentMethodConfig<SwapToBtcPaymentMethodConfig>(pmi, handlers);
        if (config is null || body?.Network is not { } network || !intents.Select(chain, config, invoiceId, network))
            return BadRequest(new { error = "That network is not available for this payment." });
        await Note(invoiceId, $"network:{network}", $"Customer chose to pay {chain.Name} on {chain.Networks.First(n => n.Code == network).Name}.");
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
