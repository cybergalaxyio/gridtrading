namespace GridTrading.Api.Data;

public static class OrderCompletion
{
    // Used when maintaining order history, never by the entry eligibility query.
    public static DateTimeOffset? FindFilledAt(decimal orderQuantity,
        IEnumerable<(decimal Quantity, DateTimeOffset OccurredAt)> executions)
    {
        if (orderQuantity <= 0m) return null;
        var cumulativeQuantity = 0m;
        foreach (var execution in executions.OrderBy(x => x.OccurredAt))
        {
            cumulativeQuantity += execution.Quantity;
            if (cumulativeQuantity >= orderQuantity)
                return DateTimeOffset.FromUnixTimeMilliseconds(execution.OccurredAt.ToUnixTimeMilliseconds());
        }
        return null;
    }
}
