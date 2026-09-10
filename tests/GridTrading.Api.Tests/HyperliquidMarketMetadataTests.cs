using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Services;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidMarketMetadataTests
{
    [Fact]
    public async Task MarketFieldsStayAlignedWithUniverseIncludingDelistedAssets()
    {
        var result = await ReadMetadata("""
            [{"universe":[
              {"name":"OLD","szDecimals":2,"isDelisted":true},
              {"name":"SOL","szDecimals":2},
              {"name":"BTC","szDecimals":5}
            ]},[
              {"markPx":"1","prevDayPx":"2","funding":"0"},
              {"markPx":"100.565","midPx":"100.6","prevDayPx":"103.085","funding":"-0.0000125"},
              {"markPx":"60000","prevDayPx":"59000","funding":"0.000001"}
            ]]
            """);
        var universe = result.GetProperty("universe");
        Assert.True(universe[0].GetProperty("isDelisted").GetBoolean());
        var sol = universe[1];
        Assert.Equal("SOL", sol.GetProperty("symbol").GetString());
        Assert.Equal(100.565m, sol.GetProperty("markPrice").GetDecimal());
        Assert.Equal(103.085m, sol.GetProperty("previousDayPrice").GetDecimal());
        Assert.Equal(-.0000125m, sol.GetProperty("fundingRate").GetDecimal());
        Assert.Equal(60000m, universe[2].GetProperty("markPrice").GetDecimal());
    }

    [Fact]
    public async Task MissingOrInvalidMarketValuesRemainUnavailableInsteadOfZero()
    {
        var result = await ReadMetadata("""
            [{"universe":[{"name":"SOL","szDecimals":2},{"name":"BTC","szDecimals":5}]},
             [{"markPx":null,"prevDayPx":"invalid","funding":"0"}]]
            """);
        var universe = result.GetProperty("universe");
        Assert.Equal(JsonValueKind.Null, universe[0].GetProperty("markPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, universe[0].GetProperty("previousDayPrice").ValueKind);
        Assert.Equal(0m, universe[0].GetProperty("fundingRate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, universe[1].GetProperty("markPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, universe[1].GetProperty("fundingRate").ValueKind);
    }

    private static async Task<JsonElement> ReadMetadata(string response)
    {
        using var http = new HttpClient(new MetadataHandler(response));
        var client = new HyperliquidInfoClient(http, new ConfigurationBuilder().Build());
        return JsonSerializer.SerializeToElement(await client.GetPerpetualMetadata(TestContext.Current.CancellationToken));
    }

    private sealed class MetadataHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}
