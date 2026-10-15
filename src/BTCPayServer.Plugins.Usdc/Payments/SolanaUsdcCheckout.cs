#nullable enable
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Usdc.Solana;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Usdc.Payments;

public class SolanaUsdcPaymentLinkExtension(SolanaUsdcPaymentMethodHandler handler) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => UsdcPlugin.SolanaUsdcPaymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) => GetPaymentLink(prompt, label: null);

    public string? GetPaymentLink(PaymentPrompt prompt, string? label)
    {
        if (prompt.Destination is null || prompt.Details is null)
            return null;
        var details = handler.ParsePaymentPromptDetails(prompt.Details);
        var due = prompt.Calculate().Due;
        var invoiceId = prompt.ParentEntity?.Id;
        return SolanaPay.BuildTransferUrl(prompt.Destination, due, details.Mint, details.Reference,
            label: label,
            message: invoiceId is null ? null : $"Invoice {invoiceId}");
    }
}

/// <summary>
/// Reuses BTCPay's on-chain checkout body: QR code, amount and copy buttons.
/// The QR and wallet button carry a Solana Pay URL with the reference key.
/// </summary>
public class SolanaUsdcCheckoutModelExtension(SolanaUsdcPaymentLinkExtension paymentLink) : ICheckoutModelExtension
{
    public PaymentMethodId PaymentMethodId => UsdcPlugin.SolanaUsdcPaymentMethodId;
    public string Image => "Resources/usdc.svg";
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not SolanaUsdcPaymentMethodHandler)
            return;
        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;
        context.Model.InvoiceBitcoinUrl = paymentLink.GetPaymentLink(context.Prompt, label: context.Store.StoreName);
        context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
        context.Model.ShowPayInWalletButton = true;
    }
}
