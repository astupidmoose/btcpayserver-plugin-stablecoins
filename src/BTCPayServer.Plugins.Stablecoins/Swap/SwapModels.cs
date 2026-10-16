#nullable enable

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
    /// <summary>Optional THORName that receives the THORChain affiliate fee.</summary>
    public string? ThorAffiliate { get; set; }
    /// <summary>THORChain affiliate fee in basis points (100 = 1%).</summary>
    public int ThorAffiliateBps { get; set; }
    /// <summary>Maximum price movement THORChain accepts before refunding the swap, in basis points.</summary>
    public int ThorToleranceBps { get; set; } = SwapDefaults.ThorToleranceBps;
}

public static class SwapDefaults
{
    public const string Integrator = "btcpay-stablecoins";
    public const decimal BufferPercent = 0m;
    public const int ThorToleranceBps = 300;
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
