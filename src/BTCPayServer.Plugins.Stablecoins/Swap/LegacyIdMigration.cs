#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Version 0.2 stored each chain's settings under a dashed id (LIFI-BASE). Some store integrations cannot send those
/// back, so 0.3 uses underscores (LIFI_BASE). On startup this moves every store's settings, and its
/// enabled or disabled state, to the new id, so nothing has to be re-entered.
/// Version 0.7 makes USDC and USDT from an exchange options of the token checkouts instead of payment methods
/// (NEAR_USDC, NEAR_USDT), so integrations stop listing them: their payment methods are removed, and whether
/// they were on is kept in the settings.
/// Version 0.8 drops THORChain (THOR_LITECOIN, THOR_ETH...): those payment methods are removed.
/// Version 0.10 keeps the API keys only in the store setting, and drops the per-store 1Click URL.
/// </summary>
public class LegacyIdMigration(StoreRepository storeRepository, ILogger<LegacyIdMigration> logger) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var store in await storeRepository.GetStores())
        {
            var exchange = MoveExchangeOptions(store);
            var thorchain = RemoveThorchain(store);
            var keys = StripSecrets(store);
            if (!Migrate(store) && exchange is null && !thorchain && keys is null)
                continue;
            if (keys is not null)
            {
                // Keep the keys in the store setting before removing them from the payment methods.
                var setting = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(store.Id, Controllers.UISwapStoreController.StoreSettingName)
                              ?? keys.Value.Config;
                setting.ApiKey ??= keys.Value.ApiKey;
                setting.IntentsApiKey ??= keys.Value.IntentsApiKey;
                await storeRepository.UpdateSetting(store.Id, Controllers.UISwapStoreController.StoreSettingName, setting);
            }
            await storeRepository.UpdateStore(store);
            if (exchange is { Length: > 0 })
            {
                var setting = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(store.Id, Controllers.UISwapStoreController.StoreSettingName);
                if (setting is not null)
                {
                    setting.ExchangeOptions = setting.ExchangeOptions.Union(exchange).ToArray();
                    await storeRepository.UpdateSetting(store.Id, Controllers.UISwapStoreController.StoreSettingName, setting);
                }
            }
            logger.LogInformation("Moved swap payment method settings of store {StoreId} to the new payment method ids", store.Id);
        }
    }

    public static PaymentMethodId LegacyId(SwapSourceChain chain) =>
        new(chain.PaymentMethodId.ToString().Replace('_', '-'));

    /// <summary>Returns true when the store changed.</summary>
    public static bool Migrate(StoreData store)
    {
        var changed = false;
        var blob = store.GetStoreBlob();
        foreach (var chain in SwapSourceChain.PaymentMethods)
        {
            var legacy = LegacyId(chain);
            var config = store.GetPaymentMethodConfig(legacy);
            if (config is null)
                continue;
            if (store.GetPaymentMethodConfig(chain.PaymentMethodId) is null)
            {
                store.SetPaymentMethodConfig(chain.PaymentMethodId, config);
                blob.SetExcluded(chain.PaymentMethodId, blob.GetExcludedPaymentMethods().Match(legacy));
            }
            store.SetPaymentMethodConfig(legacy, null);
            blob.SetExcluded(legacy, false);
            changed = true;
        }
        if (changed)
            store.SetStoreBlob(blob);
        return changed;
    }

    /// <summary>
    /// Removes the old NEAR_USDC / NEAR_USDT payment methods and writes the ones that were on into every token chain's settings.
    /// Returns the codes that were on, or null when the store had none of them.
    /// </summary>
    public static string[]? MoveExchangeOptions(StoreData store)
    {
        var blob = store.GetStoreBlob();
        var excluded = blob.GetExcludedPaymentMethods();
        var found = false;
        var on = new List<string>();
        foreach (var option in SwapSourceChain.ExchangeOptions)
        {
            if (store.GetPaymentMethodConfig(option.PaymentMethodId) is null)
                continue;
            found = true;
            if (!excluded.Match(option.PaymentMethodId))
                on.Add(option.Code);
            store.SetPaymentMethodConfig(option.PaymentMethodId, null);
            blob.SetExcluded(option.PaymentMethodId, false);
        }
        if (!found)
            return null;
        store.SetStoreBlob(blob);
        if (on.Count == 0)
            return [];
        foreach (var chain in SwapSourceChain.PaymentMethods.Where(c => c.Provider == SwapProvider.Lifi))
        {
            if (store.GetPaymentMethodConfig(chain.PaymentMethodId) is not JObject config)
                continue;
            var name = config.Properties().Any(p => p.Name == "Integrator") ? "ExchangeOptions" : "exchangeOptions";
            config[name] = new JArray(on);
            store.SetPaymentMethodConfig(chain.PaymentMethodId, config);
        }
        return on.ToArray();
    }

    static readonly string[] SecretFields = ["apiKey", "ApiKey", "intentsApiKey", "IntentsApiKey", "intentsUrl", "IntentsUrl"];

    /// <summary>
    /// Removes the API keys and the 1Click URL from every swap payment method (BTCPay's API returns those configs).
    /// Returns the keys found, and one config to seed the store setting with, or null when there were none.
    /// </summary>
    public static (string? ApiKey, string? IntentsApiKey, SwapToBtcPaymentMethodConfig Config)? StripSecrets(StoreData store)
    {
        string? apiKey = null, intentsApiKey = null;
        SwapToBtcPaymentMethodConfig? seed = null;
        var changed = false;
        foreach (var chain in SwapSourceChain.PaymentMethods)
        {
            if (store.GetPaymentMethodConfig(chain.PaymentMethodId) is not JObject config ||
                !config.Properties().Any(p => SecretFields.Contains(p.Name)))
                continue;
            seed ??= config.ToObject<SwapToBtcPaymentMethodConfig>();
            apiKey ??= Value(config, "apiKey", "ApiKey");
            intentsApiKey ??= Value(config, "intentsApiKey", "IntentsApiKey");
            foreach (var name in SecretFields)
                config.Remove(name);
            store.SetPaymentMethodConfig(chain.PaymentMethodId, config);
            changed = true;
        }
        return changed ? (apiKey, intentsApiKey, seed ?? new SwapToBtcPaymentMethodConfig()) : null;
    }

    static string? Value(JObject config, params string[] names) =>
        names.Select(n => config[n]?.Type == JTokenType.String ? config[n]!.ToString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Removes every THOR_ payment method. Returns true when the store changed.</summary>
    public static bool RemoveThorchain(StoreData store)
    {
        var ids = store.GetPaymentMethodConfigs().Keys.Where(id => id.ToString().StartsWith("THOR_", System.StringComparison.Ordinal)).ToList();
        if (ids.Count == 0)
            return false;
        var blob = store.GetStoreBlob();
        foreach (var id in ids)
        {
            store.SetPaymentMethodConfig(id, null);
            blob.SetExcluded(id, false);
        }
        store.SetStoreBlob(blob);
        return true;
    }
}
