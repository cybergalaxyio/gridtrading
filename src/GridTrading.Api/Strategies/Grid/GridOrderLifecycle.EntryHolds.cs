using GridTrading.Api.Data;
using GridTrading.Domain;

namespace GridTrading.Api.Strategies.Grid;

public sealed record EntryHold(string Side, string Reason, DateTimeOffset? ResumeAt = null,
    int? FilledCount = null, int? FillLimit = null);

public sealed partial class GridOrderLifecycle
{
    public async Task<IReadOnlyList<EntryHold>> ReadEntryHoldsAsync(CycleEntity cycle,
        GridConfiguration config, CancellationToken ct)
    {
        if (cycle.IsTerminal || cycle.State is not ("RUNNING" or "PAUSED") ||
            config.GridMode == GridMode.BuyOnly) return [];
        var limit = await ReadEntryFillLimitAsync(cycle, config, OrderSide.Sell, ct);
        return limit is { Reason: "FILL_LIMIT" } ? [limit] : [];
    }
}
