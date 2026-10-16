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
    /// <summary>Optional LI.FI API key. It is sent to the browser, as LI.FI's widget requires.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Optional integrator fee as a fraction, for example 0.005 for 0.5%. Paid by the customer.</summary>
    public decimal Fee { get; set; }
    /// <summary>Extra BTC asked from LI.FI over the amount due, in percent, to absorb bridge rounding.</summary>
    public decimal BufferPercent { get; set; } = SwapDefaults.BufferPercent;
    /// <summary>Optional WalletConnect (Reown) project id, which enables mobile wallets on EVM chains.</summary>
    public string? WalletConnectProjectId { get; set; }

    /// <summary>THORNode REST endpoint used for THORChain quotes. Defaults to a public THORNode gateway.</summary>
    public string? ThornodeUrl { get; set; }
    /// <summary>THORChain's hosted memoless API; not on the settings page, the default is THORChain's own.</summary>
    public string? MemolessUrl { get; set; }
    /// <summary>Optional THORName that receives the THORChain affiliate fee.</summary>
    public string? ThorAffiliate { get; set; }
    /// <summary>THORChain affiliate fee in basis points (100 = 1%).</summary>
    public int ThorAffiliateBps { get; set; }
    /// <summary>Maximum price movement THORChain accepts before refunding the swap, in basis points.</summary>
    public int ThorToleranceBps { get; set; } = SwapDefaults.ThorToleranceBps;

    /// <summary>NEAR Intents 1Click API key (a JWT). Stays on the server.</summary>
    public string? IntentsApiKey { get; set; }
    /// <summary>1Click endpoint; not on the settings page, the default is NEAR Intents' own.</summary>
    public string? IntentsUrl { get; set; }
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

    public IEnumerable<SwapSourceChain> EnabledExchangeOptions() =>
        SwapSourceChain.ExchangeOptions.Where(c => ExchangeOptions.Contains(c.Code, StringComparer.OrdinalIgnoreCase) &&
                                                   Intents.IntentsDepositService.Networks(c, this).Any());

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
    public const int ThorToleranceBps = 300;
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
    /// <summary>Source asset for THORChain swaps, for example LTC.LTC.</summary>
    public string? ThorAsset { get; set; }
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
    /// <summary>
    /// The customer's inbound transaction, when the BTC carries it: THORChain's outbound memo is OUT:&lt;inbound tx&gt;.
    /// </summary>
    public string? SourceTxId { get; set; }
}
