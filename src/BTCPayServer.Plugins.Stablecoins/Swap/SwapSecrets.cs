#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Stablecoins.Controllers;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// The store's API keys live only in the store setting, never in the payment method configs
/// (which BTCPay's API returns to anyone with store access). Cached here for the checkout.
/// </summary>
public class SwapSecrets(StoreRepository storeRepository)
{
    public record Keys(string? LifiApiKey, string? IntentsApiKey, string? PegasusPublicKey = null, string? PegasusSecret = null);

    readonly ConcurrentDictionary<string, (Keys Keys, DateTimeOffset At)> _cache = new();
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public async Task<Keys> GetAsync(string storeId)
    {
        if (_cache.TryGetValue(storeId, out var c) && DateTimeOffset.UtcNow - c.At < CacheFor)
            return c.Keys;
        var settings = await storeRepository.GetSettingAsync<SwapToBtcPaymentMethodConfig>(storeId, UISwapStoreController.StoreSettingName);
        var keys = new Keys(Clean(settings?.ApiKey), Clean(settings?.IntentsApiKey), Clean(settings?.PegasusPublicKey), Clean(settings?.PegasusSecret));
        _cache[storeId] = (keys, DateTimeOffset.UtcNow);
        return keys;
    }

    /// <summary>For the checkout model, which BTCPay builds synchronously. Served from the cache after the first call.</summary>
    public Keys Get(string storeId) =>
        _cache.TryGetValue(storeId, out var c) && DateTimeOffset.UtcNow - c.At < CacheFor ? c.Keys : GetAsync(storeId).GetAwaiter().GetResult();

    public void Set(string storeId, SwapToBtcPaymentMethodConfig settings) =>
        _cache[storeId] = (new Keys(Clean(settings.ApiKey), Clean(settings.IntentsApiKey), Clean(settings.PegasusPublicKey), Clean(settings.PegasusSecret)), DateTimeOffset.UtcNow);

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
