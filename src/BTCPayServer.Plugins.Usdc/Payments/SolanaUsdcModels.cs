#nullable enable

namespace BTCPayServer.Plugins.Usdc.Payments;

/// <summary>
/// Store-level configuration of the USDC (Solana) payment method.
/// </summary>
public class SolanaUsdcPaymentMethodConfig
{
    /// <summary>
    /// The merchant's Solana wallet (the owner, not the USDC token account).
    /// USDC is paid into that wallet's associated USDC token account.
    /// </summary>
    public string? Address { get; set; }
}

/// <summary>
/// Stored on each invoice's payment prompt.
/// </summary>
public class SolanaUsdcPromptDetails
{
    /// <summary>Solana Pay reference key unique to this prompt.</summary>
    public string Reference { get; set; } = "";
    public string Mint { get; set; } = "";
    public string Cluster { get; set; } = "";
}

/// <summary>
/// Stored on each payment.
/// </summary>
public class SolanaUsdcPaymentData
{
    public string Signature { get; set; } = "";
    public ulong Slot { get; set; }
    /// <summary>confirmed or finalized.</summary>
    public string? ConfirmationStatus { get; set; }
    /// <summary>The fee payer of the transaction; the likely refund address.</summary>
    public string? Payer { get; set; }
    public string Reference { get; set; } = "";
}
