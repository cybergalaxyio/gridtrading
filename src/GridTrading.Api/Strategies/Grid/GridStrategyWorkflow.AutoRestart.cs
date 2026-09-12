using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridStrategyWorkflow
{
    private static bool IsBasketClose(string reason) => reason is "BASKET_TAKE_PROFIT" or "BASKET_STOP_LOSS";
    private static string AutoRestartKey(string cycleId) => $"auto-restart:{cycleId}";
    private static string AutoStartKey(string cycleId) => $"auto-start:{cycleId}";

    public async Task ProcessAutoRestartAsync(string operationId, CancellationToken ct)
    {
        var pending = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId &&
            x.Type == "AUTO_RESTART" && x.Status == "ACCEPTED", ct);
        if (pending is null) return;
        var cycle = await db.Cycles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pending.ResourceId, ct);
        if (cycle is null) return;
        if (startGate is null) await RestartCoreAsync(operationId, ct);
        else await startGate.RunAsync(cycle.ExecutionAccountId, () => RestartCoreAsync(operationId, ct), ct);
    }

    private async Task RestartCoreAsync(string operationId, CancellationToken ct)
    {
        // Durable claim: two workers, or a repeated scheduler tick, cannot launch twice.
        var claimed = await db.Operations.Where(x => x.Id == operationId && x.Type == "AUTO_RESTART" && x.Status == "ACCEPTED")
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "PROCESSING"), ct);
        if (claimed == 0) return;
        var operation = await db.Operations.SingleAsync(x => x.Id == operationId, ct);
        await db.Entry(operation).ReloadAsync(ct);
        var previous = await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct);
        await db.Entry(previous).ReloadAsync(ct);
        try
        {
            var frozen = JsonSerializer.Deserialize<GridConfiguration>(previous.FrozenConfigurationJson, JsonSupport.Options)!;
            var strategy = await db.Strategies.SingleOrDefaultAsync(x => x.Id == previous.StrategyId, ct);
            if (strategy is not null) await db.Entry(strategy).ReloadAsync(ct);
            if (!previous.IsTerminal || !IsBasketClose(previous.ExitReason) || previous.OperatorResetRequired ||
                !frozen.AutoRestart || strategy is null || strategy.Archived)
            {
                await FinishAutoRestartAsync(operation, "CANCELLED", "RESTART_NOT_ELIGIBLE", ct);
                return;
            }
            var settings = DeserializeStrategy(strategy);
            if (!settings.AutoRestart)
            {
                await FinishAutoRestartAsync(operation, "CANCELLED", "AUTO_RESTART_DISABLED", ct);
                return;
            }
            // A manual start supersedes this request even if that newer cycle has already ended.
            var otherCycles = await db.Cycles.AsNoTracking().Where(x => x.StrategyId == strategy.Id && x.Id != previous.Id).ToListAsync(ct);
            if (otherCycles.Any(x => !x.IsTerminal || x.StartedAt >= previous.StartedAt))
            {
                await FinishAutoRestartAsync(operation, "CANCELLED", "RESTART_SUPERSEDED", ct);
                return;
            }
            if (!strategy.Symbol.Equals(frozen.Symbol, StringComparison.OrdinalIgnoreCase))
                throw Problem(409, "RESTART_SYMBOL_CHANGED", "The strategy symbol changed. Start the new symbol manually.");
            await lifecycle.ReconcileClosedCycleUnderGateAsync(previous, ct);
            if (previous.ActualNetQuantity != 0m || previous.ReconstructedNetQuantity != 0m ||
                (await ActiveOrdersAsync(previous.Id, null, ct)).Count != 0)
                throw Problem(409, "RESTART_CLOSE_NOT_CLEAN", "Previous cycle still has exposure or unresolved orders.");
            var lots = await db.VirtualLots.AsNoTracking().Where(x => x.CycleId == previous.Id).ToListAsync(ct);
            var executions = await db.Executions.AsNoTracking().Where(x => x.CycleId == previous.Id).ToListAsync(ct);
            var flattenOrderIds = await db.Orders.AsNoTracking().Where(x => x.CycleId == previous.Id && x.Kind == "FLATTEN")
                .Select(x => x.Id).ToListAsync(ct);
            var executionNet = executions.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);
            var remainingLotNet = lots.Sum(x => x.Side == "BUY" ? x.RemainingQuantity : -x.RemainingQuantity);
            var flattenedNet = executions.Where(x => flattenOrderIds.Contains(x.OrderId))
                .Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);
            // Cycle flattening preserves historical lots. Prove that executed closes offset
            // their net quantity; never erase them or infer settlement from order status alone.
            if (executionNet != 0m || remainingLotNet + flattenedNet != 0m || lots.Any(x => x.RemainingQuantity < 0m))
                throw Problem(409, "RESTART_CLOSE_NOT_CLEAN", "Previous cycle lots and confirmed flatten executions do not reconcile to zero.");

            // Keep the execution account/environment that the operator originally started.
            // Current saved parameters apply to the next cycle; startup still fetches a fresh quote.
            var preview = await CreatePreviewAsync(new(strategy.Id, strategy.Version, settings.ManualCenterPrice ?? 0m,
                null, null, previous.ExecutionEnvironmentId, previous.ExecutionAccountId), ct);
            var (_, next) = await StartCoreAsync(strategy.Id,
                new(preview.Id, preview.Plan.CenterPrice, new(true, true, previous.ExecutionEnvironmentId)),
                AutoStartKey(previous.Id), ct);
            db.AuditLogs.Add(Audit(previous.Id, "AUTO_RESTART_COMPLETED", $"Started cycle {next.Id}."));
            await FinishAutoRestartAsync(operation, "COMPLETED", "", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await FailAutoRestartAsync(operation, previous, ex is TradingProblemException problem ? problem.Code :
                ex is GridValidationException validation ? validation.Code : "AUTO_RESTART_FAILED", ex.Message, ct);
        }
    }

    // Called once on server startup. Never resend uncertain exchange actions after a crash.
    public async Task RecoverInterruptedAutoRestartsAsync(CancellationToken ct)
    {
        var interrupted = await db.Operations.Where(x => x.Type == "AUTO_RESTART" && x.Status == "PROCESSING").ToListAsync(ct);
        foreach (var operation in interrupted)
        {
            var start = await db.Operations.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == AutoStartKey(operation.ResourceId), ct);
            if (start?.Status == "COMPLETED")
            {
                await FinishAutoRestartAsync(operation, "COMPLETED", "", ct);
                continue;
            }
            var previous = await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct);
            if (start is not null)
            {
                var next = await db.Cycles.SingleOrDefaultAsync(x => x.Id == start.ResourceId, ct);
                if (next is { IsTerminal: false, State: "STARTING" })
                {
                    next.State = "FAULT";
                    next.OperatorResetRequired = true;
                    next.ExitReason = "AUTO_RESTART_INTERRUPTED";
                    next.StateVersion++;
                }
            }
            await FailAutoRestartAsync(operation, previous, "AUTO_RESTART_INTERRUPTED",
                "Server stopped during automatic startup. Check orders and positions before starting again.", ct);
        }
    }

    private async Task FailAutoRestartAsync(OperationEntity operation, CycleEntity previous, string code, string detail, CancellationToken ct)
    {
        db.RiskAlerts.Add(FaultAlert(previous, "AUTO_RESTART_FAILED",
            $"Cycle {previous.Id} 自动重启已停止 [{code}]：{detail} 请检查仓位和挂单后手动 Start。"));
        db.AuditLogs.Add(Audit(previous.Id, "AUTO_RESTART_FAILED", $"[{code}] {detail}"));
        await FinishAutoRestartAsync(operation, "FAILED", code, ct);
    }

    private async Task FinishAutoRestartAsync(OperationEntity operation, string status, string error, CancellationToken ct)
    {
        operation.Status = status;
        operation.ErrorCode = error;
        operation.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
