#nullable enable
using System;
using System.Linq;
using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Intents;
using BTCPayServer.Rating;
using BTCPayServer.Services;
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
/// Keeps BTCPay's own checkout (store header, amount, expiry timer, View Details).
/// Token chains (Ethereum, Base...) show the amount in the invoice's currency and a wallet panel, plus
/// "Send USDC from an exchange", which looks like a normal coin invoice. BTC, the swap and its provider never show.
/// </summary>
public class SwapToBtcCheckoutModelExtension(
    SwapToBtcPaymentMethodHandler handler,
    IntentsDepositService intents,
    DisplayFormatter displayFormatter) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "StablecoinsSwapCheckout";
    /// <summary>The network picker, then BTCPay's own on-chain body.</summary>
    public const string DepositCheckoutBodyComponentName = "StablecoinsDepositCheckout";
    /// <summary>BTCPay's own on-chain checkout body, reused as is.</summary>
    public const string BitcoinCheckoutBodyComponentName = "BitcoinCheckoutBody";

    public PaymentMethodId PaymentMethodId => handler.PaymentMethodId;
    public string Image => handler.Chain.Image;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler != handler || context.Prompt.Destination is not { } address)
            return;
        var configToken = context.Store.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = configToken is null ? new SwapToBtcPaymentMethodConfig() : handler.ParsePaymentMethodConfig(configToken);
        var model = context.Model;
        var accounting = context.Prompt.Calculate();

        model.ShowRecommendedFee = false;
        model.NetworkFee = 0;
        model.TxCountForFee = 0;
        // Token checkouts also offer "Send USDC from an exchange": a deposit address paid into this checkout's BTC address.
        var options = config.ExchangeOptionsFor(handler.Chain).ToList();
        // NEAR Intents sets temporary minimums per network: hide the buttons on invoices it recently refused as too small.
        var chosenBefore = intents.Chosen(context.InvoiceEntity.Id);
        options = options.Where(o => o == chosenBefore || !intents.BelowMinimum(handler.Chain.IntentsNetworkCode, accounting.Due)).ToList();
        var chosen = intents.Chosen(context.InvoiceEntity.Id) is { } c && options.Contains(c) ? c : null;
        if (chosen is not null)
            ModifyForDeposit(context, config, chosen, address, accounting);
        else
            ModifyForTokens(context, config, address, accounting);
        if (options.Count > 0 && context.InvoiceEntity.Status == InvoiceStatus.New && accounting.Due > 0m)
            model.AdditionalData[ExchangeDataKey] = new JObject
            {
                ["chooseUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/deposits/{context.InvoiceEntity.Id}/{handler.PaymentMethodId}/option"),
                ["chosen"] = chosen?.Code,
                ["options"] = new JArray(options.Select(o => new JObject
                {
                    ["code"] = o.Code,
                    ["label"] = $"Send {o.Ticker} from an exchange"
                }))
            };
    }

    public const string ExchangeDataKey = "stablecoinsExchange";

    /// <summary>
    /// BTCPay's own on-chain checkout body, with a network picker above it.
    /// Until a network is picked, the amount shows in the invoice's currency; then in the coin, with its deposit address.
    /// </summary>
    void ModifyForDeposit(CheckoutModelContext context, SwapToBtcPaymentMethodConfig config, SwapSourceChain chain, string btcAddress,
        PaymentMethodAccounting accounting)
    {
        var model = context.Model;
        var invoice = context.InvoiceEntity;
        var rate = context.Prompt.Rate;
        model.CheckoutBodyComponentName = DepositCheckoutBodyComponentName;
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

        // Always the network of the checkout the customer is on: Base checkout, USDC on Base.
        var networks = IntentsDepositService.Networks(chain, config).Where(n => n.Code == handler.Chain.IntentsNetworkCode).ToList();
        var data = new JObject
        {
            ["symbol"] = chain.Ticker,
            ["networks"] = new JArray(networks.Select(n => new JObject { ["code"] = n.Code, ["name"] = n.Name })),
            ["fixed"] = true,
            ["selectUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/deposits/{invoice.Id}/{handler.PaymentMethodId}/{chain.Code}/network")
        };
        model.AdditionalData["stablecoinsDeposit"] = data;
        if (invoice.Status != InvoiceStatus.New || accounting.Due <= 0m)
        {
            data["state"] = "done";
            return;
        }

        var status = intents.Get(chain, config, invoice.Id, btcAddress, accounting.Due, invoice.ExpirationTime);
        // Came from another token checkout (Ethereum, then Base): switch to this one's network, unless a deposit is on its way.
        if (networks.Count == 1 && status.Network != networks[0].Code && intents.Select(chain, config, invoice.Id, networks[0].Code))
            status = intents.Get(chain, config, invoice.Id, btcAddress, accounting.Due, invoice.ExpirationTime);
        data["state"] = status.State.ToString().ToLowerInvariant();
        data["network"] = status.Network;
        data["networkName"] = chain.Networks.FirstOrDefault(n => n.Code == status.Network)?.Name;
        data["error"] = status.Error;
        if (status.Payment is not { } payment)
            return;
        data["minutes"] = payment.TimeEstimateSeconds is { } s ? (int)Math.Ceiling(s / 60.0) + 1 : null;
        var places = Math.Min(payment.Decimals, 6);
        if (status.Remaining is { } remaining)
            data["remaining"] = Format(remaining, places);
        if (status.State is not (IntentsState.Ready or IntentsState.Incomplete))
            return;

        // Same layout as a coin invoice: the amount in the coin on top, then the QR code and the address.
        // The QR code holds only the address: what an exchange's withdrawal form and most wallets scan.
        var perBtc = payment.Amount / accounting.Due;
        model.PaymentMethodCurrency = chain.Ticker;
        model.Due = Format(payment.Amount, places);
        model.Paid = accounting.Paid > 0m ? Format(accounting.Paid * perBtc, places) : "0";
        model.OrderAmount = Format(payment.Amount + (accounting.Paid > 0m ? accounting.Paid * perBtc : 0m), places);
        if (rate > 0m)
            model.Rate = displayFormatter.Currency(rate / perBtc, invoice.Currency, DisplayFormatter.CurrencyFormat.Symbol);
        model.Address = payment.Address;
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = payment.Address;
        model.CryptoImage = context.UrlHelper.Content("~/" + chain.Image);
        data["memo"] = payment.Memo;
    }

    void ModifyForTokens(CheckoutModelContext context, SwapToBtcPaymentMethodConfig config, string btcAddress, PaymentMethodAccounting accounting)
    {
        var chain = handler.Chain;
        var model = context.Model;
        var invoice = context.InvoiceEntity;
        var rate = context.Prompt.Rate;
        // Shown in the invoice's own currency: the customer picks which token to pay with in the panel.
        model.PaymentMethodCurrency = invoice.Currency;
        if (rate > 0m)
        {
            string Fiat(decimal btc) => displayFormatter.Currency(btc * rate, invoice.Currency, DisplayFormatter.CurrencyFormat.None);
            model.Due = Fiat(accounting.Due);
            model.Paid = Fiat(accounting.Paid);
            model.OrderAmount = Fiat(accounting.TotalDue);
            model.OrderAmountFiat = null;
        }
        var display = DisplayPrice(context);
        model.Rate = null;
        model.ShowPayInWalletButton = false;
        model.Address = null;
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = "";
        model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        model.AdditionalData["stablecoinsSwap"] = new JObject
        {
            ["fromChain"] = chain.LifiChainId,
            ["chainName"] = chain.Name,
            ["btcAddress"] = btcAddress,
            ["btcDue"] = accounting.Due.ToString(CultureInfo.InvariantCulture),
            ["active"] = invoice.Status == InvoiceStatus.New && accounting.Due > 0m,
            ["integrator"] = string.IsNullOrWhiteSpace(config.Integrator) ? SwapDefaults.Integrator : config.Integrator,
            ["apiKey"] = config.ApiKey,
            ["fee"] = config.Fee,
            ["bufferPercent"] = config.BufferPercent,
            ["walletConnectProjectId"] = config.WalletConnectProjectId,
            ["displayCurrency"] = display.Currency,
            ["btcPrice"] = display.BtcPrice?.ToString(CultureInfo.InvariantCulture),
            ["sourceUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/swaps/{invoice.Id}/{handler.PaymentMethodId}/source")
        };
    }

    /// <summary>
    /// The store's default currency and the BTC price in it, for the panel's "about 20.62 CAD" line.
    /// Falls back to the invoice's currency when the invoice has no rate for the store's default.
    /// </summary>
    static (string Currency, decimal? BtcPrice) DisplayPrice(CheckoutModelContext context)
    {
        var invoice = context.InvoiceEntity;
        var preferred = context.StoreBlob.DefaultCurrency;
        if (!string.IsNullOrEmpty(preferred) && !preferred.Equals(invoice.Currency, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (invoice.TryGetRate(new CurrencyPair("BTC", preferred.ToUpperInvariant()), out var price) && price > 0m)
                    return (preferred.ToUpperInvariant(), price);
            }
            catch
            {
                // No such rate on this invoice.
            }
        }
        return (invoice.Currency, context.Prompt.Rate > 0m ? context.Prompt.Rate : null);
    }

    /// <summary>Rounded up to what the coin can send, without trailing zeros past the coin's precision.</summary>
    public static string Format(decimal amount, int decimals)
    {
        var places = Math.Min(decimals, 8);
        var factor = 1m;
        for (var i = 0; i < places; i++) factor *= 10m;
        return (decimal.Ceiling(amount * factor) / factor).ToString("0." + new string('#', places), CultureInfo.InvariantCulture);
    }
}
