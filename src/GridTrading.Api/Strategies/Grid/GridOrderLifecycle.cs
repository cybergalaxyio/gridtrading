using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle(
    TradingDbContext db,
    ExecutionEnvironmentRegistry environments,
    ExecutionAccountOperationGate accountGate, TimeProvider? timeProvider = null)
{
    internal Task<T> RunAccountOperationAsync<T>(string accountId, Func<Task<T>> action, CancellationToken ct) =>
        accountGate.RunAsync(accountId, action, ct);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public Task<int> ProcessFillsAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessFillsCoreAsync(accountId, fills, ct), ct);

    private async Task<int> ProcessFillsCoreAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        var refreshedCycles = new HashSet<string>();
        foreach (var fill in fills.OrderBy(x => x.OccurredAt))
        {
            var cycle = await FindCycleAsync(accountId, fill.ExchangeOrderId, fill.ClientOrderId, ct, fill.ExecutionEnvironmentId);
            if (cycle is null) continue;
            if (refreshedCycles.Add(cycle.Id)) await RefreshLiveParametersAsync(cycle, ct);
            var config = DeserializeConfig(cycle);
            if (!await ApplyFillAsync(cycle, config, fill, ct, allowExchangeActions: IsLedgerReady(cycle))) continue;
            processed++;
            affected[cycle.Id] = (cycle, config);
        }

        foreach (var item in affected.Values)
        {
            if (!IsLedgerReady(item.Cycle)) continue;
            await UpdateRiskPauseAsync(item.Cycle, ct);
            if (item.Cycle.State == "RUNNING")
                await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        }
        return processed;
    }

    public Task<int> ProcessFundingPaymentsAsync(string accountId, IReadOnlyList<NormalizedFundingPayment> payments, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ApplyFundingPaymentsCoreAsync(accountId, payments, ct), ct);

    private static Task<int> ApplyFundingPaymentsCoreAsync(
        string accountId, IReadOnlyList<NormalizedFundingPayment> payments, CancellationToken ct) =>
        Task.FromResult(0);

    public Task<int> ProcessOrderUpdatesAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessOrderUpdatesCoreAsync(accountId, updates, ct), ct);

    private async Task<int> ProcessOrderUpdatesCoreAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        var refreshedCycles = new HashSet<string>();
        var completedEntries = new Dictionary<(string CycleId, string Side), CycleEntity>();
        foreach (var update in updates)
        {
            var order = await FindOrderAsync(accountId, update.ExchangeOrderId, update.ClientOrderId, ct, update.ExecutionEnvironmentId);
            if (order is null) continue;
            var cycle = await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
            if (refreshedCycles.Add(cycle.Id)) await RefreshLiveParametersAsync(cycle, ct);
            // A cancel/open from an earlier amendment generation must not roll
            // the current OID back. REST resolves unknown/new generations by CLOID.
            if (order.ExchangeOrderId != update.ExchangeOrderId || update.OccurredAt < order.LastExchangeUpdateAt) continue;
            if (order.Status is "FILLED" or "CANCELLED" or "REJECTED" && update.Status is "NEW" or "PARTIALLY_FILLED") continue;
            if (order.Status == "FILLED" && update.Status != "FILLED") continue;
            order.Status = update.Status;
            var previousGenerations = (await db.Executions.Where(x => x.OrderId == order.Id).ToListAsync(ct))
                .Where(x => VenueOrderId(x, order) != update.ExchangeOrderId).Sum(x => x.Quantity);
            order.ObservedFilledQuantity = Math.Max(order.ObservedFilledQuantity ?? 0m, previousGenerations + update.FilledQuantity);
            if (update.Status is "CANCELLED" or "REJECTED" or "FILLED") order.CancellationPending = false;
            if (order.ObservedFilledQuantity > order.FilledQuantity)
            {
                cycle.LedgerStatus = "RECOVERY_REQUIRED";
                cycle.LedgerError = $"Waiting for executions of order {order.Id}.";
                accountGate.RequireRecovery(cycle.Id);
            }
            if (update.Status == "FILLED")
            {
                if (update.HasExchangeTimestamp) order.FilledAt ??= update.OccurredAt.ToUniversalTime();
                await OrderFillNotifications.RecordConfirmedAsync(db, Selection(cycle), order,
                    order.FilledAt ?? update.OccurredAt, ct);
                if (order.Kind == "ENTRY" && cycle.State == "RUNNING" && IsLedgerReady(cycle))
                    completedEntries[(cycle.Id, order.Side)] = cycle;
            }
            order.LastExchangeUpdateAt = update.OccurredAt;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            processed++;
            if (order.Kind == "TAKE_PROFIT" ||
                (update.Status == "CANCELLED" && update.FilledQuantity <= order.FilledQuantity && cycle.State == "RUNNING"))
                affected[cycle.Id] = (cycle, DeserializeConfig(cycle));
        }
        await db.SaveChangesAsync(ct);
        // Terminal order notifications may precede fills. Enforce the limit now,
        // but leave replenishment to fill processing so no unrecorded lot is skipped.
        foreach (var item in completedEntries)
            await CanPlaceNewEntryOrderAsync(item.Value, DeserializeConfig(item.Value),
                Enum.Parse<OrderSide>(item.Key.Side, true), ct);
        foreach (var item in affected.Values)
        {
            // A terminal update can finish a cancellation within an already recovered session.
            // A fresh process still requires a complete REST reconciliation.
            if (accountGate.IsRecovered(item.Cycle.Id) && item.Cycle.LedgerStatus != "READY")
                await ValidateLedgerAsync(item.Cycle, ct);
            if (!IsLedgerReady(item.Cycle)) continue;
            await UpdateRiskPauseAsync(item.Cycle, ct);
            await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        }
        return processed;
    }

    public Task BeginClosingAsync(CycleEntity cycle, long? expectedVersion, CancellationToken ct) =>
        accountGate.RunAsync(cycle.ExecutionAccountId, async () =>
        {
            // Recovery and operator commands must not overwrite each other's state transitions.
            await db.Entry(cycle).ReloadAsync(ct);
            if (expectedVersion.HasValue && cycle.StateVersion != expectedVersion)
                throw new TradingProblemException(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
            if (cycle.IsTerminal)
                throw new TradingProblemException(409, "INVALID_CYCLE_STATE", "A terminal cycle cannot be closed again.");
            cycle.State = "CLOSING";
            cycle.StateVersion++;
            await db.SaveChangesAsync(ct);
        }, ct);

    public Task<decimal> ReconcileAsync(CycleEntity cycle, CancellationToken ct) =>
        accountGate.RunAsync(cycle.ExecutionAccountId, async () =>
        {
            try { return await ReconcileCoreAsync(cycle, ct); }
            catch (Exception error)
            {
                accountGate.RequireRecovery(cycle.Id);
                cycle.LedgerStatus = "RECOVERY_REQUIRED";
                cycle.LedgerError = error.Message;
                await db.SaveChangesAsync(ct);
                if (cycle.RiskPaused && cycle.RiskRecoveryChecks != 0)
                {
                    cycle.RiskRecoveryChecks = 0;
                    await db.SaveChangesAsync(ct);
                }
                throw;
            }
        }, ct);

    private async Task<decimal> ReconcileCoreAsync(CycleEntity cycle, CancellationToken ct, bool allowExchangeActions = true)
    {
        // Background selection happens before the account gate. Refresh inside it
        // so an old tracked Cycle cannot overwrite newer fill/fee accumulators.
        await db.Entry(cycle).ReloadAsync(ct);
        var config = DeserializeConfig(cycle);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var snapshot = await adapter.ReconcileAsync(selection, cycle, config, ct);
        foreach (var fill in snapshot.Fills.OrderBy(x => x.OccurredAt))
            await ApplyFillAsync(cycle, config, fill, ct, allowExchangeActions: false);
        await ApplyFundingPaymentsCoreAsync(selection.AccountId, snapshot.FundingPayments, ct);
        await ApplyOrderSnapshotAsync(cycle, snapshot, ct);

        cycle.ActualNetQuantity = snapshot.Position.Quantity;
        cycle.ReconstructedNetQuantity = await ReconstructedPositionAsync(cycle.Id, ct);
        cycle.PaidFees = (await db.Executions.Where(x => x.CycleId == cycle.Id).ToListAsync(ct)).Sum(x => x.Fee);
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (allowExchangeActions && cycle.State == "PAUSED") await TryCancelPausedEntriesAsync(cycle, ct);
        var ledgerReady = await ValidateLedgerAsync(cycle, ct);
        if (!ledgerReady || !allowExchangeActions) return 0m;

        // FAULT and CLOSING synchronize records only. No placement, amendment,
        // partial-entry cancellation, or entry maintenance is allowed here.
        if (cycle.State is "RUNNING" or "PAUSED")
        {
            var pending = await db.Orders.Where(x => x.CycleId == cycle.Id &&
                x.Status == "PENDING_EXCHANGE" && x.Kind == "TAKE_PROFIT").ToListAsync(ct);
            foreach (var order in pending)
            {
                var lot = await db.VirtualLots.SingleOrDefaultAsync(x => x.TakeProfitOrderId == order.Id && x.Status != "CLOSED", ct);
                if (lot is null || lot.RemainingQuantity <= 0m) { order.Status = "CANCELLED"; continue; }
                order.Price = lot.TakeProfitPrice;
                order.Quantity = order.FilledQuantity + lot.RemainingQuantity;
            }
            await db.SaveChangesAsync(ct);
            try { await adapter.PlaceOrdersAsync(selection, config, pending, ct); }
            catch (TradingProblemException ex) when (ex.Code == "PROTECTIVE_ORDER_REJECTED")
            {
                await HandleProtectiveOrderRejectionAsync(cycle, ex, ct);
            }
            if (cycle.State is "RUNNING" or "PAUSED")
            {
                await RecoverLotProtectionAsync(cycle, config, ct);
                // Paused cycles already retry all Entry tails through the tolerant path above.
                if (cycle.State == "RUNNING")
                    await CancelExpiredPartialEntriesAsync(cycle, config, snapshot.OpenOrdersByClientId.Keys.ToHashSet(), ct);
            }
        }
        await UpdateRiskPauseAsync(cycle, ct, confirmedReconciliation: true);
        if (cycle.State == "RUNNING")
        {
            var pendingEntries = await db.Orders.Where(x => x.CycleId == cycle.Id &&
                x.Status == "PENDING_EXCHANGE" && x.Kind == "ENTRY").ToListAsync(ct);
            await PlaceNewEntryOrdersAsync(cycle, config, pendingEntries, ct);
            await MaintainEntryOrdersAsync(cycle, config, quote: null, ct);
        }
        return (await ValueAsync(cycle, ct)).LiquidationPnl;
    }

    private async Task<bool> ApplyFillAsync(CycleEntity cycle, GridConfiguration config, NormalizedExecutionFill fill, CancellationToken ct, bool allowExchangeActions = true)
    {
        if (fill.ExecutionEnvironmentId is not null && fill.ExecutionEnvironmentId != cycle.ExecutionEnvironmentId) return false;
        var order = await db.Orders.SingleOrDefaultAsync(x => x.CycleId == cycle.Id &&
            (x.ExchangeOrderId == fill.ExchangeOrderId ||
             (fill.ClientOrderId != null && x.ClientOrderId == fill.ClientOrderId)), ct);
        if (order is null || await db.Executions.AnyAsync(x => x.ExecutionAccountId == cycle.ExecutionAccountId && x.ExchangeExecutionId == fill.ExecutionId, ct)) return false;
        // An amendment fill can arrive before its new price/size snapshot. Let
        // reconciliation record that generation with its confirmed details.
        if (order.ExchangeOrderId == "pending" || order.ExchangeOrderId.StartsWith("0x", StringComparison.Ordinal))
            await OrderPlacementNotifications.RecordAsync(db, Selection(cycle), order, ct, fill.ExchangeOrderId);
        if (order.ExchangeOrderId == "pending") order.ExchangeOrderId = fill.ExchangeOrderId;
        var terminalBeforeFill = order.Status is "CANCELLED" or "REJECTED";
        var execution = new ExecutionEntity
        {
            Id = Ids.New("execution"), ExecutionAccountId = cycle.ExecutionAccountId, ExchangeExecutionId = fill.ExecutionId, ExchangeOrderId = fill.ExchangeOrderId, CycleId = cycle.Id, OrderId = order.Id,
            Side = fill.Side, Price = fill.Price, Quantity = fill.Quantity, Fee = fill.Fee, OccurredAt = fill.OccurredAt
        };
        db.Executions.Add(execution);
        // Rebuild from executions, not a status/placement ACK that may already
        // include this fill (notably IOC flatten acknowledgements).
        var orderExecutions = await db.Executions.Where(x => x.OrderId == order.Id).ToListAsync(ct);
        order.FilledQuantity = orderExecutions.Sum(x => x.Quantity) + fill.Quantity;
        order.Quantity = Math.Max(order.Quantity, order.FilledQuantity);
        var filledAt = OrderCompletion.FindFilledAt(order.Quantity,
            orderExecutions.Append(execution).Select(x => (x.Quantity, x.OccurredAt)));
        if (filledAt.HasValue) order.FilledAt = filledAt;
        if (!terminalBeforeFill) order.Status = order.FilledQuantity >= order.Quantity ? "FILLED" : "PARTIALLY_FILLED";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        if (filledAt.HasValue && (order.ExchangeOrderId == fill.ExchangeOrderId ||
            order.ExchangeOrderId.StartsWith("0x", StringComparison.Ordinal)))
            await OrderFillNotifications.RecordConfirmedAsync(db, Selection(cycle), order,
                filledAt.Value, ct, fill.ExchangeOrderId);
        cycle.PaidFees += fill.Fee;
        if (cycle.ExecutionEnvironmentId == ExecutionEnvironmentIds.PaperLocal)
            cycle.ActualNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;
        cycle.ReconstructedNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;

        if (order.Kind == "ENTRY") await CreateOrAmendTakeProfitAsync(cycle, config, order, execution, ct, allowExchangeActions);
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAsync(cycle, order, execution, ct, allowExchangeActions);
        await db.SaveChangesAsync(ct);
        return true;
    }



    private async Task<CycleEntity?> FindCycleAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct, string? environmentId = null)
    {
        var order = await FindOrderAsync(accountId, exchangeOrderId, clientOrderId, ct, environmentId);
        return order is null ? null : await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
    }

    private async Task<OrderEntity?> FindOrderAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct, string? environmentId = null) =>
        await (from order in db.Orders
               join cycle in db.Cycles on order.CycleId equals cycle.Id
               where cycle.ExecutionAccountId == accountId && !cycle.IsTerminal &&
                     (environmentId == null || cycle.ExecutionEnvironmentId == environmentId) &&
                     (order.ExchangeOrderId == exchangeOrderId || (clientOrderId != null && order.ClientOrderId == clientOrderId))
               select order).FirstOrDefaultAsync(ct);


    private Task<List<OrderEntity>> ActiveOrdersAsync(string cycleId, CancellationToken ct) =>
        db.Orders.Where(x => x.CycleId == cycleId &&
            (x.Status == "PENDING_EXCHANGE" || x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN"))
            .ToListAsync(ct);

    private async Task<decimal> ReconstructedPositionAsync(string cycleId, CancellationToken ct) =>
        (await db.Executions.Where(x => x.CycleId == cycleId).ToListAsync(ct))
            .Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);

    private static string FundingCoin(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant().Replace("-", "").Replace("_", "").Replace("/", "");
        if (normalized.EndsWith("USDC", StringComparison.Ordinal)) return normalized[..^4];
        if (normalized.EndsWith("USDT", StringComparison.Ordinal)) return normalized[..^4];
        return normalized;
    }

    private async Task RefreshLiveParametersAsync(CycleEntity cycle, CancellationToken ct)
    {
        // Callers may have loaded the cycle before acquiring the gate. Refresh the
        // editable settings and their movement state together without discarding
        // in-memory ledger recovery decisions.
        var saved = await db.Cycles.AsNoTracking().Where(x => x.Id == cycle.Id)
            .Select(x => new { x.LiveConfigurationJson, x.LivePlanJson, x.EntryGridPriceOffset, x.EntryGridMovePendingOrderId, x.StateVersion }).SingleAsync(ct);
        cycle.LiveConfigurationJson = saved.LiveConfigurationJson;
        cycle.LivePlanJson = saved.LivePlanJson;
        cycle.EntryGridPriceOffset = saved.EntryGridPriceOffset;
        cycle.EntryGridMovePendingOrderId = saved.EntryGridMovePendingOrderId;
        cycle.StateVersion = Math.Max(cycle.StateVersion, saved.StateVersion);
    }

    private static GridConfiguration DeserializeConfig(CycleEntity cycle) =>
        cycle.EffectiveConfiguration;
    private static ExecutionSelection Selection(CycleEntity cycle) =>
        new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);

}
