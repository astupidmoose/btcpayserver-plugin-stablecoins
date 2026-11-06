#nullable enable
using System;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>Format checks for the refund addresses: a wrong one loses every refund.</summary>
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

    /// <summary>
    /// A mainnet Monero address: standard or subaddress (95 characters, starting with 4 or 8) or integrated (106, starting with 4),
    /// whose Keccak-256 checksum matches. A stagenet or testnet address would lose the refund.
    /// </summary>
    public static bool IsMoneroAddress(string value)
    {
        if (!(value.Length == 95 && value[0] is '4' or '8' || value.Length == 106 && value[0] == '4') || !value.All(c => Base58.Contains(c)))
            return false;
        if (MoneroBase58Decode(value) is not { Length: > 4 } data)
            return false;
        // Network bytes: 18 standard, 19 integrated, 42 subaddress (mainnet).
        if (data[0] is not (18 or 19 or 42))
            return false;
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(data, 0, data.Length - 4);
        var hash = new byte[32];
        digest.DoFinal(hash, 0);
        return hash.AsSpan(0, 4).SequenceEqual(data.AsSpan(data.Length - 4));
    }

    /// <summary>Monero's base58: 8-byte blocks, each written as 11 characters (a shorter last block as fewer).</summary>
    static byte[]? MoneroBase58Decode(string value)
    {
        int[] encodedSizes = [0, 2, 3, 5, 6, 7, 9, 10, 11];
        var result = new System.Collections.Generic.List<byte>();
        for (var start = 0; start < value.Length; start += 11)
        {
            var block = value.Substring(start, Math.Min(11, value.Length - start));
            var size = Array.IndexOf(encodedSizes, block.Length);
            if (size < 0)
                return null;
            System.Numerics.BigInteger number = 0;
            foreach (var c in block)
                number = number * 58 + Base58.IndexOf(c);
            if (number >= System.Numerics.BigInteger.One << (8 * size))
                return null;
            var bytes = new byte[size];
            for (var i = size - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(number & 0xFF);
                number >>= 8;
            }
            result.AddRange(bytes);
        }
        return result.ToArray();
    }

    static bool IsBase58(string value, int min, int max) =>
        value.Length >= min && value.Length <= max && value.All(c => Base58.Contains(c));
}
