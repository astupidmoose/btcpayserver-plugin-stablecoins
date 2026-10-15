#nullable enable
using System;

namespace BTCPayServer.Plugins.Stablecoins.Solana;

/// <summary>
/// Server-wide Solana settings. Chosen from BTCPay's own network type
/// (mainnet uses mainnet-beta, every other network uses devnet) and overridable
/// with the BTCPAY_SOLANA_RPC_URI and BTCPAY_SOLANA_POLL_SECONDS environment variables.
/// </summary>
public class SolanaChainConfiguration
{
    public const string MainnetUsdcMint = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    public const string DevnetUsdcMint = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";

    public required string Cluster { get; init; }
    public required Uri RpcUri { get; init; }
    public required string UsdcMint { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsMainnet => Cluster == "mainnet-beta";

    /// <summary>Block explorer link with {0} for the signature, as BTCPay's link providers expect.</summary>
    public string TransactionLinkFormat =>
        IsMainnet ? "https://explorer.solana.com/tx/{0}" : $"https://explorer.solana.com/tx/{{0}}?cluster={Cluster}";

    public static SolanaChainConfiguration Create(bool mainnet, Uri? rpcOverride, int? pollSeconds)
    {
        return new SolanaChainConfiguration
        {
            Cluster = mainnet ? "mainnet-beta" : "devnet",
            RpcUri = rpcOverride ?? new Uri(mainnet ? "https://api.mainnet-beta.solana.com" : "https://api.devnet.solana.com"),
            UsdcMint = mainnet ? MainnetUsdcMint : DevnetUsdcMint,
            PollInterval = TimeSpan.FromSeconds(pollSeconds is > 0 ? pollSeconds.Value : 10)
        };
    }
}
