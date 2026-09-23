using GridTrading.Api.Data;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    public Task<bool> CloseCycleAsync(CycleEntity cycle, long? expectedVersion, bool emergency,
        string reason, CancellationToken ct) => accountGate.RunAsync(cycle.ExecutionAccountId, async () =>
    {
        await db.Entry(cycle).ReloadAsync(ct);
        if (expectedVersion.HasValue && cycle.StateVersion != expectedVersion)
            throw new TradingProblemException(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
        if (cycle.IsTerminal)
            throw new TradingProblemException(409, "INVALID_CYCLE_STATE", "A terminal cycle cannot be closed again.");
        var restartEligible = !cycle.IsOperatorPaused && !cycle.OperatorResetRequired && cycle.State != "FAULT";
        cycle.State = "CLOSING";
        cycle.StateVersion++;
        await db.SaveChangesAsync(ct);
        try
        {
            var config = DeserializeConfig(cycle);
            var adapter = environments.Adapter(cycle.ExecutionEnvironmentId);
            await ReconcileCoreAsync(cycle, ct, allowExchangeActions: false);
            // Cancellation is allowed even while fills are missing. Never cancel external orders.
            var orders = (await ActiveOrdersAsync(cycle.Id, ct)).Where(x => x.Kind != "FLATTEN").ToArray();
            await adapter.CancelOrdersAsync(Selection(cycle), orders, ct);
            await ReconcileCoreAsync(cycle, ct, allowExchangeActions: false);
            RequireLedgerReady(cycle);
            if ((await ActiveOrdersAsync(cycle.Id, ct)).Count != 0)
                throw new TradingProblemException(503, "CANCEL_INCOMPLETE", "Strategy orders are still unresolved.");
            var residual = await adapter.FlattenAsync(Selection(cycle), cycle, config, ct);
            await ReconcileCoreAsync(cycle, ct, allowExchangeActions: false);
            RequireLedgerReady(cycle);
            if (residual != 0m)
                throw new TradingProblemException(503, "FLATTEN_INCOMPLETE", $"Strategy exposure still has {residual} unfilled; retry Exit after Sync.");
            if (cycle.ReconstructedNetQuantity != 0m || (await ActiveOrdersAsync(cycle.Id, ct)).Count != 0)
                throw new TradingProblemException(503, "FLATTEN_INCOMPLETE", "Confirmed strategy exposure remains; retry Exit after Sync.");
            cycle.State = "WAITING_FOR_OPERATOR";
            cycle.IsTerminal = true;
            cycle.EndedAt = clock.GetUtcNow();
            cycle.OperatorPaused = false;
            cycle.RiskPaused = false;
            cycle.RiskRecoveryChecks = 0;
            cycle.OperatorResetRequired = emergency;
            cycle.ExitReason = emergency ? "EMERGENCY_FLATTEN" : string.IsNullOrWhiteSpace(reason) ? "OPERATOR_CLOSE" : reason;
            var executions = await db.Executions.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
            cycle.RealisedCyclePnl = executions.Sum(x => (x.Side == "SELL" ? 1m : -1m) * x.Price * x.Quantity);
            await db.SaveChangesAsync(ct);
            return restartEligible;
        }
        catch (Exception error)
        {
            cycle.State = "FAULT";
            cycle.ExitReason = error is TradingProblemException problem ? problem.Code : "CLOSE_UNCERTAIN";
            db.RiskAlerts.Add(new RiskAlertEntity { Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                Code = cycle.ExitReason == "FLATTEN_INCOMPLETE" ? "FLATTEN_RESIDUAL_POSITION" : cycle.ExitReason,
                Message = $"Cycle {cycle.Id}: {error.Message}", CreatedAt = clock.GetUtcNow() });
            cycle.LedgerStatus = "RECOVERY_REQUIRED";
            cycle.LedgerError = error.Message;
            accountGate.RequireRecovery(cycle.Id);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }, ct);
}
