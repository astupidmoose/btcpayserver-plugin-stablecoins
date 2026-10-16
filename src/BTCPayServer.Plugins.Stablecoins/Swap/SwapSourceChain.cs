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
    /// <summary>
    /// Stablecoins through NEAR Intents' 1Click API: the customer picks a network and sends an exact amount to a
    /// one-time deposit address, from any wallet or exchange. Not a payment method of its own (so it is never
    /// listed separately): an option inside the token checkouts, paid into that payment method's BTC address.
    /// </summary>
    Intents
}

public enum AddressFamily { Evm, Solana, Tron }

/// <summary>A network a stablecoin can be sent on, by its NEAR Intents blockchain code (eth, pol, sol...).</summary>
public record IntentsNetwork(string Code, string Name, AddressFamily Family, string Image);

/// <summary>
/// A chain customers can pay from. Each one is its own BTCPay payment method, so
/// store integrations and the checkout list them as "Ethereum", "Base"...
/// Every one of them settles the invoice in BTC to the store's own on-chain wallet.
/// </summary>
public record SwapSourceChain
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required SwapProvider Provider { get; init; }
    /// <summary>LI.FI chain id (LI.FI only).</summary>
    public long LifiChainId { get; init; }
    /// <summary>The same chain's NEAR Intents code (base, sol...), for "Send USDC from an exchange" on this checkout (LI.FI only).</summary>
    public string? IntentsNetworkCode { get; init; }
    /// <summary>The coin's ticker, shown with the amount to send (exchange options only).</summary>
    public string? Ticker { get; init; }
    /// <summary>Decimals the coin can send (exchange options only).</summary>
    public int Decimals { get; init; } = 8;
    /// <summary>The coin's icon.</summary>
    public string? CoinImage { get; init; }

    /// <summary>Networks the token can be sent on (NEAR Intents only).</summary>
    public IReadOnlyList<IntentsNetwork> Networks { get; init; } = [];

    /// <summary>
    /// LIFI_ETHEREUM. Underscores, not dashes: some store integrations turn every '-' into '_'
    /// in the payment method it sends back when creating the invoice, and BTCPay only maps that back for built-in coins.
    /// </summary>
    public PaymentMethodId PaymentMethodId => new($"{Provider switch { SwapProvider.Lifi => "LIFI", _ => "NEAR" }}_{Code}");

    public string Image => CoinImage ?? $"Resources/chains/{Code.ToLowerInvariant()}.svg";

    static SwapSourceChain Lifi(string code, string name, long chainId, string intentsNetwork) =>
        new() { Code = code, Name = name, Provider = SwapProvider.Lifi, LifiChainId = chainId, IntentsNetworkCode = intentsNetwork };

    static SwapSourceChain Intents(string symbol, params IntentsNetwork[] networks) =>
        new() { Code = symbol, Name = $"{symbol} from an exchange", Provider = SwapProvider.Intents, Ticker = symbol, Networks = networks, Decimals = 6 };

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
        Lifi("ETHEREUM", "Ethereum", 1, "eth"),
        Lifi("BASE", "Base", 8453, "base"),
        Lifi("ARBITRUM", "Arbitrum", 42161, "arb"),
        Lifi("OPTIMISM", "Optimism", 10, "op"),
        Lifi("POLYGON", "Polygon", 137, "pol"),
        Lifi("BSC", "BNB Chain", 56, "bsc"),
        Lifi("AVALANCHE", "Avalanche", 43114, "avax"),
        Lifi("SOLANA", "Solana", SolanaLifiChainId, "sol"),
        Lifi("TRON", "Tron", TronLifiChainId, "tron"),
        // Stablecoins from any wallet or exchange: buttons on the token checkouts, on that checkout's own chain.
        Intents("USDC", Eth, Base, Arb, Pol, Sol, Bsc, Avax, Op) with { CoinImage = "Resources/chains/usdc.svg" },
        Intents("USDT", Eth, Tron, Bsc, Sol, Pol, Arb, Avax) with { CoinImage = "Resources/chains/usdt.svg" },
    ];

    /// <summary>The chains that are BTCPay payment methods (everything but the exchange options).</summary>
    public static readonly SwapSourceChain[] PaymentMethods = All.Where(c => c.Provider != SwapProvider.Intents).ToArray();
    /// <summary>"USDC from an exchange", "USDT from an exchange": offered inside the token (LI.FI) checkouts.</summary>
    public static readonly SwapSourceChain[] ExchangeOptions = All.Where(c => c.Provider == SwapProvider.Intents).ToArray();

    static readonly Dictionary<PaymentMethodId, SwapSourceChain> ByPmi = PaymentMethods.ToDictionary(c => c.PaymentMethodId);

    public static SwapSourceChain? Find(PaymentMethodId? pmi) =>
        pmi is not null && ByPmi.TryGetValue(pmi, out var chain) ? chain : null;

    public static SwapSourceChain? Find(string? code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
