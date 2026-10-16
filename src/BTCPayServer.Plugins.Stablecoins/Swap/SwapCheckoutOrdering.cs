#nullable enable
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Puts the "from exchange" options (USDC, USDT by deposit address) after every other way to pay on the checkout,
/// whichever payment method is showing. BTCPay keeps the list order for the tabs.
/// </summary>
public class SwapCheckoutOrdering : IGlobalCheckoutModelExtension
{
    public static bool IsLast(PaymentMethodId id) =>
        SwapSourceChain.Find(id)?.Provider == SwapProvider.Intents;

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Model.AvailablePaymentMethods is { } methods)
            context.Model.AvailablePaymentMethods = Reorder(methods);
    }

    public static List<CheckoutModel.AvailablePaymentMethod> Reorder(List<CheckoutModel.AvailablePaymentMethod> methods) =>
        methods.OrderBy(m => IsLast(m.PaymentMethodId)).ToList();
}
