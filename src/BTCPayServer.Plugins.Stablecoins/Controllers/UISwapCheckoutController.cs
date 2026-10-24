#nullable enable
using System;
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
using Microsoft.AspNetCore.Http;
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
    SourceVerifier verifier,
    CheckoutNotes notes,
    ILogger<UISwapCheckoutController> logger) : Controller
{

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
        if (!notes.AllowCaller(HttpContext.Connection.RemoteIpAddress?.ToString()))
            return StatusCode(429, new { error = "Too many requests." });
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice?.GetPaymentPrompt(pmi) is not { Activated: true, Destination: { } btcAddress })
            return NotFound();
        // Anyone with the invoice id can call this, so what it records is marked as the customer's claim.
        // Only a transaction LI.FI confirms pays this invoice's address becomes searchable (SourceVerifier).
        if (from is not null)
        {
            var paying = IsShortText(body!.Amount) && IsShortText(body.Token) ? $"{body.Amount} {body.Token}" : "tokens";
            await Note(invoiceId, $"from:{from}", $"Customer-reported, unverified: started paying {paying} on {chain.Name} from wallet {from}.");
        }
        if (txHash is not null &&
            await Note(invoiceId, $"tx:{txHash}", $"Customer-reported, unverified: {chain.Name} transaction {txHash}. Checking it with LI.FI. Track it at https://scan.li.fi/tx/{txHash}"))
            verifier.Enqueue(invoiceId, chain.Name, txHash, btcAddress);
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

    /// <summary>
    /// "Send USDC from an exchange" on a token checkout, or back to the wallet panel (null). The choice is this browser's own
    /// (a cookie), so nobody else can switch the customer's view; the deposit is quoted when the checkout next loads.
    /// </summary>
    [HttpPost("deposits/{invoiceId}/{paymentMethodId}/option")]
    public async Task<IActionResult> Option(string invoiceId, string paymentMethodId, [FromBody] OptionChoice body)
    {
        if (!notes.AllowCaller(HttpContext.Connection.RemoteIpAddress?.ToString()))
            return StatusCode(429, new { error = "Too many requests." });
        if (await TokenCheckout(invoiceId, paymentMethodId) is not var (tokens, config))
            return NotFound();
        var cookie = SwapToBtcCheckoutModelExtension.ChoiceCookie(invoiceId);
        if (body?.Option is not { } code)
        {
            Response.Cookies.Delete(cookie, new CookieOptions { Path = "/" });
            return Json(new { ok = true });
        }
        if (config.ExchangeOptionsFor(tokens).FirstOrDefault(o => o.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is not { } option)
            return BadRequest(new { error = "That option is not available for this payment." });
        Response.Cookies.Append(cookie, option.Code, new CookieOptions
        {
            Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps, MaxAge = TimeSpan.FromDays(2)
        });
        await Note(invoiceId, $"option:{option.Code}:{tokens.Code}",
            $"Customer chose to send {option.Ticker} on {tokens.Name} from an exchange (NEAR Intents).");
        return Json(new { ok = true });
    }

    /// <summary>Kept for checkouts loaded before 0.10: the network is always the token checkout's own.</summary>
    [HttpPost("deposits/{invoiceId}/{paymentMethodId}/{option}/network")]
    public async Task<IActionResult> Network(string invoiceId, string paymentMethodId, string option, [FromBody] NetworkChoice body)
    {
        if (await TokenCheckout(invoiceId, paymentMethodId) is not var (tokens, config) ||
            config.ExchangeOptionsFor(tokens).FirstOrDefault(o => o.Code.Equals(option, StringComparison.OrdinalIgnoreCase)) is null)
            return NotFound();
        return body?.Network == tokens.IntentsNetworkCode
            ? Json(new { ok = true })
            : BadRequest(new { error = "That network is not available for this payment." });
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
        if (!notes.TryAdd(invoiceId, key))
            return false;
        var logs = new InvoiceLogs();
        logs.Write(message, InvoiceEventData.EventSeverity.Info);
        await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        return true;
    }
}
