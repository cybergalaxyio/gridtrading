using GridTrading.Domain;

namespace GridTrading.Domain.Tests;

public sealed class GridInstrumentRulesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingOrNonpositiveMetadataKeepsLegacyFallbacksAndRequestedSymbol(int value)
    {
        var config = new GridConfiguration
        {
            Symbol = "BTC", TickSize = value, QuantityStep = value, MinOrderQuantity = value,
            MinOrderNotional = value, MaxActiveOrders = value
        };

        Assert.Equal(new InstrumentRules("BTC", .001m, .1m, .1m, 5m, 500), GridInstrumentRules.FromConfiguration(config));
    }

    [Fact]
    public void PositiveMetadataIsPreservedIndependentlyOfMissingFields()
    {
        var config = new GridConfiguration
        {
            Symbol = "BTC", TickSize = .00001m, QuantityStep = .01m,
            MinOrderQuantity = 0m, MinOrderNotional = 12m, MaxActiveOrders = 100
        };

        Assert.Equal(new InstrumentRules("BTC", .00001m, .01m, .1m, 12m, 100), GridInstrumentRules.FromConfiguration(config));
        Assert.Equal(0m, config.MinOrderQuantity);
    }
}
