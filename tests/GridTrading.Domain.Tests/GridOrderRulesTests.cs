using GridTrading.Domain.Strategies.Grid;

namespace GridTrading.Domain.Tests;

public sealed class GridOrderRulesTests
{
    [Theory]
    [InlineData("NEW", 0, true)]
    [InlineData("NEW", 0.1, false)]
    [InlineData("PARTIALLY_FILLED", 0.1, false)]
    [InlineData("PENDING_EXCHANGE", 0, false)]
    [InlineData("UNKNOWN", 0, false)]
    public void OnlyConfirmedCompletelyUnfilledEntryCanMove(string status, decimal filled, bool expected)
    {
        Assert.Equal(expected, GridOrderRules.CanMoveEntry(status, filled));
    }

    [Fact]
    public void OpenFragmentsOccupyLevelUntilLastLotCloses()
    {
        var fragments = new[]
        {
            (OrderSide.Sell, 7, false),
            (OrderSide.Sell, 7, true),
            (OrderSide.Sell, 8, false),
            (OrderSide.Buy, 7, false)
        };

        var occupied = GridOrderRules.OccupiedLevels(OrderSide.Sell, fragments);

        Assert.Contains(7, occupied);
        Assert.Contains(8, occupied);
        Assert.Equal(2, occupied.Count);
        Assert.Empty(GridOrderRules.OccupiedLevels(OrderSide.Sell,
            fragments.Select(x => (x.Item1, x.Item2, true))));
    }
}
