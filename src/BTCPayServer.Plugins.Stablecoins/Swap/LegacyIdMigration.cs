#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Version 0.2 stored each chain's settings under a dashed id (LIFI-BASE). WooCommerce cannot send those
/// back, so 0.3 uses underscores (LIFI_BASE). On startup this moves every store's settings, and its
/// enabled or disabled state, to the new id, so nothing has to be re-entered.
/// </summary>
public class LegacyIdMigration(StoreRepository storeRepository, ILogger<LegacyIdMigration> logger) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var store in await storeRepository.GetStores())
        {
            if (!Migrate(store))
                continue;
            await storeRepository.UpdateStore(store);
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
        foreach (var chain in SwapSourceChain.All)
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
}
