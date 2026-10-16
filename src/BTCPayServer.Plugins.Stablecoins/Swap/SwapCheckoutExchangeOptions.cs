#nullable enable
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// The "from exchange" options (USDC, USDT by deposit address) are not tabs of their own on the checkout when the
/// invoice can be paid from a wallet on a token chain (LI.FI): they show as buttons at the bottom of those wallet
/// checkouts instead ("Send USDC from an exchange"). Without a token chain they stay as tabs, so they can still be reached.
/// </summary>
public class SwapCheckoutExchangeOptions : IGlobalCheckoutModelExtension
{
    public const string DataKey = "stablecoinsExchange";

    public static bool IsExchangeOption(PaymentMethodId id) =>
        SwapSourceChain.Find(id)?.Provider == SwapProvider.Intents;

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Model.AvailablePaymentMethods is { } methods && Apply(methods) is { } buttons)
            context.Model.AdditionalData[DataKey] = buttons;
    }

    /// <summary>Hides the exchange options' tabs and returns their buttons, or null when they stay as tabs.</summary>
    public static JArray? Apply(List<CheckoutModel.AvailablePaymentMethod> methods)
    {
        var exchange = methods.Where(m => m.Displayed && IsExchangeOption(m.PaymentMethodId)).ToList();
        if (exchange.Count == 0 || !methods.Any(m => m.Displayed && SwapSourceChain.Find(m.PaymentMethodId)?.Provider == SwapProvider.Lifi))
            return null;
        foreach (var m in exchange)
            m.Displayed = false;
        return new JArray(exchange.Select(m => new JObject
        {
            ["paymentMethodId"] = m.PaymentMethodId.ToString(),
            ["label"] = $"Send {SwapSourceChain.Find(m.PaymentMethodId)!.Ticker} from an exchange"
        }));
    }
}
