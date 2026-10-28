#nullable enable
using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Pegasus;

/// <summary>
/// The Monero checkout: BTCPay's own coin layout (amount in XMR, QR code, address), showing the Pegasus Swap order's
/// one-time deposit address. BTC, the swap and its provider never show.
/// </summary>
public class MoneroCheckoutModelExtension(
    SwapToBtcPaymentMethodHandler handler,
    PegasusSwapService swaps,
    DisplayFormatter displayFormatter) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "StablecoinsMoneroCheckout";
    public const string DataKey = "stablecoinsMonero";

    public PaymentMethodId PaymentMethodId => handler.PaymentMethodId;
    public string Image => handler.Chain.Image;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler != handler || context.Prompt.Destination is not { } btcAddress)
            return;
        var configToken = context.Store.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = configToken is null ? new SwapToBtcPaymentMethodConfig() : handler.ParsePaymentMethodConfig(configToken);
        var model = context.Model;
        var invoice = context.InvoiceEntity;
        var accounting = context.Prompt.Calculate();
        var rate = context.Prompt.Rate;
        var chain = handler.Chain;

        model.ShowRecommendedFee = false;
        model.NetworkFee = 0;
        model.TxCountForFee = 0;
        model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        model.CryptoImage = context.UrlHelper.Content("~/" + chain.Image);
        // Until the XMR amount is known, the amounts show in the invoice's currency and no address shows.
        model.ShowPayInWalletButton = false;
        model.Address = null;
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = "";
        model.Rate = null;
        model.PaymentMethodCurrency = invoice.Currency;
        if (rate > 0m)
        {
            string Fiat(decimal btc) => displayFormatter.Currency(btc * rate, invoice.Currency, DisplayFormatter.CurrencyFormat.None);
            model.Due = Fiat(accounting.Due);
            model.Paid = Fiat(accounting.Paid);
            model.OrderAmount = Fiat(accounting.TotalDue);
            model.OrderAmountFiat = null;
        }

        var data = new JObject();
        model.AdditionalData[DataKey] = data;
        if (invoice.Status != InvoiceStatus.New || accounting.Due <= 0m)
        {
            data["state"] = "done";
            return;
        }
        var status = swaps.Get(config, context.Store.Id, handler.PaymentMethodId, invoice.Id, btcAddress, accounting.Due);
        data["state"] = status.State.ToString().ToLowerInvariant();
        data["error"] = status.Error;
        if (status is not { State: PegasusState.Ready, Payment: { } payment })
            return;

        // Same layout as a coin invoice: the amount in XMR on top, then the QR code and the address.
        var perBtc = payment.Amount / accounting.Due;
        var places = PegasusSwapService.AmountDecimals;
        model.PaymentMethodCurrency = chain.Ticker;
        model.Due = SwapToBtcCheckoutModelExtension.Format(payment.Amount, places);
        model.Paid = accounting.Paid > 0m ? SwapToBtcCheckoutModelExtension.Format(accounting.Paid * perBtc, places) : "0";
        model.OrderAmount = SwapToBtcCheckoutModelExtension.Format(payment.Amount + (accounting.Paid > 0m ? accounting.Paid * perBtc : 0m), places);
        if (rate > 0m)
            model.Rate = displayFormatter.Currency(rate / perBtc, invoice.Currency, DisplayFormatter.CurrencyFormat.Symbol);
        model.Address = payment.Address;
        // A Monero payment URI: wallets that scan it fill in the address and the amount.
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR =
            $"monero:{payment.Address}?tx_amount={payment.Amount.ToString("0.############", CultureInfo.InvariantCulture)}";
        model.ShowPayInWalletButton = true;
        data["memo"] = payment.Memo;
    }
}
