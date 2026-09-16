using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Domain;

namespace GridTrading.Api.Strategies.Grid.Configuration;

// JSON conversion only: no validation, metadata refresh, or persistence side effects.
public static class GridConfigurationCodec
{
    public static StrategyRequest ReadStrategy(string json) =>
        JsonSerializer.Deserialize<StrategyRequest>(json, JsonSupport.Options)!;

    public static string WriteStrategy(StrategyRequest settings) =>
        JsonSerializer.Serialize(settings, JsonSupport.Options);

    public static GridConfiguration ReadFrozen(string json) =>
        JsonSerializer.Deserialize<GridConfiguration>(json, JsonSupport.Options)!;

    public static string WriteFrozen(GridConfiguration configuration) =>
        JsonSerializer.Serialize(configuration, JsonSupport.Options);
}
