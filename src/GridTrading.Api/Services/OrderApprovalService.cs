using System.Globalization;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchange;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class OrderApprovalPendingException() : Exception("The closing order is waiting for manual approval.");

// Approval is bound to one exact send, not to a command, strategy, or trading session.
public sealed class OrderApprovalService(TradingDbContext db, ExecutionAccountOperationGate gate)
{
    private static bool Current(OrderApprovalEntity approval, OrderEntity order, CycleEntity cycle) =>
        !cycle.IsTerminal && !order.CancellationPending && order.FilledQuantity == approval.FilledQuantity &&
        order.ExchangeOrderId == approval.ExchangeOrderId &&
        (approval.Action == "AMEND" ? order.Status is "NEW" or "PARTIALLY_FILLED" : order.Status == "PENDING_EXCHANGE") &&
        (order.Kind == "FLATTEN" ? cycle.State == "CLOSING" :
            order.Kind == "ENTRY" ? cycle.State == "RUNNING" && !cycle.RiskPaused && !cycle.OperatorPaused :
            cycle.State is "RUNNING" or "PAUSED");

    public async Task<object[]> ListAsync(CancellationToken ct)
    {
        var rows = await (from approval in db.OrderApprovals.AsNoTracking()
            join order in db.Orders.AsNoTracking() on approval.OrderId equals order.Id
            join cycle in db.Cycles.AsNoTracking() on approval.CycleId equals cycle.Id
            join strategy in db.Strategies.AsNoTracking() on cycle.StrategyId equals strategy.Id
            where approval.Status == "PENDING" || approval.Status == "APPROVED" || approval.Status == "REJECTED"
            select new { approval, order, cycle, strategy.Name }).ToListAsync(ct);
        return rows.Where(x => Current(x.approval, x.order, x.cycle))
            .OrderBy(x => x.approval.CreatedAt).Select(x => (object)new
            {
                x.approval.Id, x.approval.OrderId, x.approval.CycleId, x.cycle.StrategyId, strategyName = x.Name,
                x.approval.ExecutionEnvironmentId, x.approval.ExecutionAccountId,
                x.approval.Symbol, x.approval.Side, x.approval.Kind, x.approval.Action,
                x.approval.Price, x.approval.Quantity, x.approval.TimeInForce, x.approval.ReduceOnly,
                x.approval.Status, x.approval.CreatedAt, x.order.GridLevel
            }).ToArray();
    }

