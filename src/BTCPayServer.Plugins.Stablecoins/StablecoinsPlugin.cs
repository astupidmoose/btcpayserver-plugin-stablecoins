#nullable enable
using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Payments;
using BTCPayServer.Plugins.Stablecoins.Services;
using BTCPayServer.Plugins.Stablecoins.Solana;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Stablecoins;

public class StablecoinsPlugin : BaseBTCPayServerPlugin
{
    public const string UsdcCurrency = "USDC";
    public const int UsdcDivisibility = 6;
    public const string ViewsDirectory = "/Views/Stablecoins";

    public static readonly PaymentMethodId SolanaUsdcPaymentMethodId = new("USDC-SOLANA");

    public static readonly string[] DefaultRateRules =
    [
        "USDC_X = USDC_BTC * BTC_X",
        "USDC_BTC = kraken(USDC_USD) / kraken(BTC_USD)"
    ];

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var networkType = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>().NetworkType;
        var configuration = pluginServices.BootstrapServices.GetRequiredService<IConfiguration>();
        var chain = SolanaChainConfiguration.Create(
            // BTCPAY_SOLANA_CLUSTER=devnet lets a mainnet BTCPay test against devnet USDC, and vice versa.
            mainnet: configuration.GetOrDefault<string?>("solana_cluster", null) is { } cluster
                ? cluster.Equals("mainnet-beta", StringComparison.OrdinalIgnoreCase) || cluster.Equals("mainnet", StringComparison.OrdinalIgnoreCase)
                : networkType == ChainName.Mainnet,
            rpcOverride: configuration.GetOrDefault<Uri?>("solana_rpc_uri", null),
            pollSeconds: configuration.GetOrDefault<int?>("solana_poll_seconds", null));
        services.AddSingleton(chain);

        services.AddHttpClient(SolanaRpcClient.HttpClientName);
        services.AddSingleton(provider => new SolanaRpcClient(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(SolanaRpcClient.HttpClientName),
            chain));

        // USDC is not in BTCPay's currency table, and stores need a default rate rule for it.
        // Kraken quotes USDC/USD; BTC_X then converts to the store currency like any altcoin.
        services.AddCurrencyData(new CurrencyData
        {
            Code = UsdcCurrency,
            Name = "USD Coin",
            Divisibility = UsdcDivisibility,
            Symbol = null,
            Crypto = true
        });
        services.AddSingleton(new DefaultRules(DefaultRateRules));

        services.AddSingleton<SolanaUsdcPaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(p => p.GetRequiredService<SolanaUsdcPaymentMethodHandler>());
        services.AddSingleton<SolanaUsdcPaymentLinkExtension>();
        services.AddSingleton<IPaymentLinkExtension>(p => p.GetRequiredService<SolanaUsdcPaymentLinkExtension>());
        services.AddSingleton<ICheckoutModelExtension, SolanaUsdcCheckoutModelExtension>();
        services.AddDefaultPrettyName(SolanaUsdcPaymentMethodId, "USDC (Solana)");
        services.AddTransactionLinkProvider(SolanaUsdcPaymentMethodId, new DefaultTransactionLinkProvider(chain.TransactionLinkFormat));

        services.AddSingleton<SolanaUsdcListener>();
        services.AddHostedService(p => p.GetRequiredService<SolanaUsdcListener>());

        services.AddUIExtension("store-wallets-nav", $"{ViewsDirectory}/StoreNavExtension.cshtml");
        services.AddUIExtension("store-invoices-payments", $"{ViewsDirectory}/ViewSolanaUsdcPaymentData.cshtml");
    }
}
