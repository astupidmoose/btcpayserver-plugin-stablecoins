using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class LegacyIdMigrationTests
{
    [Fact]
    public void MovesDashedSettingsToTheNewIdsAndKeepsTheirState()
    {
        var store = new StoreData { Id = "store" };
        var settings = new JObject { ["integrator"] = "my-store", ["apiKey"] = "secret-key", ["walletConnectProjectId"] = "wc" };
        store.SetPaymentMethodConfig(new PaymentMethodId("LIFI-BASE"), settings);
        store.SetPaymentMethodConfig(new PaymentMethodId("LIFI-ETHEREUM"), settings);
        store.SetPaymentMethodConfig(new PaymentMethodId("BTC-CHAIN"), new JValue("xpub-placeholder"));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(new PaymentMethodId("LIFI-ETHEREUM"), true);
        store.SetStoreBlob(blob);

        Assert.True(LegacyIdMigration.Migrate(store));

        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("LIFI-BASE")));
        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("LIFI-ETHEREUM")));
        Assert.Equal("secret-key", store.GetPaymentMethodConfig(new PaymentMethodId("LIFI_BASE"))!["apiKey"]!.ToString());
        Assert.Equal("my-store", store.GetPaymentMethodConfig(new PaymentMethodId("LIFI_ETHEREUM"))!["integrator"]!.ToString());
        Assert.NotNull(store.GetPaymentMethodConfig(new PaymentMethodId("BTC-CHAIN")));
        var excluded = store.GetStoreBlob().GetExcludedPaymentMethods();
        Assert.False(excluded.Match(new PaymentMethodId("LIFI_BASE")));
        Assert.True(excluded.Match(new PaymentMethodId("LIFI_ETHEREUM")));

        // Running again changes nothing.
        Assert.False(LegacyIdMigration.Migrate(store));
    }

    [Fact]
    public void TurnsTheOldExchangePaymentMethodsIntoTokenCheckoutOptions()
    {
        var store = new StoreData { Id = "store" };
        var settings = new JObject { ["integrator"] = "my-store", ["refundEvm"] = "0xabc" };
        store.SetPaymentMethodConfig(new PaymentMethodId("LIFI_BASE"), settings);
        store.SetPaymentMethodConfig(new PaymentMethodId("LIFI_ETHEREUM"), settings);
        store.SetPaymentMethodConfig(new PaymentMethodId("NEAR_USDC"), settings);
        store.SetPaymentMethodConfig(new PaymentMethodId("NEAR_USDT"), settings);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(new PaymentMethodId("NEAR_USDT"), true);
        store.SetStoreBlob(blob);

        Assert.Equal(new[] { "USDC" }, LegacyIdMigration.MoveExchangeOptions(store));

        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("NEAR_USDC")));
        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("NEAR_USDT")));
        Assert.False(store.GetStoreBlob().GetExcludedPaymentMethods().Match(new PaymentMethodId("NEAR_USDT")));
        Assert.Equal(new[] { "USDC" }, store.GetPaymentMethodConfig(new PaymentMethodId("LIFI_BASE"))!["exchangeOptions"]!.ToObject<string[]>());
        Assert.Equal(new[] { "USDC" }, store.GetPaymentMethodConfig(new PaymentMethodId("LIFI_ETHEREUM"))!["exchangeOptions"]!.ToObject<string[]>());
        Assert.Null(LegacyIdMigration.MoveExchangeOptions(store));
    }

    [Fact]
    public void RemovesTheThorchainPaymentMethods()
    {
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(new PaymentMethodId("LIFI_BASE"), new JObject { ["integrator"] = "my-store" });
        store.SetPaymentMethodConfig(new PaymentMethodId("THOR_LITECOIN"), new JObject { ["integrator"] = "my-store" });
        store.SetPaymentMethodConfig(new PaymentMethodId("THOR_ETH"), new JObject { ["integrator"] = "my-store" });
        var blob = store.GetStoreBlob();
        blob.SetExcluded(new PaymentMethodId("THOR_ETH"), true);
        store.SetStoreBlob(blob);

        Assert.True(LegacyIdMigration.RemoveThorchain(store));

        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("THOR_LITECOIN")));
        Assert.Null(store.GetPaymentMethodConfig(new PaymentMethodId("THOR_ETH")));
        Assert.False(store.GetStoreBlob().GetExcludedPaymentMethods().Match(new PaymentMethodId("THOR_ETH")));
        Assert.NotNull(store.GetPaymentMethodConfig(new PaymentMethodId("LIFI_BASE")));
        Assert.False(LegacyIdMigration.RemoveThorchain(store));
    }
}
