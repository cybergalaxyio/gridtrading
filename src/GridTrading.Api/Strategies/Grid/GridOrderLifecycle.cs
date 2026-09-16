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
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public Task<int> ProcessFillsAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessFillsCoreAsync(accountId, fills, ct), ct);

    private async Task<int> ProcessFillsCoreAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        foreach (var fill in fills.OrderBy(x => x.OccurredAt))
        {
            var cycle = await FindCycleAsync(accountId, fill.ExchangeOrderId, fill.ClientOrderId, ct);
            if (cycle is null) continue;
            var config = DeserializeConfig(cycle);
            if (!await ApplyFillAsync(cycle, config, fill, ct)) continue;
            processed++;
            affected[cycle.Id] = (cycle, config);
        }

        foreach (var item in affected.Values)
        {
            await UpdateRiskPauseAsync(item.Cycle, ct);
            if (item.Cycle.State == "RUNNING")
                await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        }
        return processed;
    }

    public Task<int> ProcessFundingPaymentsAsync(string accountId, IReadOnlyList<NormalizedFundingPayment> payments, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ApplyFundingPaymentsCoreAsync(accountId, payments, ct), ct);

    private async Task<int> ApplyFundingPaymentsCoreAsync(
        string accountId, IReadOnlyList<NormalizedFundingPayment> payments, CancellationToken ct)
    {
        if (payments.Count == 0) return 0;
        var cycles = await db.Cycles.Where(x => x.ExecutionAccountId == accountId).ToListAsync(ct);
        var processed = 0;
        foreach (var payment in payments.OrderBy(x => x.OccurredAt))
        {
            if (await db.FundingPayments.AnyAsync(x => x.ExchangeFundingId == payment.FundingId, ct)) continue;
            var cycle = cycles
                .Where(x => x.StartedAt <= payment.OccurredAt &&
                    (x.EndedAt == null || x.EndedAt >= payment.OccurredAt))
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefault(x => FundingCoin(DeserializeConfig(x).Symbol) == FundingCoin(payment.Coin));
            if (cycle is null) continue;
            var config = DeserializeConfig(cycle);
            if (!config.IncludeFunding) continue;
            var fundingCost = -payment.UsdcDelta;
            db.FundingPayments.Add(new FundingPaymentEntity
            {
                Id = Ids.New("funding"), ExchangeFundingId = payment.FundingId, CycleId = cycle.Id,
                ExecutionAccountId = accountId, Coin = payment.Coin, UsdcDelta = payment.UsdcDelta,
                FundingCost = fundingCost, PositionQuantity = payment.PositionQuantity,
                FundingRate = payment.FundingRate, OccurredAt = payment.OccurredAt
            });
            cycle.AccruedFunding += fundingCost;
            processed++;
        }
        await db.SaveChangesAsync(ct);
        return processed;
    }

    public Task<int> ProcessOrderUpdatesAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessOrderUpdatesCoreAsync(accountId, updates, ct), ct);

    private async Task<int> ProcessOrderUpdatesCoreAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        var completedEntries = new Dictionary<(string CycleId, string Side), CycleEntity>();
        foreach (var update in updates)
        {
            var order = await FindOrderAsync(accountId, update.ExchangeOrderId, update.ClientOrderId, ct);
            if (order is null) continue;
            var cycle = await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
            // A cancel/open from an earlier amendment generation must not roll
            // the current OID back. REST resolves unknown/new generations by CLOID.
            if (order.ExchangeOrderId != update.ExchangeOrderId || update.OccurredAt < order.LastExchangeUpdateAt) continue;
            if (order.Status is "FILLED" or "CANCELLED" or "REJECTED" && update.Status is "NEW" or "PARTIALLY_FILLED") continue;
            if (order.Status == "FILLED" && update.Status != "FILLED") continue;
            order.Status = update.Status;
            if (update.Status == "FILLED")
            {
                if (update.HasExchangeTimestamp) order.FilledAt ??= update.OccurredAt.ToUniversalTime();
                if (order.Kind == "ENTRY" && cycle.State == "RUNNING")
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
            catch
            {
                if (cycle.RiskPaused && cycle.RiskRecoveryChecks != 0)
                {
                    cycle.RiskRecoveryChecks = 0;
                    await db.SaveChangesAsync(ct);
                }
                throw;
            }
        }, ct);

    private async Task<decimal> ReconcileCoreAsync(CycleEntity cycle, CancellationToken ct)
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

        // FAULT and CLOSING synchronize records only. No placement, amendment,
        // partial-entry cancellation, or entry maintenance is allowed here.
        if (cycle.State is "RUNNING" or "PAUSED")
        {
            // Pauses block even previously persisted, unsent Entry intents.
            if (cycle.State == "PAUSED") await TryCancelPausedEntriesAsync(cycle, ct);
            var pending = await db.Orders.Where(x => x.CycleId == cycle.Id &&
                x.Status == "PENDING_EXCHANGE" && x.Kind == "TAKE_PROFIT").ToListAsync(ct);
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
        var finalFee = Math.Abs(snapshot.Position.PositionValue) * config.TakerFeeRate;
        var slippage = Math.Abs(snapshot.Position.PositionValue) * config.EstimatedExitSlippagePct / 100m;
        return GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, snapshot.Position.UnrealizedPnl,
            cycle.PaidFees, config.IncludeFunding ? cycle.AccruedFunding : 0m, finalFee, slippage)).LiquidationPnl;
    }

    private async Task<bool> ApplyFillAsync(CycleEntity cycle, GridConfiguration config, NormalizedExecutionFill fill, CancellationToken ct, bool allowExchangeActions = true)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.CycleId == cycle.Id &&
            (x.ExchangeOrderId == fill.ExchangeOrderId ||
             (fill.ClientOrderId != null && x.ClientOrderId == fill.ClientOrderId)), ct);
        if (order is null || await db.Executions.AnyAsync(x => x.ExchangeExecutionId == fill.ExecutionId, ct)) return false;
        if (order.ExchangeOrderId == "pending") order.ExchangeOrderId = fill.ExchangeOrderId;
        var terminalBeforeFill = order.Status is "CANCELLED" or "REJECTED";
        var execution = new ExecutionEntity
        {
            Id = Ids.New("execution"), ExchangeExecutionId = fill.ExecutionId, ExchangeOrderId = fill.ExchangeOrderId, CycleId = cycle.Id, OrderId = order.Id,
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
        cycle.PaidFees += fill.Fee;
        cycle.ActualNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;
        cycle.ReconstructedNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;

        if (order.Kind == "ENTRY") await CreateOrAmendTakeProfitAsync(cycle, config, order, execution, ct, allowExchangeActions);
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAsync(cycle, order, execution, ct, allowExchangeActions);
        await db.SaveChangesAsync(ct);
        return true;
    }



    private async Task<CycleEntity?> FindCycleAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct)
    {
        var order = await FindOrderAsync(accountId, exchangeOrderId, clientOrderId, ct);
        return order is null ? null : await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
    }

    private async Task<OrderEntity?> FindOrderAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct) =>
        await (from order in db.Orders
               join cycle in db.Cycles on order.CycleId equals cycle.Id
               where cycle.ExecutionAccountId == accountId && !cycle.IsTerminal &&
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

    private static GridConfiguration DeserializeConfig(CycleEntity cycle) =>
        GridConfigurationCodec.ReadFrozen(cycle.FrozenConfigurationJson);
    private static ExecutionSelection Selection(CycleEntity cycle) =>
        new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);

}
