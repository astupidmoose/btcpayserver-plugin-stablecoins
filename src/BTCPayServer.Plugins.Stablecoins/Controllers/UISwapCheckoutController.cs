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
    SourceVerifier verifier,
    Intents.IntentsDepositService intents,
    CheckoutNotes notes) : Controller
{

    public class SourceTransaction
    {
        public string? TxHash { get; set; }
        public long? ChainId { get; set; }
        /// <summary>The customer's wallet, reported when they start paying (before signing).</summary>
        public string? FromAddress { get; set; }
        public string? Token { get; set; }
        public string? Amount { get; set; }
        /// <summary>For a payment through NEAR Intents: the deposit address the wallet sent to.</summary>
        public string? DepositAddress { get; set; }
    }

    /// <summary>
    /// The checkout reports the customer's own transaction (signed in the wallet panel),
    /// so support can follow a swap that is slow, refunded or stuck from the invoice page.
    /// </summary>
    [HttpPost("swaps/{invoiceId}/{paymentMethodId}/source")]
    public async Task<IActionResult> Source(string invoiceId, string paymentMethodId, [FromBody] SourceTransaction body)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) || SwapSourceChain.Find(pmi) is not { Provider: SwapProvider.Lifi } chain)
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
        // The customer may have switched to another network in the checkout ("Pay from another network").
        var paidFrom = SwapSourceChain.FindLifi(body!.ChainId) ?? chain;
        var deposit = body.DepositAddress?.Trim();
        if (deposit is not null)
        {
            // Paid through NEAR Intents: only a deposit made for this invoice, and its tracker (not LI.FI's) follows it.
            if (txHash is null || !IsWalletAddress(deposit) || !intents.HasWalletDeposit(invoiceId, deposit))
                return BadRequest(new { error = "That is not a deposit of this invoice." });
            await Note(invoiceId, $"tx:{txHash}",
                $"Customer-reported, unverified: {paidFrom.Name} transaction {txHash}, sent from the customer's wallet to NEAR Intents deposit {deposit}.");
            return Json(new { ok = true });
        }
        // Anyone with the invoice id can call this, so what it records is marked as the customer's claim.
        // Only a transaction LI.FI confirms pays this invoice's address becomes searchable (SourceVerifier).
        if (from is not null)
        {
            var paying = IsShortText(body.Amount) && IsShortText(body.Token) ? $"{body.Amount} {body.Token}" : "tokens";
            await Note(invoiceId, $"from:{from}:{paidFrom.Code}", $"Customer-reported, unverified: started paying {paying} on {paidFrom.Name} from wallet {from}.");
        }
        if (txHash is not null &&
            await Note(invoiceId, $"tx:{txHash}", $"Customer-reported, unverified: {paidFrom.Name} transaction {txHash}. Checking it with LI.FI. Track it at https://scan.li.fi/tx/{txHash}"))
            await verifier.Report(invoiceId, pmi, paidFrom, txHash, btcAddress);
        return Json(new { ok = true });
    }

    /// <summary>The token the customer picked in the wallet panel, as LI.FI names it, and their wallet.</summary>
    public class WalletPayment
    {
        public long? ChainId { get; set; }
        public string? Token { get; set; }
        public int? Decimals { get; set; }
        public string? FromAddress { get; set; }
    }

    /// <summary>
    /// The wallet panel's NEAR Intents price for the picked token: how much of it delivers the BTC still due. A price check
    /// only; the panel compares it with LI.FI's and shows the lower one.
    /// </summary>
    [HttpPost("swaps/{invoiceId}/{paymentMethodId}/near/quote")]
    public async Task<IActionResult> NearQuote(string invoiceId, string paymentMethodId, [FromBody] WalletPayment body, CancellationToken cancellationToken)
    {
        if (!notes.AllowQuote(HttpContext.Connection.RemoteIpAddress?.ToString()))
            return StatusCode(429, new { error = "Too many requests." });
        if (await WalletCheckout(invoiceId, paymentMethodId, body) is not { } c)
            return NotFound();
        try
        {
            var quote = await intents.QuoteWallet(c.Config, c.StoreId, c.PaidFrom, c.Token, c.Decimals, c.FromAddress, c.BtcAddress, c.Due, cancellationToken);
            return Json(new
            {
                amountIn = quote.AmountIn.ToString(CultureInfo.InvariantCulture),
                decimals = quote.Decimals,
                amountInUsd = quote.AmountInUsd?.ToString(CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Most often a token NEAR Intents does not take: the panel then only uses LI.FI.
            return StatusCode(409, new { error = ex is Intents.IntentsWalletException ? ex.Message : "NEAR Intents could not price this payment." });
        }
    }

    /// <summary>
    /// The customer pressed Pay on a NEAR Intents price: makes (or reuses) the one-time deposit the wallet then sends the
    /// token to. Saved on the invoice before its address is returned.
    /// </summary>
    [HttpPost("swaps/{invoiceId}/{paymentMethodId}/near/deposit")]
    public async Task<IActionResult> NearDeposit(string invoiceId, string paymentMethodId, [FromBody] WalletPayment body)
    {
        if (!notes.AllowCaller(HttpContext.Connection.RemoteIpAddress?.ToString()))
            return StatusCode(429, new { error = "Too many requests." });
        if (await WalletCheckout(invoiceId, paymentMethodId, body) is not { } c)
            return NotFound();
        try
        {
            var deposit = await intents.CreateWalletDeposit(c.Config, c.StoreId, c.Pmi, invoiceId, c.PaidFrom, c.Token, c.Decimals, c.FromAddress,
                c.BtcAddress, c.Due, c.Expiry);
            return Json(new
            {
                depositAddress = deposit.Address,
                amountIn = deposit.AmountIn.ToString(CultureInfo.InvariantCulture),
                decimals = deposit.Decimals
            });
        }
        catch (Exception ex)
        {
            await Note(invoiceId, $"near-deposit-failed:{ex.Message}",
                $"Could not make a NEAR Intents deposit for the customer's {c.PaidFrom.Name} wallet payment: {ex.Message}", InvoiceEventData.EventSeverity.Warning);
            return StatusCode(409, new { error = "This payment could not be prepared. Please try again." });
        }
    }

    record WalletContext(PaymentMethodId Pmi, string StoreId, SwapToBtcPaymentMethodConfig Config, SwapSourceChain PaidFrom, string Token, int Decimals,
        string FromAddress, string BtcAddress, decimal Due, DateTimeOffset Expiry);

    /// <summary>
    /// A wallet payment the store takes through NEAR Intents: a token checkout of an unpaid invoice with BTC still due, the
    /// store's NEAR Intents option on, and a well-formed token and wallet on one of the token chains. Null otherwise.
    /// </summary>
    async Task<WalletContext?> WalletCheckout(string invoiceId, string paymentMethodId, WalletPayment? body)
    {
        if (!PaymentMethodId.TryParse(paymentMethodId, out var pmi) || SwapSourceChain.Find(pmi) is not { Provider: SwapProvider.Lifi } chain)
            return null;
        if (body is null || !IsWalletAddress(body.Token) || !IsWalletAddress(body.FromAddress) || body.Decimals is not (>= 0 and <= 36))
            return null;
        var paidFrom = body.ChainId is { } id ? SwapSourceChain.FindLifi(id) : chain;
        if (paidFrom is null)
            return null;
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null || invoice.Status != InvoiceStatus.New ||
            invoice.GetPaymentPrompt(pmi) is not { Activated: true, Destination: { } btcAddress } prompt)
            return null;
        var due = prompt.Calculate().Due;
        if (due <= 0m)
            return null;
        var store = await storeRepository.FindStore(invoice.StoreId);
        if (store?.GetPaymentMethodConfig<SwapToBtcPaymentMethodConfig>(pmi, handlers) is not { IntentsForWallets: true, Disabled: false } config)
            return null;
        return new WalletContext(pmi, invoice.StoreId, config, paidFrom, body.Token!.Trim(), body.Decimals.Value, body.FromAddress!.Trim(),
            btcAddress, due, invoice.ExpirationTime);
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
    async Task<bool> Note(string invoiceId, string key, string message, InvoiceEventData.EventSeverity severity = InvoiceEventData.EventSeverity.Info)
    {
        if (!notes.TryAdd(invoiceId, key))
            return false;
        var logs = new InvoiceLogs();
        logs.Write(message, severity);
        await invoiceRepository.AddInvoiceLogs(invoiceId, logs);
        return true;
    }
}
