using GridTrading.Api.Data;
using GridTrading.Api.Services;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    // The restart workflow already holds the account gate. Terminal-cycle reconciliation
    // only ingests venue state and executions; it cannot place or maintain orders.
    internal Task<decimal> ReconcileClosedCycleUnderGateAsync(CycleEntity cycle, CancellationToken ct)
    {
        if (!cycle.IsTerminal || cycle.State != "WAITING_FOR_OPERATOR")
            throw new TradingProblemException(409, "RESTART_CLOSE_NOT_CLEAN", "Only a successfully closed cycle can be checked for restart.");
        return ReconcileCoreAsync(cycle, ct);
    }
}
