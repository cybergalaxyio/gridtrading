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

    [Fact]
    public void MissingPartialFillTimeoutDefaultsToTenMinutes()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(StrategyRequest.Default, JsonSupport.Options))!.AsObject();
        json.Remove("partialFillCancelAfterMinutes");

        var strategy = JsonSerializer.Deserialize<StrategyRequest>(json.ToJsonString(), JsonSupport.Options)!;

        Assert.Equal(10, strategy.PartialFillCancelAfterMinutes);
    }

    [Fact]
    public void MissingFaultExposureThresholdDefaultsToTenUsd()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(StrategyRequest.Default, JsonSupport.Options))!.AsObject();
        json.Remove("faultExposureThresholdUsdt");

        var strategy = JsonSerializer.Deserialize<StrategyRequest>(json.ToJsonString(), JsonSupport.Options)!;
        var configuration = strategy.ToConfiguration();

        Assert.Equal(10m, strategy.FaultExposureThresholdUsdt);
        Assert.Equal(10m, configuration.FaultExposureThresholdUsdt);
    }

    [Fact]
    public void NewStrategyIdsUseTheShortFormat()
    {
        var first = Ids.NewStrategy();
        var second = Ids.NewStrategy();

        Assert.StartsWith("strategy_", first);
        Assert.Equal(25, first.Length);
        Assert.NotEqual(first, second);
    }
}
