#nullable enable
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Stablecoins;

/// <summary>
/// Pay a BTC invoice from another chain: one payment method per source chain, each paying a fresh
/// address from the store's own BTC wallet. Tokens on EVM chains, Solana and Tron go through LI.FI;
/// USDC and USDT from an exchange go through NEAR Intents deposit addresses, inside those same checkouts;
/// Monero goes through Pegasus Swap deposit addresses, as its own payment method.
/// </summary>
public class StablecoinsPlugin : BaseBTCPayServerPlugin
{
    public const string ViewsDirectory = "/Views/Stablecoins";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var networkType = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>().NetworkType;

        services.AddHttpClient(Intents.OneClickClient.HttpClientName);
        services.AddSingleton(provider => new Intents.OneClickClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(Intents.OneClickClient.HttpClientName)));
        services.AddSingleton<SwapSecrets>();
        services.AddSingleton<Intents.IntentsDepositService>();
        services.AddHostedService<Intents.IntentsTracker>();
        services.AddHttpClient(LifiStatusClient.HttpClientName);
        services.AddSingleton(provider => new LifiStatusClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(LifiStatusClient.HttpClientName)));
        services.AddSingleton<SourceVerifier>();
        services.AddHostedService(p => p.GetRequiredService<SourceVerifier>());
        services.AddSingleton<Controllers.CheckoutNotes>();
        services.AddHttpClient(Pegasus.PegasusClient.HttpClientName);
        services.AddSingleton(provider => new Pegasus.PegasusClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(Pegasus.PegasusClient.HttpClientName)));
        services.AddSingleton<Pegasus.IPendingPayments, Pegasus.PendingPayments>();
        services.AddSingleton<Pegasus.PegasusSwapService>();
        services.AddHostedService<Pegasus.PegasusTracker>();

        var btcTxLink = networkType == ChainName.Mainnet ? "https://mempool.space/tx/{0}" : "https://mempool.space/testnet/tx/{0}";
        foreach (var source in SwapSourceChain.PaymentMethods)
        {
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<SwapToBtcPaymentMethodHandler>(provider, source));
            services.AddSingleton<IPaymentMethodHandler>(provider => Handler(provider, source));
            if (source.Provider == SwapProvider.Pegasus)
                services.AddSingleton<ICheckoutModelExtension>(provider => ActivatorUtilities.CreateInstance<Pegasus.MoneroCheckoutModelExtension>(provider, Handler(provider, source)));
            else
                services.AddSingleton<ICheckoutModelExtension>(provider => ActivatorUtilities.CreateInstance<SwapToBtcCheckoutModelExtension>(provider, Handler(provider, source)));
            services.AddSingleton<IPaymentLinkExtension>(provider => new SwapToBtcPaymentLinkExtension(Handler(provider, source)));
            services.AddDefaultPrettyName(source.PaymentMethodId, source.Name);
            services.AddTransactionLinkProvider(source.PaymentMethodId, new DefaultTransactionLinkProvider(btcTxLink));
        }
        services.AddStartupTask<LegacyIdMigration>();
        services.AddSingleton<SwapToBtcListener>();
        services.AddHostedService(p => p.GetRequiredService<SwapToBtcListener>());

        services.AddUIExtension("store-wallets-nav", $"{ViewsDirectory}/StoreNavExtension.cshtml");
        services.AddUIExtension("store-invoices-payments", $"{ViewsDirectory}/ViewSwapToBtcPaymentData.cshtml");
        services.AddUIExtension("checkout-end", $"{ViewsDirectory}/SwapCheckout.cshtml");
        services.AddUIExtension("checkout-end", $"{ViewsDirectory}/MoneroCheckout.cshtml");
    }

    static SwapToBtcPaymentMethodHandler Handler(System.IServiceProvider provider, SwapSourceChain source) =>
        provider.GetServices<SwapToBtcPaymentMethodHandler>().Single(h => h.Chain == source);
}
