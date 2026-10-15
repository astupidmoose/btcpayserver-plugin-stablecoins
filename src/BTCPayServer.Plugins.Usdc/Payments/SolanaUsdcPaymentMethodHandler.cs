#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Usdc.Solana;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Usdc.Payments;

/// <summary>
/// USDC on Solana as a BTCPay payment method. Every prompt pays the merchant's
/// wallet and carries its own Solana Pay reference key; the reference, not the
/// address, is what ties a transfer back to the invoice.
/// </summary>
public class SolanaUsdcPaymentMethodHandler(SolanaChainConfiguration chain) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => UsdcPlugin.SolanaUsdcPaymentMethodId;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    public SolanaChainConfiguration Chain => chain;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = UsdcPlugin.UsdcCurrency;
        context.Prompt.Divisibility = UsdcPlugin.UsdcDivisibility;
        return Task.CompletedTask;
    }

    public Task ConfigurePrompt(PaymentMethodContext context)
    {
        var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
        if (!SolanaPay.IsPublicKey(config.Address))
            throw new PaymentMethodUnavailableException("No valid Solana wallet address is configured for this store");

        var reference = SolanaPay.NewReference();
        var details = new SolanaUsdcPromptDetails
        {
            Reference = reference,
            Mint = chain.UsdcMint,
            Cluster = chain.Cluster
        };
        context.Prompt.Destination = config.Address;
        // The customer's wallet pays the network fee; nothing is added to the invoice.
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Details = JObject.FromObject(details, Serializer);
        // The reference is unique per prompt, so it is what goes into AddressInvoices.
        // The merchant address is shared by every invoice and must not be tracked.
        context.TrackedDestinations.Add(reference);
        context.AdditionalSearchTerms.Add(reference);
        return Task.CompletedTask;
    }

    public SolanaUsdcPaymentMethodConfig ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<SolanaUsdcPaymentMethodConfig>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SolanaUsdcPaymentMethodConfig)}");

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = validationContext.Config.ToObject<SolanaUsdcPaymentMethodConfig>(Serializer);
        if (!SolanaPay.IsPublicKey(config?.Address))
            validationContext.ModelState.AddModelError(nameof(SolanaUsdcPaymentMethodConfig.Address), "Invalid Solana address");
        return Task.CompletedTask;
    }

    public SolanaUsdcPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<SolanaUsdcPromptDetails>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SolanaUsdcPromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public SolanaUsdcPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<SolanaUsdcPaymentData>(Serializer)
        ?? throw new FormatException($"Invalid {nameof(SolanaUsdcPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
