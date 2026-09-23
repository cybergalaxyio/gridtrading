using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    public bool IsLedgerReady(CycleEntity cycle) => cycle.ExecutionEnvironmentId == ExecutionEnvironmentIds.PaperLocal ||
        (accountGate.IsRecovered(cycle.Id) && cycle.LedgerStatus == "READY" &&
            !db.Orders.Local.Any(x => x.CycleId == cycle.Id && x.CancellationPending));

    public void RegisterNewCycle(CycleEntity cycle)
    {
        cycle.LedgerStatus = "READY";
        cycle.LedgerError = null;
        accountGate.MarkRecovered(cycle.Id);
    }

    public async Task<BasketPnl> ValueAsync(CycleEntity cycle, CancellationToken ct)
    {
        var config = DeserializeConfig(cycle);
        var executions = await db.Executions.AsNoTracking().Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        var quote = GridCycleAccounting.NetQuantity(executions) == 0m ? new ExecutionQuote(0m, 0m, 0m, clock.GetUtcNow()) :
            await environments.Adapter(cycle.ExecutionEnvironmentId).GetQuoteAsync(Selection(cycle), config.Symbol, ct);
        return GridCycleAccounting.Value(cycle, executions, config, quote);
    }

    private async Task<bool> ValidateLedgerAsync(CycleEntity cycle, CancellationToken ct)
    {
        var orders = await db.Orders.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        var executions = await db.Executions.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        var lots = await db.VirtualLots.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        string? error = null;
        foreach (var order in orders)
        {
            var fills = executions.Where(x => x.OrderId == order.Id).Sum(x => x.Quantity);
            var expected = Math.Max(order.FilledQuantity, order.ObservedFilledQuantity ?? 0m);
            if (order.Status == "FILLED") expected = Math.Max(expected, order.Quantity);
            if (order.Status == "UNKNOWN" || order.CancellationPending || fills < expected)
            {
                error = $"Order {order.Id} has an unresolved submission or missing executions. Sync is required.";
                break;
            }
            if (order.Kind == "ENTRY")
            {
                var entryLots = lots.Where(x => x.EntryOrderId == order.Id).ToArray();
                var closed = executions.Where(x => entryLots.Any(l => l.TakeProfitOrderId == x.OrderId)).Sum(x => x.Quantity);
                if (entryLots.Sum(x => x.FilledQuantity) != fills ||
                    entryLots.Sum(x => x.RemainingQuantity) != fills - closed)
                    error = $"Entry {order.Id} lots do not match confirmed entry and take-profit executions.";
            }
        }
        var orderIds = orders.Select(x => x.Id).ToHashSet();
        var net = GridCycleAccounting.NetQuantity(executions);
        var flattenIds = orders.Where(x => x.Kind == "FLATTEN").Select(x => x.Id).ToHashSet();
        var lotNet = lots.Sum(x => (x.Side == "BUY" ? 1m : -1m) * x.RemainingQuantity);
        if (executions.Any(x => !orderIds.Contains(x.OrderId) || x.ExecutionAccountId != cycle.ExecutionAccountId) ||
            lots.Any(x => x.RemainingQuantity < 0m) ||
            lotNet + GridCycleAccounting.NetQuantity(executions.Where(x => flattenIds.Contains(x.OrderId))) != net)
            error = "Strategy lots and confirmed executions do not reconcile. Manual positions cannot repair this ledger.";
        cycle.ReconstructedNetQuantity = net;
        cycle.PaidFees = executions.Sum(x => x.Fee);
        // Legacy funding remains in the audit table, but no longer belongs to an unfinished cycle's PnL.
        if (!cycle.IsTerminal) cycle.AccruedFunding = 0m;
        cycle.LedgerStatus = error is null ? "READY" : "RECOVERY_REQUIRED";
        cycle.LedgerError = error;
        if (error is null) accountGate.MarkRecovered(cycle.Id);
        else
        {
            cycle.RiskRecoveryChecks = 0;
            accountGate.RequireRecovery(cycle.Id);
            if (!await db.RiskAlerts.AnyAsync(x => x.CycleId == cycle.Id && x.Code == "STRATEGY_LEDGER_INCOMPLETE" && !x.Acknowledged, ct))
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Code = "STRATEGY_LEDGER_INCOMPLETE",
                    Severity = "WARNING", Message = error, CreatedAt = clock.GetUtcNow()
                });
        }
        await db.SaveChangesAsync(ct);
        return error is null;
    }

    private void RequireLedgerReady(CycleEntity cycle)
    {
        if (!IsLedgerReady(cycle))
            throw new TradingProblemException(503, "STRATEGY_LEDGER_INCOMPLETE",
                cycle.LedgerError ?? "Reconcile the strategy's orders and fills before trading.");
    }
}
