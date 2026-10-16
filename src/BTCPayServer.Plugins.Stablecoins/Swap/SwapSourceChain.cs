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
    /// <summary>Native coins through THORChain: the customer sends an exact amount to a THORChain vault, no memo (memoless deposit).</summary>
    Thorchain,
    /// <summary>
    /// Stablecoins through NEAR Intents' 1Click API: the customer picks a network and sends an exact amount to a
    /// one-time deposit address, from any wallet or exchange.
    /// </summary>
    Intents
}

public enum AddressFamily { Evm, Solana, Tron }

/// <summary>A network a stablecoin can be sent on, by its NEAR Intents blockchain code (eth, pol, sol...).</summary>
public record IntentsNetwork(string Code, string Name, AddressFamily Family, string Image);

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
    /// <summary>EVM chain id for EIP-681 payment links (THORChain coins on EVM chains only).</summary>
    public long? EvmChainId { get; init; }
    /// <summary>The coin's icon; BTCPay's own where it has one, so the checkout looks like its own coin invoices.</summary>
    public string? CoinImage { get; init; }

    /// <summary>Networks the token can be sent on (NEAR Intents only).</summary>
    public IReadOnlyList<IntentsNetwork> Networks { get; init; } = [];

    public string? ThorChain => ThorAsset?.Split('.')[0];

    /// <summary>
    /// LIFI_ETHEREUM or THOR_LITECOIN. Underscores, not dashes: the WooCommerce BTCPay plugin turns every '-' into '_'
    /// in the payment method it sends back when creating the invoice, and BTCPay only maps that back for built-in coins.
    /// </summary>
    public PaymentMethodId PaymentMethodId => new($"{Provider switch { SwapProvider.Lifi => "LIFI", SwapProvider.Thorchain => "THOR", _ => "NEAR" }}_{Code}");

    public string Image => CoinImage ?? $"Resources/chains/{Code.ToLowerInvariant()}.svg";

    /// <summary>
    /// What the QR code and "Pay in wallet" open: a payment link wallets understand, or just the address when the
    /// coin has no common one. EVM coins use EIP-681 (amount in wei, chain id so the wallet switches network).
    /// </summary>
    public string? PaymentUri(string address, decimal amount, string formattedAmount)
    {
        if (EvmChainId is { } evm)
        {
            var wei = decimal.Round(amount * 1_000_000_000_000_000_000m, 0, MidpointRounding.ToPositiveInfinity);
            return $"ethereum:{address}@{evm}?value={wei.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return UriScheme is { } scheme ? $"{scheme}:{address}?amount={formattedAmount}" : null;
    }

    static SwapSourceChain Lifi(string code, string name, long chainId) =>
        new() { Code = code, Name = name, Provider = SwapProvider.Lifi, LifiChainId = chainId };

    static SwapSourceChain Thor(string code, string name, string asset, string ticker, int decimals, string? uriScheme, string? image = null, long? evmChainId = null) =>
        new() { Code = code, Name = name, Provider = SwapProvider.Thorchain, ThorAsset = asset, Ticker = ticker, Decimals = decimals, UriScheme = uriScheme, CoinImage = image, EvmChainId = evmChainId };

    static SwapSourceChain Intents(string symbol, params IntentsNetwork[] networks) =>
        new() { Code = symbol, Name = symbol, Provider = SwapProvider.Intents, Ticker = symbol, Networks = networks, Decimals = 6 };

    static readonly IntentsNetwork Eth = new("eth", "Ethereum", AddressFamily.Evm, "Resources/chains/ethereum.svg");
    static readonly IntentsNetwork Base = new("base", "Base", AddressFamily.Evm, "Resources/chains/base.svg");
    static readonly IntentsNetwork Arb = new("arb", "Arbitrum", AddressFamily.Evm, "Resources/chains/arbitrum.svg");
    static readonly IntentsNetwork Op = new("op", "Optimism", AddressFamily.Evm, "Resources/chains/optimism.svg");
    static readonly IntentsNetwork Pol = new("pol", "Polygon", AddressFamily.Evm, "Resources/chains/polygon.svg");
    static readonly IntentsNetwork Bsc = new("bsc", "BNB Smart Chain (BEP20)", AddressFamily.Evm, "Resources/chains/bsc.svg");
    static readonly IntentsNetwork Avax = new("avax", "Avalanche C-Chain", AddressFamily.Evm, "Resources/chains/avalanche.svg");
    static readonly IntentsNetwork Sol = new("sol", "Solana", AddressFamily.Solana, "Resources/chains/solana.svg");
    static readonly IntentsNetwork Tron = new("tron", "Tron (TRC20)", AddressFamily.Tron, "Resources/chains/tron.svg");

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
        // THORChain coins with memoless deposits. BCH is left out on purpose. Monero is not offered by THORChain's memoless API yet.
        Thor("LITECOIN", "Litecoin", "LTC.LTC", "LTC", 8, "litecoin", "imlegacy/litecoin.svg"),
        Thor("DOGECOIN", "Dogecoin", "DOGE.DOGE", "DOGE", 8, "dogecoin", "imlegacy/dogecoin.png"),
        Thor("ZCASH", "Zcash", "ZEC.ZEC", "ZEC", 8, "zcash"),
        Thor("COSMOS", "Cosmos Hub", "GAIA.ATOM", "ATOM", 6, null),
        Thor("XRP", "XRP Ledger", "XRP.XRP", "XRP", 6, null),
        // Each chain's own coin, paid like Litecoin: scan, send the exact amount, no wallet connection.
        // Their tokens stay with the LI.FI options above. THORChain amounts stop at 8 decimals.
        Thor("ETH", "ETH", "ETH.ETH", "ETH", 8, null, "Resources/chains/ethereum.svg", 1),
        Thor("BASEETH", "ETH on Base", "BASE.ETH", "ETH", 8, null, "Resources/chains/base.svg", 8453),
        Thor("BNB", "BNB", "BSC.BNB", "BNB", 8, null, "Resources/chains/bsc.svg", 56),
        Thor("AVAX", "AVAX", "AVAX.AVAX", "AVAX", 8, null, "Resources/chains/avalanche.svg", 43114),
        Thor("SOL", "SOL", "SOL.SOL", "SOL", 8, "solana", "Resources/chains/solana.svg"),
        Thor("TRX", "TRX", "TRON.TRX", "TRX", 6, null, "Resources/chains/tron.svg"),
        // Stablecoins from any wallet or exchange: one payment method per coin, the network is picked at checkout.
        Intents("USDC", Eth, Base, Arb, Pol, Sol, Bsc, Avax, Op) with { CoinImage = "Resources/chains/usdc.svg" },
        Intents("USDT", Eth, Tron, Bsc, Sol, Pol, Arb, Avax) with { CoinImage = "Resources/chains/usdt.svg" },
    ];

    static readonly Dictionary<PaymentMethodId, SwapSourceChain> ByPmi = All.ToDictionary(c => c.PaymentMethodId);

    public static SwapSourceChain? Find(PaymentMethodId? pmi) =>
        pmi is not null && ByPmi.TryGetValue(pmi, out var chain) ? chain : null;

    public static SwapSourceChain? Find(string? code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
