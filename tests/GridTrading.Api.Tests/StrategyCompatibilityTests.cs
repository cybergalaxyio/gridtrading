using System.Text.Json;
using System.Text.Json.Nodes;
using GridTrading.Api.Contracts;
using GridTrading.Api.Infrastructure;
using GridTrading.Domain;

namespace GridTrading.Api.Tests;

public sealed class StrategyCompatibilityTests
{
    [Fact]
    public void LegacyPositionModeConfigurationDefaultsToTwoWayGrid()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(StrategyRequest.Default, JsonSupport.Options))!.AsObject();
        json.Remove("gridMode");
        json["positionMode"] = "ONE_WAY";

        var strategy = JsonSerializer.Deserialize<StrategyRequest>(json.ToJsonString(), JsonSupport.Options)!;

        Assert.Equal(GridMode.TwoWay, strategy.GridMode);
    }
}
