using System.Net;
using System.Text;
using BTCPayServer.Plugins.Stablecoins.Pegasus;
using BTCPayServer.Plugins.Stablecoins.Swap;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stablecoins.Tests;

public class ExactJsonTests
{
    // 18 significant digits: more than a double holds. XMR amounts have 12 decimals.
    const string ManyDigits = "1234.567890123456789";

    [Fact]
    public void KeepsEveryDigitOfANumericAmount()
    {
        var json = ExactJson.Parse($$"""{"amount":{{ManyDigits}}}""");
        Assert.Equal(1234.567890123456789m, PegasusClient.Decimal(json["amount"]));
        // What Newtonsoft's default parsing gives: the amount has already gone through a double.
        Assert.NotEqual(1234.567890123456789m, PegasusClient.Decimal(JToken.Parse($$"""{"amount":{{ManyDigits}}}""")["amount"]));
    }

    [Fact]
    public void ParsesEverythingElseAsBefore()
    {
        var json = ExactJson.Parse("""{"i":5,"s":"0.10","b":true,"n":null,"a":[1,"x"],"o":{"k":"v"}} // trailing comment""");
        Assert.Equal(JTokenType.Integer, json["i"]!.Type);
        Assert.Equal("0.10", json["s"]!.ToString());
        Assert.True(json["b"]!.Value<bool>());
        Assert.Equal(JTokenType.Null, json["n"]!.Type);
        Assert.Equal(2, ((JArray)json["a"]!).Count);
        Assert.Equal("v", json["o"]!["k"]!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"a":1} trailing""")]
    [InlineData("""{"a":""")]
    public void IsNullForAnythingThatIsNotOneJsonValue(string text)
    {
        Assert.Null(ExactJson.TryParse(text));
        Assert.ThrowsAny<JsonException>(() => ExactJson.Parse(text));
    }

    class FixedResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task APegasusEstimateSentAsNumbersKeepsEveryDigit()
    {
        var client = new PegasusClient(new HttpClient(new FixedResponse(
            """{"amount":1.234567890123,"receive":0.00987654321,"exchangeRate":0.00800000000000000001,"minAmount":0.1,"maxAmount":500}""")));
        var estimate = await client.Estimate(new Uri("https://pegasus.example"), new PegasusClient.Credentials("pub", "secret"),
            "XMR", "XMR", "BTC", "BTC", 0.00987654321m, "receive", CancellationToken.None);

        Assert.Equal(1.234567890123m, estimate.Amount);
        Assert.Equal(0.00987654321m, estimate.Receive);
        Assert.Equal(0.00800000000000000001m, estimate.ExchangeRate);
        Assert.Equal(0.1m, estimate.MinAmount);
        Assert.Equal(500m, estimate.MaxAmount);
    }
}
