#nullable enable
using System;
using System.Linq;
using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Stablecoins.Intents;
using BTCPayServer.Plugins.Stablecoins.Thorchain;
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
/// Native coins (Litecoin, Dogecoin...) look exactly like a normal BTCPay invoice in that coin: amount in the coin,
/// QR code, address, Pay in wallet. Token chains (Ethereum, Base...) show the amount in the invoice's currency and
/// a wallet panel. BTC, the swap and its provider never show to the customer.
/// </summary>
public class SwapToBtcCheckoutModelExtension(
    SwapToBtcPaymentMethodHandler handler,
    ThorDepositService deposits,
    IntentsDepositService intents,
    DisplayFormatter displayFormatter) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "StablecoinsSwapCheckout";
    public const string CoinStatusComponentName = "StablecoinsCoinStatus";
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
        if (handler.Chain.Provider == SwapProvider.Thorchain)
            ModifyForCoin(context, config, address, accounting);
        else if (handler.Chain.Provider == SwapProvider.Intents)
            ModifyForDeposit(context, config, address, accounting);
        else
            ModifyForTokens(context, config, address, accounting);
    }

    void ModifyForCoin(CheckoutModelContext context, SwapToBtcPaymentMethodConfig config, string btcAddress, PaymentMethodAccounting accounting)
    {
        var chain = handler.Chain;
        var model = context.Model;
        var invoice = context.InvoiceEntity;
        model.PaymentMethodCurrency = chain.Ticker;
        model.Address = null;
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = "";
        model.Rate = null;

        if (invoice.Status != InvoiceStatus.New || accounting.Due <= 0m)
        {
            model.CheckoutBodyComponentName = CoinStatusComponentName;
            model.Due = model.OrderAmount = model.Paid = "";
            model.AdditionalData["stablecoinsCoin"] = new JObject { ["state"] = "done", ["ticker"] = chain.Ticker };
            return;
        }

        var status = deposits.Get(chain, config, invoice.Id, btcAddress, accounting.Due);
        if (status is not { State: ThorPaymentState.Ready, Payment: { } payment } || payment.Used)
        {
            model.CheckoutBodyComponentName = CoinStatusComponentName;
            model.Due = model.OrderAmount = model.Paid = "";
            model.AdditionalData["stablecoinsCoin"] = new JObject
            {
                ["state"] = status.Payment?.Used == true ? "received" : status.State == ThorPaymentState.Failed ? "failed" : "preparing",
                ["ticker"] = chain.Ticker,
                ["error"] = status.Error,
                ["minutes"] = status.Payment?.TotalSwapSeconds is { } s ? (int)Math.Ceiling(s / 60.0) + 1 : null
            };
            return;
        }

        // The coin amount stands for the BTC still due; earlier payments are shown in the coin at the same ratio.
        var perBtc = payment.Amount / accounting.Due;
        var amount = Format(payment.Amount, chain.Decimals);
        model.CheckoutBodyComponentName = BitcoinCheckoutBodyComponentName;
        model.Address = payment.Address;
        model.Due = amount;
        model.Paid = accounting.Paid > 0m ? Format(accounting.Paid * perBtc, chain.Decimals) : "0";
        model.OrderAmount = Format(payment.Amount + (accounting.Paid > 0m ? accounting.Paid * perBtc : 0m), chain.Decimals);
        if (context.Prompt.Rate > 0m)
            model.Rate = displayFormatter.Currency(context.Prompt.Rate / perBtc, invoice.Currency, DisplayFormatter.CurrencyFormat.Symbol);
        var uri = chain.PaymentUri(payment.Address, payment.Amount, amount);
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = uri ?? payment.Address;
        model.ShowPayInWalletButton = model.ShowPayInWalletButton && uri is not null;
    }

    /// <summary>
    /// BTCPay's own on-chain checkout body, with a network picker above it.
    /// Until a network is picked, the amount shows in the invoice's currency; then in the coin, with its deposit address.
    /// </summary>
    void ModifyForDeposit(CheckoutModelContext context, SwapToBtcPaymentMethodConfig config, string btcAddress, PaymentMethodAccounting accounting)
    {
        var chain = handler.Chain;
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

        var networks = IntentsDepositService.Networks(chain, config).ToList();
        var data = new JObject
        {
            ["symbol"] = chain.Ticker,
            ["networks"] = new JArray(networks.Select(n => new JObject { ["code"] = n.Code, ["name"] = n.Name })),
            ["selectUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/deposits/{invoice.Id}/{handler.PaymentMethodId}/network")
        };
        model.AdditionalData["stablecoinsDeposit"] = data;
        if (invoice.Status != InvoiceStatus.New || accounting.Due <= 0m)
        {
            data["state"] = "done";
            return;
        }

        var status = intents.Get(chain, config, invoice.Id, btcAddress, accounting.Due, invoice.ExpirationTime);
        data["state"] = status.State.ToString().ToLowerInvariant();
        data["network"] = status.Network;
        data["networkName"] = networks.FirstOrDefault(n => n.Code == status.Network)?.Name;
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
            ["sourceUrl"] = context.UrlHelper.Content($"~/plugins/stablecoins/swaps/{invoice.Id}/{handler.PaymentMethodId}/source")
        };
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