    public async Task ApproveAsync(string id, CancellationToken ct)
    {
        var accountId = await db.OrderApprovals.Where(x => x.Id == id).Select(x => x.ExecutionAccountId)
            .SingleOrDefaultAsync(ct) ?? throw new TradingProblemException(404, "APPROVAL_NOT_FOUND", "Order approval was not found.");
        await gate.RunAsync(accountId, async () =>
        {
            var approval = await db.OrderApprovals.SingleAsync(x => x.Id == id, ct);
            var order = await db.Orders.SingleAsync(x => x.Id == approval.OrderId, ct);
            var cycle = await db.Cycles.SingleAsync(x => x.Id == approval.CycleId, ct);
            if (approval.Status is "APPROVED" or "SUBMITTED") return; // Repeated clicks cannot authorize another send.
            if (approval.Status is not ("PENDING" or "REJECTED") || !Current(approval, order, cycle))
                throw new TradingProblemException(409, "ORDER_APPROVAL_STALE", "This order changed or was cancelled. Refresh the pending orders.");
            approval.Status = "APPROVED";
            approval.ApprovedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(new AuditEntity { ResourceId = order.Id, Action = "ORDER_APPROVED", Actor = "operator",
                Detail = $"{approval.Id}: {approval.Action} {approval.Side} {approval.Quantity} {approval.Symbol} @ {approval.Price} {approval.TimeInForce}",
                OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task RejectAsync(string id, CancellationToken ct)
    {
        var accountId = await db.OrderApprovals.Where(x => x.Id == id).Select(x => x.ExecutionAccountId)
            .SingleOrDefaultAsync(ct) ?? throw new TradingProblemException(404, "APPROVAL_NOT_FOUND", "Order approval was not found.");
        await gate.RunAsync(accountId, async () =>
        {
            var approval = await db.OrderApprovals.SingleAsync(x => x.Id == id, ct);
            var order = await db.Orders.SingleAsync(x => x.Id == approval.OrderId, ct);
            var cycle = await db.Cycles.SingleAsync(x => x.Id == approval.CycleId, ct);
            if (approval.Status == "REJECTED") return;
            if (approval.Status is not ("PENDING" or "APPROVED") || !Current(approval, order, cycle))
                throw new TradingProblemException(409, "ORDER_APPROVAL_STALE", "This order changed or was already sent. Refresh the order list.");
            // Retain this exact intent as a durable denial. Strategy maintenance must not
            // requeue or send it, even if confirmation is subsequently switched off.
            approval.Status = "REJECTED";
            approval.ApprovedAt = null;
            db.AuditLogs.Add(new AuditEntity { ResourceId = order.Id, Action = "ORDER_REJECTED", Actor = "operator",
                Detail = $"{approval.Id}: rejected {approval.Action} {approval.Side} {approval.Quantity} {approval.Symbol} @ {approval.Price} {approval.TimeInForce}",
                OccurredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    public static async Task InvalidateAsync(TradingDbContext db, string orderId, CancellationToken ct)
    {
        var approvals = await db.OrderApprovals.Where(x => x.OrderId == orderId &&
            (x.Status == "PENDING" || x.Status == "APPROVED" || x.Status == "REJECTED")).ToListAsync(ct);
        foreach (var approval in approvals) approval.Status = "STALE";
        await db.SaveChangesAsync(ct);
    }

    public static async Task<bool> AuthorizeWireAsync(TradingDbContext db, string accountId,
        string stableClientOrderId, string action, HyperliquidLimitOrder wire, CancellationToken ct)
    {
        // Evaluate the actual rounded wire values, including GTC fallback and amendments.
        var price = decimal.Parse(wire.Price, CultureInfo.InvariantCulture);
        var quantity = decimal.Parse(wire.Size, CultureInfo.InvariantCulture);
        if (!(await new TradingControlSettingsService(db).GetAsync(ct)).RequiresConfirmation(price, quantity) &&
            !await db.OrderApprovals.AnyAsync(x => x.ExecutionAccountId == accountId &&
                x.ClientOrderId == stableClientOrderId && (x.Status == "PENDING" || x.Status == "APPROVED" || x.Status == "REJECTED"), ct)) return true;
        var order = await db.Orders.SingleOrDefaultAsync(x => x.ClientOrderId == stableClientOrderId, ct)
            ?? throw new TradingProblemException(409, "ORDER_REVIEW_REQUIRED", "A tracked order is required for manual approval.");
        var cycle = await db.Cycles.SingleAsync(x => x.Id == order.CycleId && x.ExecutionAccountId == accountId, ct);
        return await AuthorizeAsync(db, new(cycle.ExecutionEnvironmentId, accountId), order, action,
            price, quantity,
            wire.Tif, wire.ReduceOnly, ct);
    }

    public static async Task<bool> AuthorizeAsync(TradingDbContext db, ExecutionSelection selection,
        OrderEntity order, string action, decimal price, decimal quantity, string tif, bool reduceOnly, CancellationToken ct)
    {
        var active = await db.OrderApprovals.Where(x => x.OrderId == order.Id &&
            (x.Status == "PENDING" || x.Status == "APPROVED" || x.Status == "REJECTED")).ToListAsync(ct);
        if (active.Count == 0 && !(await new TradingControlSettingsService(db).GetAsync(ct)).RequiresConfirmation(price, quantity)) return true;
        var match = active.SingleOrDefault(x => x.Action == action && x.Price == price && x.Quantity == quantity &&
            x.TimeInForce == tif && x.ReduceOnly == reduceOnly && x.FilledQuantity == order.FilledQuantity &&
            x.ExchangeOrderId == order.ExchangeOrderId && x.Side == order.Side && x.Symbol == order.Symbol);
        foreach (var stale in active.Where(x => x != match)) stale.Status = "STALE";
        if (match is null)
        {
            db.OrderApprovals.Add(new OrderApprovalEntity
            {
                Id = Ids.New("approval"), OrderId = order.Id, CycleId = order.CycleId,
                ExecutionEnvironmentId = selection.EnvironmentId, ExecutionAccountId = selection.AccountId,
                Symbol = order.Symbol, Side = order.Side, Kind = order.Kind, Action = action,
                Price = price, Quantity = quantity, TimeInForce = tif, ReduceOnly = reduceOnly,
                ClientOrderId = order.ClientOrderId, ExchangeOrderId = order.ExchangeOrderId,
                FilledQuantity = order.FilledQuantity, CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return false;
        }
        await db.SaveChangesAsync(ct);
        if (match.Status != "APPROVED") return false;
        // Commit consumption before external I/O. Uncertain responses never reuse permission.
        var consumed = await db.OrderApprovals.Where(x => x.Id == match.Id && x.Status == "APPROVED")
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "SUBMITTED"), ct);
        await db.Entry(match).ReloadAsync(ct);
        return consumed == 1;
    }
}
