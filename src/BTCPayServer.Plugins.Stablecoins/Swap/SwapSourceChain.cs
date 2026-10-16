#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

public enum SwapProvider
{
    /// <summary>Tokens on EVM chains, Solana and Tron, through the LI.FI widget and the customer's connected wallet.</summary>
    Lifi,
    /// <summary>Native coins through THORChain: the customer sends the coin to a THORChain vault with a memo.</summary>
    Thorchain
}

/// <summary>
/// A chain customers can pay from. Each one is its own BTCPay payment method, so
/// WooCommerce (separate gateways) and the checkout list them as "Ethereum", "Litecoin"...
/// Every one of them settles the invoice in BTC to the store's own on-chain wallet.
/// </summary>
public record SwapSourceChain
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required SwapProvider Provider { get; init; }
    /// <summary>LI.FI chain id (LI.FI only).</summary>
    public long LifiChainId { get; init; }
    /// <summary>THORChain asset, for example LTC.LTC (THORChain only).</summary>
    public string? ThorAsset { get; init; }
    /// <summary>The coin's ticker, shown with the amount to send (THORChain only).</summary>
    public string? Ticker { get; init; }
    /// <summary>Decimals the coin itself can send (THORChain only; THORChain amounts are always 1e8 units).</summary>
    public int Decimals { get; init; } = 8;
    /// <summary>URI scheme for the deposit QR code, for example litecoin. Null when wallets have no common one.</summary>
    public string? UriScheme { get; init; }
    /// <summary>Where the customer's wallet puts the THORChain memo, shown in the checkout's instructions.</summary>
    public string? MemoField { get; init; }

    public string? ThorChain => ThorAsset?.Split('.')[0];

    /// <summary>
    /// LIFI_ETHEREUM or THOR_LITECOIN. Underscores, not dashes: the WooCommerce BTCPay plugin turns every '-' into '_'
    /// in the payment method it sends back when creating the invoice, and BTCPay only maps that back for built-in coins.
    /// </summary>
    public PaymentMethodId PaymentMethodId => new($"{(Provider == SwapProvider.Lifi ? "LIFI" : "THOR")}_{Code}");

    public string Image => $"Resources/chains/{Code.ToLowerInvariant()}.svg";

    static SwapSourceChain Lifi(string code, string name, long chainId) =>
        new() { Code = code, Name = name, Provider = SwapProvider.Lifi, LifiChainId = chainId };

    static SwapSourceChain Thor(string code, string name, string asset, string ticker, int decimals, string? uriScheme, string memoField) =>
        new() { Code = code, Name = name, Provider = SwapProvider.Thorchain, ThorAsset = asset, Ticker = ticker, Decimals = decimals, UriScheme = uriScheme, MemoField = memoField };

    public const long SolanaLifiChainId = 1151111081099710;
    public const long TronLifiChainId = 728126428;

    public static readonly SwapSourceChain[] All =
    [
        Lifi("ETHEREUM", "Ethereum", 1),
        Lifi("BASE", "Base", 8453),
        Lifi("ARBITRUM", "Arbitrum", 42161),
        Lifi("OPTIMISM", "Optimism", 10),
        Lifi("POLYGON", "Polygon", 137),
        Lifi("BSC", "BNB Chain", 56),
        Lifi("AVALANCHE", "Avalanche", 43114),
        Lifi("SOLANA", "Solana", SolanaLifiChainId),
        Lifi("TRON", "Tron", TronLifiChainId),
        // THORChain's other non-EVM coins. BCH is left out on purpose. Solana and Tron go through LI.FI,
        // which also takes their tokens. RUNE (needs a THORChain wallet transaction) and Monero
        // (cannot carry a memo) need THORChain's memoless deposits, which are not built yet.
        Thor("LITECOIN", "Litecoin", "LTC.LTC", "LTC", 8, "litecoin", "OP_RETURN"),
        Thor("DOGECOIN", "Dogecoin", "DOGE.DOGE", "DOGE", 8, "dogecoin", "OP_RETURN"),
        Thor("ZCASH", "Zcash", "ZEC.ZEC", "ZEC", 8, "zcash", "OP_RETURN, from a transparent (t1) address"),
        Thor("COSMOS", "Cosmos Hub", "GAIA.ATOM", "ATOM", 6, null, "memo"),
        Thor("XRP", "XRP Ledger", "XRP.XRP", "XRP", 6, null, "memo"),
    ];

    static readonly Dictionary<PaymentMethodId, SwapSourceChain> ByPmi = All.ToDictionary(c => c.PaymentMethodId);

    public static SwapSourceChain? Find(PaymentMethodId? pmi) =>
        pmi is not null && ByPmi.TryGetValue(pmi, out var chain) ? chain : null;

    public static SwapSourceChain? Find(string? code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
