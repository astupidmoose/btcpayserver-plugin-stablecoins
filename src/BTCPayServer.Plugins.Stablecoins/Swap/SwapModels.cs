#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Store-level configuration of one "pay from chain X" method. The same LI.FI settings
/// are written to every enabled chain, so the checkout can read them without another lookup.
/// </summary>
public class SwapToBtcPaymentMethodConfig
{
    /// <summary>LI.FI integrator name from the partner portal. Tags swaps and routes fees.</summary>
    public string? Integrator { get; set; }
    /// <summary>
    /// Optional LI.FI API key, sent to the browser as LI.FI requires. Kept only in the store setting (<see cref="SwapSecrets"/>),
    /// never in the payment method configs that BTCPay's API returns.
    /// </summary>
    public string? ApiKey { get; set; }
    /// <summary>Optional integrator fee as a fraction, for example 0.005 for 0.5%. Paid by the customer.</summary>
    public decimal Fee { get; set; }
    /// <summary>Extra BTC asked from LI.FI over the amount due, in percent, to absorb bridge rounding.</summary>
    public decimal BufferPercent { get; set; } = SwapDefaults.BufferPercent;
    /// <summary>Optional WalletConnect (Reown) project id, which enables mobile wallets on EVM chains.</summary>
    public string? WalletConnectProjectId { get; set; }

    /// <summary>NEAR Intents 1Click API key (a JWT). Stays on the server, in the store setting only, like <see cref="ApiKey"/>.</summary>
    public string? IntentsApiKey { get; set; }
    /// <summary>
    /// Where NEAR Intents returns a stablecoin payment it could not swap (too little sent, sent too late), and any excess.
    /// One per address family; a network is offered only when its family has one.
    /// </summary>
    public string? RefundEvm { get; set; }
    public string? RefundSolana { get; set; }
    public string? RefundTron { get; set; }
    /// <summary>Price movement allowed on stablecoin swaps, in basis points. The amount shown includes it; any unused part is refunded.</summary>
    public int IntentsSlippageBps { get; set; } = SwapDefaults.IntentsSlippageBps;
    /// <summary>The exchange options switched on (USDC, USDT), offered on the token checkouts. Not payment methods.</summary>
    public string[] ExchangeOptions { get; set; } = [];
    /// <summary>The store turned the whole feature off: no chain is offered, but the choices below are kept.</summary>
    public bool Disabled { get; set; }
    /// <summary>The token chains (codes) the store picked, remembered while the feature is off.</summary>
    public string[] Chains { get; set; } = [];

    public IEnumerable<SwapSourceChain> EnabledExchangeOptions() =>
        SwapSourceChain.ExchangeOptions.Where(c => ExchangeOptions.Contains(c.Code, StringComparer.OrdinalIgnoreCase) &&
                                                   Intents.IntentsDepositService.Networks(c, this).Any());

    /// <summary>
    /// The exchange options a token checkout offers: only the coins NEAR Intents takes on that same chain, with a refund
    /// address there. The customer sends on the chain of the checkout they are on, never another one.
    /// </summary>
    public IEnumerable<SwapSourceChain> ExchangeOptionsFor(SwapSourceChain chain) =>
        EnabledExchangeOptions().Where(o => Intents.IntentsDepositService.Networks(o, this).Any(n => n.Code == chain.IntentsNetworkCode));

    public string? RefundAddress(AddressFamily family) => family switch
    {
        AddressFamily.Evm => RefundEvm,
        AddressFamily.Solana => RefundSolana,
        _ => RefundTron
    };
}

public static class SwapDefaults
{
    public const string Integrator = "btcpay-stablecoins";
    public const decimal BufferPercent = 0m;
    public const int IntentsSlippageBps = 50;
}

/// <summary>
/// Stored on each invoice's prompt: where in the store's BTC wallet the address came from.
/// </summary>
public class SwapToBtcPromptDetails
{
    public string? AccountDerivation { get; set; }
    public string? KeyPath { get; set; }
    public int KeyIndex { get; set; }
    public long FromChainId { get; set; }
    /// <summary>Every NEAR Intents deposit quoted for this checkout, followed until settled (also after a restart).</summary>
    public List<Intents.PersistedQuote>? IntentsQuotes { get; set; }
}

/// <summary>
/// Stored on each payment: the BTC output that LI.FI's bridge delivered.
/// </summary>
public class SwapToBtcPaymentData
{
    /// <summary>txid-index, the same format BTCPay uses for on-chain payments.</summary>
    public string Outpoint { get; set; } = "";
    public long ConfirmationCount { get; set; }
    public bool RBF { get; set; }
    public string? KeyPath { get; set; }
}
