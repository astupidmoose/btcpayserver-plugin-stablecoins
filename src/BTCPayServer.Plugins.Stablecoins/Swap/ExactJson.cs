#nullable enable
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Swap;

/// <summary>
/// Parses provider responses with every non-integer number read as <see cref="decimal"/>. Newtonsoft's default
/// reads them as <see cref="double"/>, which can drop digits of an amount (XMR has 12 decimals) before it ever
/// reaches a decimal.
/// </summary>
public static class ExactJson
{
    public static JToken Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { FloatParseHandling = FloatParseHandling.Decimal };
        var token = JToken.ReadFrom(reader);
        // Same as JToken.Parse: nothing but whitespace or comments may follow the value.
        while (reader.Read())
            if (reader.TokenType != JsonToken.Comment)
                throw new JsonReaderException("Additional text found in JSON after the value.");
        return token;
    }

    /// <summary>Null when the text is not JSON (an error page, an empty body).</summary>
    public static JToken? TryParse(string text)
    {
        try
        {
            return Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
