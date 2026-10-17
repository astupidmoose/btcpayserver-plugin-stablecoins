#nullable enable
using System;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>Format checks for the refund addresses: a wrong one loses every NEAR Intents refund.</summary>
public static class AddressChecks
{
    const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>0x and 40 hex characters; a mixed-case address must also match its EIP-55 checksum.</summary>
    public static bool IsEvmAddress(string value)
    {
        if (value.Length != 42 || !value.StartsWith("0x", StringComparison.Ordinal) || !value.Skip(2).All(Uri.IsHexDigit))
            return false;
        var hex = value[2..];
        if (hex == hex.ToLowerInvariant() || hex == hex.ToUpperInvariant())
            return true;
        return hex == Eip55(hex);
    }

    /// <summary>The checksummed spelling of 40 hex characters (without 0x).</summary>
    public static string Eip55(string hex)
    {
        var lower = hex.ToLowerInvariant();
        var digest = new KeccakDigest(256);
        var input = Encoding.ASCII.GetBytes(lower);
        digest.BlockUpdate(input, 0, input.Length);
        var hash = new byte[32];
        digest.DoFinal(hash, 0);
        var result = new StringBuilder(40);
        for (var i = 0; i < 40; i++)
        {
            var nibble = (hash[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF;
            result.Append(char.IsLetter(lower[i]) && nibble >= 8 ? char.ToUpperInvariant(lower[i]) : lower[i]);
        }
        return result.ToString();
    }

    public static bool IsSolanaAddress(string value) => IsBase58(value, 32, 44);

    public static bool IsTronAddress(string value) => value.StartsWith('T') && IsBase58(value, 34, 34);

    static bool IsBase58(string value, int min, int max) =>
        value.Length >= min && value.Length <= max && value.All(c => Base58.Contains(c));
}
