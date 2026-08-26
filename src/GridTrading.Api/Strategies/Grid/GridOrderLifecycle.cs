using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle(
    TradingDbContext db,
    ExecutionEnvironmentRegistry environments,
    ExecutionAccountOperationGate accountGate)
{
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

        foreach (var item in affected.Values.Where(x => x.Cycle.State == "RUNNING"))
            await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
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
        foreach (var update in updates)
        {
            var order = await FindOrderAsync(accountId, update.ExchangeOrderId, update.ClientOrderId, ct);
            if (order is null) continue;
            var cycle = await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
            order.ExchangeOrderId = string.IsNullOrWhiteSpace(update.ExchangeOrderId) ? order.ExchangeOrderId : update.ExchangeOrderId;
            order.Status = update.Status;
            order.UpdatedAt = update.OccurredAt;
            processed++;
            if (update.Status == "CANCELLED" && update.FilledQuantity <= order.FilledQuantity && cycle.State == "RUNNING")
                affected[cycle.Id] = (cycle, DeserializeConfig(cycle));
        }
        await db.SaveChangesAsync(ct);
        foreach (var item in affected.Values)
            await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        return processed;
    }

    public Task<decimal> ReconcileAsync(CycleEntity cycle, CancellationToken ct) =>
        accountGate.RunAsync(cycle.ExecutionAccountId, () => ReconcileCoreAsync(cycle, ct), ct);

    private async Task<decimal> ReconcileCoreAsync(CycleEntity cycle, CancellationToken ct)
    {
        var config = DeserializeConfig(cycle);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var snapshot = await adapter.ReconcileAsync(selection, cycle, config, ct);

        foreach (var fill in snapshot.Fills.OrderBy(x => x.OccurredAt))
            await ApplyFillAsync(cycle, config, fill, ct);
        await ApplyFundingPaymentsCoreAsync(selection.AccountId, snapshot.FundingPayments, ct);
        foreach (var update in snapshot.OrderUpdates)
        {
            var order = await FindOrderAsync(selection.AccountId, update.ExchangeOrderId, update.ClientOrderId, ct);
            if (order is null) continue;
            order.Status = update.Status;
            order.FilledQuantity = Math.Max(order.FilledQuantity, update.FilledQuantity);
            order.UpdatedAt = update.OccurredAt;
        }

        var pending = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Status == "PENDING_EXCHANGE").ToListAsync(ct);
        foreach (var order in pending)
        {
            if (!snapshot.OpenOrdersByClientId.TryGetValue(order.ClientOrderId, out var exchangeId)) continue;
            order.ExchangeOrderId = exchangeId;
            order.Status = "NEW";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        try
        {
            await adapter.PlaceOrdersAsync(selection, config, pending.Where(x => x.Status == "PENDING_EXCHANGE"), ct);
        }
        catch (TradingProblemException ex) when (ex.Code == "PROTECTIVE_ORDER_REJECTED")
        {
            await HandleProtectiveOrderRejectionAsync(cycle, selection, adapter, ex, ct);
            throw;
        }

        var openClientIds = snapshot.OpenOrdersByClientId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var order in await ActiveOrdersAsync(cycle.Id, ct))
        {
            if (order.Status == "PENDING_EXCHANGE" || openClientIds.Contains(order.ClientOrderId) || order.FilledQuantity >= order.Quantity) continue;
            order.Status = "CANCELLED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await CancelExpiredPartialEntriesAsync(cycle, config, openClientIds, ct);

        cycle.ActualNetQuantity = snapshot.Position.Quantity;
        cycle.ReconstructedNetQuantity = await ReconstructedPositionAsync(cycle.Id, ct);
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (cycle.State == "RUNNING") await MaintainEntryOrdersAsync(cycle, config, quote: null, ct);

        var finalFee = Math.Abs(snapshot.Position.PositionValue) * config.TakerFeeRate;
        var slippage = Math.Abs(snapshot.Position.PositionValue) * config.EstimatedExitSlippagePct / 100m;
        return GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, snapshot.Position.UnrealizedPnl,
            cycle.PaidFees, config.IncludeFunding ? cycle.AccruedFunding : 0m, finalFee, slippage)).LiquidationPnl;
    }


    private async Task<bool> ApplyFillAsync(CycleEntity cycle, GridConfiguration config, NormalizedExecutionFill fill, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.CycleId == cycle.Id &&
            (x.ExchangeOrderId == fill.ExchangeOrderId ||
             (fill.ClientOrderId != null && x.ClientOrderId == fill.ClientOrderId)), ct);
        if (order is null || await db.Executions.AnyAsync(x => x.ExchangeExecutionId == fill.ExecutionId, ct)) return false;
        if (!string.IsNullOrWhiteSpace(fill.ExchangeOrderId)) order.ExchangeOrderId = fill.ExchangeOrderId;
        var terminalBeforeFill = order.Status is "CANCELLED" or "REJECTED";
        var execution = new ExecutionEntity
        {
            Id = Ids.New("execution"), ExchangeExecutionId = fill.ExecutionId, CycleId = cycle.Id, OrderId = order.Id,
            Side = fill.Side, Price = fill.Price, Quantity = fill.Quantity, Fee = fill.Fee, OccurredAt = fill.OccurredAt
        };
        db.Executions.Add(execution);
        order.FilledQuantity = Math.Min(order.Quantity, order.FilledQuantity + fill.Quantity);
        if (!terminalBeforeFill) order.Status = order.FilledQuantity >= order.Quantity ? "FILLED" : "PARTIALLY_FILLED";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        cycle.PaidFees += fill.Fee;
        cycle.ActualNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;
        cycle.ReconstructedNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;

        if (order.Kind == "ENTRY") await CreateTakeProfitAsync(cycle, config, order, execution, ct);
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAsync(cycle, order, execution, ct);
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
        JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
    private static ExecutionSelection Selection(CycleEntity cycle) =>
        new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);

}
