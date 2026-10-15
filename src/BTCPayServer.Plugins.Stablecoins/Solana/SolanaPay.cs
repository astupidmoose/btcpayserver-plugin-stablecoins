#nullable enable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Stablecoins.Solana;

/// <summary>
/// Helpers for the Solana Pay transfer request spec:
/// https://docs.solanapay.com/spec#transfer-request
/// </summary>
public static class SolanaPay
{
    static readonly Base58Encoder Base58 = Encoders.Base58 as Base58Encoder ?? new Base58Encoder();

    /// <summary>
    /// A fresh reference key for one payment prompt. The spec only requires a base58
    /// encoded 32 byte value; the wallet adds it as a read-only, non-signer account so
    /// getSignaturesForAddress(reference) finds the payment.
    /// </summary>
    public static string NewReference()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base58.EncodeData(bytes.ToArray());
    }

    /// <summary>
    /// True when <paramref name="value"/> is a base58 string decoding to exactly 32 bytes,
    /// the shape of every Solana public key.
    /// </summary>
    public static bool IsPublicKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 32 or > 44)
            return false;
        try
        {
            return Base58.DecodeData(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string BuildTransferUrl(string recipient, decimal amount, string splToken, string reference,
        string? label = null, string? message = null, string? memo = null)
    {
        var sb = new StringBuilder("solana:");
        sb.Append(recipient);
        sb.Append("?amount=").Append(FormatAmount(amount));
        sb.Append("&spl-token=").Append(splToken);
        sb.Append("&reference=").Append(reference);
        if (!string.IsNullOrEmpty(label))
            sb.Append("&label=").Append(Uri.EscapeDataString(label));
        if (!string.IsNullOrEmpty(message))
            sb.Append("&message=").Append(Uri.EscapeDataString(message));
        if (!string.IsNullOrEmpty(memo))
            sb.Append("&memo=").Append(Uri.EscapeDataString(memo));
        return sb.ToString();
    }

    /// <summary>
    /// The spec wants a plain decimal ("1", "0.01"), no exponent, no trailing zeros, at most
    /// the token's decimals (6 for USDC).
    /// </summary>
    public static string FormatAmount(decimal amount)
    {
        amount = Math.Round(amount, StablecoinsPlugin.UsdcDivisibility, MidpointRounding.ToPositiveInfinity);
        return amount.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
