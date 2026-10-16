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
    const int MaxNotesPerInvoice = 20;

    public class SourceTransaction
    {
        public string? TxHash { get; set; }
        public long? ChainId { get; set; }
        /// <summary>The customer's wallet, reported when they start paying (before signing).</summary>
        public string? FromAddress { get; set; }
        public string? Token { get; set; }
        public string? Amount { get; set; }
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
        var from = body?.FromAddress?.Trim();
        if (txHash is not null && !IsTransactionId(txHash))
            return BadRequest(new { error = "That does not look like a transaction ID." });
        if (from is not null && !IsWalletAddress(from))
            return BadRequest(new { error = "That does not look like a wallet address." });
        if (txHash is null && from is null)
            return BadRequest(new { error = "Nothing to record." });
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice?.GetPaymentPrompt(pmi) is not { Activated: true })
            return NotFound();
        if (from is not null)
        {
            var paying = IsShortText(body!.Amount) && IsShortText(body.Token) ? $"{body.Amount} {body.Token}" : "tokens";
            if (await Note(invoiceId, $"from:{from}", $"Customer started paying {paying} on {chain.Name} from wallet {from}."))
                await invoiceRepository.AddSearchTerms(invoiceId, [from]);
        }
        if (txHash is not null &&
            await Note(invoiceId, $"tx:{txHash}", $"Customer's {chain.Name} transaction for this swap: {txHash}. Track it at {ExplorerLink(chain, txHash)}"))
            await invoiceRepository.AddSearchTerms(invoiceId, [txHash]);
        return Json(new { ok = true });
    }

    public class NetworkChoice
    {
        public string? Network { get; set; }
    }

    public class OptionChoice
    {
        /// <summary>USDC or USDT; null goes back to the wallet panel.</summary>
        public string? Option { get; set; }
    }

    /// <summary>"Send USDC from an exchange" on a token checkout, or back to the wallet panel.</summary>
    [HttpPost("deposits/{invoiceId}/{paymentMethodId}/option")]
    public async Task<IActionResult> Option(string invoiceId, string paymentMethodId, [FromBody] OptionChoice body)
    {
        if (await TokenCheckout(invoiceId, paymentMethodId) is not var (tokens, config))
            return NotFound();
        if (body?.Option is not { } code)
        {
            intents.Choose(invoiceId, null);
            return Json(new { ok = true });
        }
        if (config.ExchangeOptionsFor(tokens).FirstOrDefault(o => o.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is not { } option ||
            !intents.Select(option, config, invoiceId, tokens.IntentsNetworkCode!))
            return BadRequest(new { error = "That option is not available for this payment." });
        intents.Choose(invoiceId, option);
        await Note(invoiceId, $"option:{option.Code}:{tokens.Code}",
            $"Customer chose to send {option.Ticker} on {tokens.Name} from an exchange (NEAR Intents).");
        return Json(new { ok = true });
    }

    /// <summary>The exchange option's network picker. The deposit address for that network is prepared in the background.</summary>
    [HttpPost("deposits/{invoiceId}/{paymentMethodId}/{option}/network")]
    public async Task<IActionResult> Network(string invoiceId, string paymentMethodId, string option, [FromBody] NetworkChoice body)
    {
        if (await TokenCheckout(invoiceId, paymentMethodId) is not var (tokens, config) ||
            config.ExchangeOptionsFor(tokens).FirstOrDefault(o => o.Code.Equals(option, StringComparison.OrdinalIgnoreCase)) is not { } chain)
            return NotFound();
        if (body?.Network is not { } network || network != tokens.IntentsNetworkCode || !intents.Select(chain, config, invoiceId, network))
            return BadRequest(new { error = "That network is not available for this payment." });
        await Note(invoiceId, $"network:{chain.Code}:{network}", $"Customer chose to send {chain.Ticker} on {chain.Networks.First(n => n.Code == network).Name}.");
        return Json(new { ok = true });
    }

    /// <summary>A token checkout of an unpaid invoice and its settings, or null.</summary>
    async Task<(SwapSourceChain Chain, SwapToBtcPaymentMethodConfig Config)?> TokenCheckout(string invoiceId, string paymentMethodId)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) || SwapSourceChain.Find(pmi) is not { Provider: SwapProvider.Lifi } chain)
            return null;
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.Status != InvoiceStatus.New || invoice.GetPaymentPrompt(pmi) is not { Activated: true })
            return null;
        var store = await storeRepository.FindStore(invoice.StoreId);
        return store?.GetPaymentMethodConfig<SwapToBtcPaymentMethodConfig>(pmi, handlers) is { } config ? (chain, config) : null;
    }

    public static bool IsTransactionId(string? txHash) =>
        txHash is { Length: >= 32 and <= 128 } && txHash.All(c => char.IsAsciiLetterOrDigit(c) || c is 'x' or 'X');

    /// <summary>EVM (0x...), Solana and Tron addresses: letters and digits only.</summary>
    public static bool IsWalletAddress(string? address) =>
        address is { Length: >= 26 and <= 64 } && address.All(char.IsAsciiLetterOrDigit);

    static bool IsShortText(string? text) =>
        text is { Length: > 0 and <= 32 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>LI.FI's explorer follows every step of a route.</summary>
    public static string ExplorerLink(SwapSourceChain chain, string txHash) => $"https://scan.li.fi/tx/{txHash}";

    /// <summary>Adds a line to the invoice's log, once per key. False when it was already noted (or the invoice has enough notes).</summary>
    async Task<bool> Note(string invoiceId, string key, string message)
    {
        var prefix = invoiceId + ":";
        if (Logged.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) >= MaxNotesPerInvoice ||
            !Logged.TryAdd(prefix + key, 0))
            return false;
        var logs = new InvoiceLogs();
        logs.Write(message, InvoiceEventData.EventSeverity.Info);
        await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        return true;
    }
}
