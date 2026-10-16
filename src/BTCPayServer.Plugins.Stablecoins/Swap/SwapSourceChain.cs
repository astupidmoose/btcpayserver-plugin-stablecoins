#nullable enable
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// A chain customers can pay from. Each one is its own BTCPay payment method, so
/// WooCommerce (separate gateways) and the checkout list them as "Ethereum", "Base"...
/// Every one of them settles the invoice in BTC to the store's own on-chain wallet.
/// </summary>
public record SwapSourceChain(string Code, string Name, long LifiChainId, bool IsEvm)
{
    /// <summary>For example LIFI-ETHEREUM. WooCommerce shows it as LIFI_ETHEREUM until you rename the gateway.</summary>
    public PaymentMethodId PaymentMethodId { get; } = new($"LIFI-{Code}");

    public string Image => $"Resources/chains/{Code.ToLowerInvariant()}.svg";

    public static readonly SwapSourceChain[] All =
    [
        new("ETHEREUM", "Ethereum", 1, true),
        new("BASE", "Base", 8453, true),
        new("ARBITRUM", "Arbitrum", 42161, true),
        new("OPTIMISM", "Optimism", 10, true),
        new("POLYGON", "Polygon", 137, true),
        new("BSC", "BNB Chain", 56, true),
        new("AVALANCHE", "Avalanche", 43114, true),
        new("SOLANA", "Solana", 1151111081099710, false),
    ];

    static readonly Dictionary<PaymentMethodId, SwapSourceChain> ByPmi = All.ToDictionary(c => c.PaymentMethodId);

    public static SwapSourceChain? Find(PaymentMethodId? pmi) =>
        pmi is not null && ByPmi.TryGetValue(pmi, out var chain) ? chain : null;

    public static SwapSourceChain? Find(string? code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, System.StringComparison.OrdinalIgnoreCase));
}
