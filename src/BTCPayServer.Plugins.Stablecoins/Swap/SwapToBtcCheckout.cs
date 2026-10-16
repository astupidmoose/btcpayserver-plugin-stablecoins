#nullable enable
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// A plain BIP21 link for API clients (Greenfield's paymentLink). Any BTC sent to the address
/// counts toward the invoice, swapped or not.
/// </summary>
public class SwapToBtcPaymentLinkExtension(SwapToBtcPaymentMethodHandler handler) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => handler.PaymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) =>
        prompt.Destination is null
            ? null
            : handler.Network.GenerateBIP21(prompt.Destination, prompt.Calculate().Due).ToString();
}

/// <summary>
/// Keeps BTCPay's own checkout (store header, amount in BTC, expiry timer, View Details) and
/// replaces the QR code with the LI.FI panel, locked to this payment method's chain.
/// </summary>
public class SwapToBtcCheckoutModelExtension(SwapToBtcPaymentMethodHandler handler) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "StablecoinsSwapCheckout";
    public const string ThorCheckoutBodyComponentName = "StablecoinsThorCheckout";

    public PaymentMethodId PaymentMethodId => handler.PaymentMethodId;
    public string Image => handler.Chain.Image;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler != handler || context.Prompt.Destination is not { } address)
            return;
        var configToken = context.Store.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = configToken is null ? new SwapToBtcPaymentMethodConfig() : handler.ParsePaymentMethodConfig(configToken);

        context.Model.ShowRecommendedFee = false;
        context.Model.ShowPayInWalletButton = false;
        context.Model.Address = address;
        context.Model.InvoiceBitcoinUrl = null;
        context.Model.InvoiceBitcoinUrlQR = null;
        var chain = handler.Chain;
        var sourceUrl = context.UrlHelper.Content($"~/plugins/stablecoins/swaps/{context.InvoiceEntity.Id}/{handler.PaymentMethodId}/source");
        if (chain.Provider == SwapProvider.Thorchain)
        {
            context.Model.CheckoutBodyComponentName = ThorCheckoutBodyComponentName;
            context.Model.AdditionalData["stablecoinsThor"] = new JObject
            {
                ["chainName"] = chain.Name,
                ["ticker"] = chain.Ticker,
                ["memoField"] = chain.MemoField,
                ["btcAddress"] = address,
                ["sourceUrl"] = sourceUrl,
                ["depositUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/thorchain/{context.InvoiceEntity.Id}/{handler.PaymentMethodId}/deposit")
            };
            return;
        }
        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        context.Model.AdditionalData["stablecoinsSwap"] = new JObject
        {
            ["fromChain"] = chain.LifiChainId,
            ["chainName"] = chain.Name,
            ["btcAddress"] = address,
            ["integrator"] = string.IsNullOrWhiteSpace(config.Integrator) ? SwapDefaults.Integrator : config.Integrator,
            ["apiKey"] = config.ApiKey,
            ["fee"] = config.Fee,
            ["bufferPercent"] = config.BufferPercent,
            ["walletConnectProjectId"] = config.WalletConnectProjectId,
            ["invoiceLabel"] = $"{context.Store.StoreName} invoice",
            ["sourceUrl"] = sourceUrl
        };
    }
}
