namespace GridTrading.Domain;

public static class GridInstrumentRules
{
    // Preserve the historical fallbacks when old frozen configurations lack metadata.
    public static readonly InstrumentRules LegacyDefaults = new("SOLUSDT", .001m, .1m, .1m, 5m, 500);

    public static InstrumentRules FromConfiguration(GridConfiguration config) => new(config.Symbol,
        config.TickSize > 0m ? config.TickSize : LegacyDefaults.TickSize,
        config.QuantityStep > 0m ? config.QuantityStep : LegacyDefaults.QuantityStep,
        config.MinOrderQuantity > 0m ? config.MinOrderQuantity : LegacyDefaults.MinOrderQuantity,
        config.MinOrderNotional > 0m ? config.MinOrderNotional : LegacyDefaults.MinOrderNotional,
        config.MaxActiveOrders > 0 ? config.MaxActiveOrders : LegacyDefaults.MaxActiveOrders);
}
