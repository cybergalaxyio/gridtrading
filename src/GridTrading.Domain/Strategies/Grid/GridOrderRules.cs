namespace GridTrading.Domain.Strategies.Grid;

public static class GridOrderRules
{
    public static bool CanMoveEntry(string status, decimal filledQuantity) =>
        status == "NEW" && filledQuantity == 0m;

    public static IReadOnlySet<int> OccupiedLevels(
        OrderSide side,
        IEnumerable<(OrderSide Side, int LevelIndex, bool IsClosed)> lots) =>
        lots.Where(x => x.Side == side && !x.IsClosed)
            .Select(x => x.LevelIndex)
            .ToHashSet();
}
