#nullable enable
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using BTCPayServer.Plugins.Stablecoins.Thorchain;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Stablecoins;

/// <summary>
/// Pay a BTC invoice from another chain: one payment method per source chain, each paying a fresh
/// address from the store's own BTC wallet. Tokens on EVM chains, Solana and Tron go through LI.FI;
/// native coins (Litecoin, Dogecoin, Zcash, Cosmos, XRP) go through THORChain.
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

        services.AddHttpClient(ThornodeClient.HttpClientName);
        services.AddSingleton(provider => new ThornodeClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(ThornodeClient.HttpClientName)));
        services.AddSingleton<ThorQuoteService>();
        services.AddHttpClient(MemolessClient.HttpClientName);
        services.AddSingleton(provider => new MemolessClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(MemolessClient.HttpClientName)));
        services.AddSingleton<ThorDepositService>();

        var btcTxLink = networkType == ChainName.Mainnet ? "https://mempool.space/tx/{0}" : "https://mempool.space/testnet/tx/{0}";
        foreach (var source in SwapSourceChain.All)
        {
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<SwapToBtcPaymentMethodHandler>(provider, source));
            services.AddSingleton<IPaymentMethodHandler>(provider => Handler(provider, source));
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
    }

    static SwapToBtcPaymentMethodHandler Handler(System.IServiceProvider provider, SwapSourceChain source) =>
        provider.GetServices<SwapToBtcPaymentMethodHandler>().Single(h => h.Chain == source);
}
