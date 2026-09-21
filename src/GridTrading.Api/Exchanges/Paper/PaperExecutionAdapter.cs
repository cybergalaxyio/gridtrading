using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;

namespace GridTrading.Api.Exchanges.Paper;

public sealed class PaperExecutionAdapter(MarketState market, TradingDbContext db) : IExecutionAdapter, IOrderAmendmentAdapter
{
    public const string AccountId = "acct_paper_01";

    public ExecutionEnvironmentDescriptor Environment { get; } =
        new(ExecutionEnvironmentIds.PaperLocal, "PAPER", "LOCAL", "Paper Local");

    public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>(
            [new(AccountId, ExecutionEnvironmentIds.PaperLocal, "Weekend Paper", true)]);

    public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct)
    {
        EnsureSelection(selection);
        if (!symbol.Equals("SOLUSDT", StringComparison.OrdinalIgnoreCase))
            throw new TradingProblemException(404, "INSTRUMENT_NOT_FOUND", $"{symbol} is not available in the Paper environment.");
        var price = referencePrice is > 0m ? referencePrice.Value : market.Snapshot(symbol).Mid;
        return Task.FromResult(new ExecutionInstrument(symbol.ToUpperInvariant(), Environment.Id, 0, 1, price,
            .001m, .1m, .1m, 5m, 500, .0002m, .00055m, "PAPER_SIMULATOR", DateTimeOffset.UtcNow));
    }

    public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct)
    {
        EnsureSelection(selection);
        var quote = market.Snapshot(symbol);
        return Task.FromResult(new ExecutionQuote(quote.Bid, quote.Ask, quote.Mid, quote.AsOf));
    }

    public async Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct)
    {
        var quote = await GetQuoteAsync(selection, symbol, ct);
        if (market.Snapshot(symbol).IsStale)
            throw new TradingProblemException(503, "MARKET_DATA_STALE", "Fresh market data is required.");
        return quote;
    }

    public async Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
    {
        EnsureSelection(selection);
        foreach (var order in orders.Where(x => x.Status == "PENDING_EXCHANGE"))
        {
            order.ExchangeOrderId = Ids.New("paper");
            order.Status = "NEW";
            await OrderPlacementNotifications.RecordAsync(db, selection, order, ct, quantity: order.Quantity - order.FilledQuantity);
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
    {
        EnsureSelection(selection);
        foreach (var order in orders.Where(IsActive))
        {
            order.Status = "CANCELLED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task AmendOrderAsync(ExecutionSelection selection, GridConfiguration config, OrderEntity order,
        decimal price, decimal quantity, CancellationToken ct)
    {
        EnsureSelection(selection);
        order.Price = price;
        order.Quantity = quantity;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        EnsureSelection(selection);
        var active = db.Orders.Where(x => x.CycleId == cycle.Id).Where(IsActive).ToArray();
        await CancelOrdersAsync(selection, active, ct);
        var strategyPosition = cycle.ReconstructedNetQuantity;
        if (strategyPosition == 0m) return 0m;

        var quote = market.Snapshot(config.Symbol);
        var side = strategyPosition > 0m ? "SELL" : "BUY";
        var price = side == "SELL" ? quote.Bid : quote.Ask;
        var quantity = Math.Abs(strategyPosition);
        var now = DateTimeOffset.UtcNow;
        var order = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New("flatten"),
            ExchangeOrderId = Ids.New("paper"), Symbol = config.Symbol, Side = side, Kind = "FLATTEN",
            Status = "FILLED", GridLevel = -1, Price = price, Quantity = quantity, FilledQuantity = quantity,
            CreatedAt = now, UpdatedAt = now
        };
        var fee = price * quantity * config.TakerFeeRate;
        db.Orders.Add(order);
        await OrderPlacementNotifications.RecordAsync(db, selection, order, ct);
        db.Executions.Add(new ExecutionEntity
        {
            ExecutionAccountId = cycle.ExecutionAccountId,
            Id = Ids.New("execution"), ExchangeExecutionId = Ids.New("paper_fill"), CycleId = cycle.Id,
            OrderId = order.Id, Side = side, Price = price, Quantity = quantity, Fee = fee, OccurredAt = now
        });
        cycle.PaidFees += fee;
        cycle.ActualNetQuantity = 0m;
        cycle.ReconstructedNetQuantity = 0m;
        cycle.LastReconciledAt = now;
        await db.SaveChangesAsync(ct);
        return 0m;
    }

    public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle,
        GridConfiguration config, CancellationToken ct)
    {
        EnsureSelection(selection);
        var open = db.Orders.Where(x => x.CycleId == cycle.Id &&
                (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN"))
            .ToDictionary(x => x.ClientOrderId, x => x.ExchangeOrderId);
        return Task.FromResult(new ExecutionReconciliationSnapshot([], [], [], open, new ExecutionPosition(cycle.ActualNetQuantity,
                cycle.ActualNetQuantity * market.Snapshot(config.Symbol).Mid, 0m)));
    }

    private static bool IsActive(OrderEntity order) =>
        order.Status is "PENDING_EXCHANGE" or "NEW" or "PARTIALLY_FILLED" or "UNKNOWN";

    private static void EnsureSelection(ExecutionSelection selection)
    {
        if (selection.EnvironmentId != ExecutionEnvironmentIds.PaperLocal || selection.AccountId != AccountId)
            throw new TradingProblemException(422, "EXECUTION_SELECTION_MISMATCH", "The Paper adapter received a mismatched environment/account selection.");
    }
}
