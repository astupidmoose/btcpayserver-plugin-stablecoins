#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// "Pay from chain X": a BTC payment method whose prompt is a fresh address from the
/// store's own BTC wallet. The customer pays with tokens on chain X through LI.FI, and
/// LI.FI's bridge delivers native BTC to that address. Nothing is custodied by the plugin.
/// </summary>
public class SwapToBtcPaymentMethodHandler(
    SwapSourceChain chain,
    BTCPayNetworkProvider networkProvider,
    BTCPayWalletProvider walletProvider,
    ExplorerClientProvider explorerProvider,
    WalletRepository walletRepository,
    IServiceProvider serviceProvider) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => chain.PaymentMethodId;
    public SwapSourceChain Chain => chain;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    public BTCPayNetwork Network => networkProvider.BTC;

    class Prepare
    {
        public required Task<KeyPathInformation> ReserveAddress;
        public required DerivationStrategyBase AccountDerivation;
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = Network.CryptoCode;
        context.Prompt.Divisibility = Network.Divisibility;
        if (!context.Prompt.Activated)
            return Task.CompletedTask;
        // Resolved lazily: the handler dictionary is built from all handlers, including this one.
        var handlers = serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var settings = context.Store.GetDerivationSchemeSettings(handlers, Network.CryptoCode);
        if (settings?.AccountDerivation is null)
            throw new PaymentMethodUnavailableException("The store has no BTC on-chain wallet to receive the swapped BTC");
        context.State = new Prepare
        {
            AccountDerivation = settings.AccountDerivation,
            ReserveAddress = walletProvider.GetWallet(Network).ReserveAddressAsync(context.Store.Id, settings.AccountDerivation, $"lifi-{chain.Code.ToLowerInvariant()}")
        };
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!explorerProvider.IsAvailable(Network))
            throw new PaymentMethodUnavailableException("BTC full node not available");
        var prepare = context.State as Prepare
            ?? throw new PaymentMethodUnavailableException("The store has no BTC on-chain wallet to receive the swapped BTC");
        var prompt = context.Prompt;

        if (context.InvoiceEntity.Type != InvoiceType.TopUp)
        {
            var txOut = Network.NBitcoinNetwork.Consensus.ConsensusFactory.CreateTxOut();
            txOut.ScriptPubKey = new Key().GetScriptPubKey(prepare.AccountDerivation.ScriptPubKeyType());
            if (prompt.Calculate().Due < txOut.GetDustThreshold().ToDecimal(MoneyUnit.BTC))
                throw new PaymentMethodUnavailableException("Amount below the BTC dust threshold");
        }

        var reserved = await prepare.ReserveAddress;
        prompt.Destination = reserved.Address.ToString();
        // The customer pays the source chain's gas and the bridge fees inside the swap.
        prompt.PaymentMethodFee = 0m;
        // Our own (address, payment method) row in AddressInvoices. BTCPay's own listener only
        // looks up BTC-CHAIN rows, so it never credits this address to the plain BTC method.
        context.TrackedDestinations.Add(Network.GetTrackedDestination(reserved.Address.ScriptPubKey));
        prompt.Details = JObject.FromObject(new SwapToBtcPromptDetails
        {
            AccountDerivation = prepare.AccountDerivation.ToString(),
            KeyPath = reserved.KeyPath?.ToString(),
            KeyIndex = reserved.Index ?? (int)(reserved.KeyPath?.Indexes.Last() ?? 0),
            FromChainId = chain.LifiChainId
        }, Serializer);
    }

    /// <summary>Labels the address with the invoice in the store's BTC wallet, like BTCPay does for its own addresses.</summary>
    public async Task AfterSavingInvoice(PaymentMethodContext context)
    {
        if (context.Prompt.Destination is not { } address)
            return;
        var walletId = new WalletId(context.Store.Id, Network.CryptoCode);
        var invoiceObject = new WalletObjectId(walletId, WalletObjectData.Types.Invoice, context.InvoiceEntity.Id);
        await walletRepository.EnsureWalletObject(invoiceObject);
        await walletRepository.EnsureCreated(null,
        [
            WalletRepository.NewWalletObjectLinkData(new WalletObjectId(walletId, WalletObjectData.Types.Address, address), invoiceObject)
        ]);
    }

    public void StripDetailsForNonOwner(object details)
    {
        if (details is SwapToBtcPromptDetails d)
        {
            d.AccountDerivation = null;
            d.KeyPath = null;
        }
    }

    public SwapToBtcPaymentMethodConfig ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<SwapToBtcPaymentMethodConfig>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SwapToBtcPaymentMethodConfig)}");

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = validationContext.Config.ToObject<SwapToBtcPaymentMethodConfig>(Serializer);
        if (config is null)
            validationContext.ModelState.AddModelError("config", "Invalid configuration");
        else
            foreach (var error in Validate(config))
                validationContext.ModelState.AddModelError(error.Field, error.Message);
        return Task.CompletedTask;
    }

    public static IEnumerable<(string Field, string Message)> Validate(SwapToBtcPaymentMethodConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Integrator) || config.Integrator.Length > 23 ||
            !config.Integrator.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            yield return (nameof(config.Integrator), "Use 1 to 23 letters, digits, dashes, dots or underscores.");
        if (config.Fee is < 0m or >= 0.1m)
            yield return (nameof(config.Fee), "The fee must be at least 0 and below 10%.");
        if (config.BufferPercent is < 0m or > 5m)
            yield return (nameof(config.BufferPercent), "The buffer must be between 0% and 5%.");
    }

    public SwapToBtcPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<SwapToBtcPromptDetails>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SwapToBtcPromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public SwapToBtcPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<SwapToBtcPaymentData>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SwapToBtcPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
