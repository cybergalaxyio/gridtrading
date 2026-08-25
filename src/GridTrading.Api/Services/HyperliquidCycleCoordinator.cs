using System.Globalization;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidCycleCoordinator(TradingDbContext db, HyperliquidTradingClient client)
{
    public Task<bool> IsTestnetAsync(string accountId, CancellationToken ct) =>
        db.HyperliquidAccounts.AnyAsync(x => x.Id == accountId && x.Enabled && x.Environment == "TESTNET", ct);

    public async Task EnsureKnownAccountAsync(string accountId, CancellationToken ct)
    {
        if (accountId == "acct_paper_01" || await IsTestnetAsync(accountId, ct)) return;
        throw new TradingProblemException(404, "EXCHANGE_ACCOUNT_NOT_FOUND", "Select the Paper account or a configured Hyperliquid Testnet account.");
    }

    public async Task<HyperliquidBook> PreflightStartAsync(string accountId, string symbol, CancellationToken ct)
    {
        var check = await client.PreflightAsync(accountId, symbol, ct);
        if (!check.AgentApproved)
            throw new TradingProblemException(412, "API_WALLET_NOT_APPROVED", "The configured API Wallet is not approved as an agent on Hyperliquid Testnet.");
        if (check.AccountValue <= 0m)
            throw new TradingProblemException(412, "TESTNET_ACCOUNT_UNFUNDED", "The Hyperliquid Testnet account has no account value. Fund it before starting.");
        if (check.NetPosition != 0m)
            throw new TradingProblemException(409, "TESTNET_POSITION_NOT_FLAT", $"Start is blocked because the actual {symbol} position is {check.NetPosition}.");
        if (check.OpenOrderCount != 0)
            throw new TradingProblemException(409, "TESTNET_OPEN_ORDERS_EXIST", $"Start is blocked because {check.OpenOrderCount} actual {symbol} orders already exist.");
        return await client.GetBookAsync(symbol, ct);
    }

    public async Task PlacePendingOrdersAsync(string accountId, GridConfiguration config, IEnumerable<OrderEntity> source, CancellationToken ct)
    {
        foreach (var order in source.Where(x => x.Status == "PENDING_EXCHANGE").ToArray())
        {
            var result = await client.PlaceLimitAsync(accountId, order.Symbol, order.Side == "BUY", order.Price, order.Quantity - order.FilledQuantity,
                order.Kind == "ENTRY" ? config.PostOnlyEntries : config.PostOnlyTakeProfits,
                order.Kind is "TAKE_PROFIT" or "FLATTEN", order.ClientOrderId, ct);
            if (result.Status == "REJECTED" && order.Kind == "TAKE_PROFIT" && config.PostOnlyTakeProfits)
                result = await client.PlaceLimitAsync(accountId, order.Symbol, order.Side == "BUY", order.Price, order.Quantity - order.FilledQuantity,
                    false, true, order.ClientOrderId, ct);
            if (result.Status == "REJECTED")
            {
                order.Status = "REJECTED"; order.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                throw new TradingProblemException(422, "TESTNET_ORDER_REJECTED", result.Error ?? "Hyperliquid rejected an order.");
            }
            order.ExchangeOrderId = result.ExchangeOrderId ?? result.Cloid;
            order.Status = result.Status == "UNKNOWN" ? "UNKNOWN" : result.Status == "FILLED" ? "PARTIALLY_FILLED" : "NEW";
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task CancelAsync(string accountId, IEnumerable<OrderEntity> source, CancellationToken ct)
    {
        foreach (var order in source.Where(IsActive).ToArray())
        {
            await client.CancelByCloidAsync(accountId, order.Symbol, order.ClientOrderId, ct);
            order.Status = "CANCELLED"; order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<decimal> FlattenAsync(string accountId, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        var active = await db.Orders.Where(x => x.CycleId == cycle.Id &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN")).ToListAsync(ct);
        await CancelAsync(accountId, active, ct);
        var remainingOrders = await client.GetOpenOrderCountAsync(accountId, config.Symbol, ct);
        if (remainingOrders != 0)
            throw new TradingProblemException(503, "CANCEL_INCOMPLETE", $"Close is blocked because {remainingOrders} actual {config.Symbol} order(s) remain open.");
        var before = await client.GetPositionAsync(accountId, config.Symbol, ct);
        cycle.ActualNetQuantity = before;
        if (before != 0m)
        {
            var book = await client.GetBookAsync(config.Symbol, ct);
            var isBuy = before < 0m;
            var price = isBuy ? book.Ask * 1.02m : book.Bid * .98m;
            var order = new OrderEntity
            {
                Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New("flatten"), ExchangeOrderId = "pending",
                Symbol = config.Symbol, Side = isBuy ? "BUY" : "SELL", Kind = "FLATTEN", Status = "PENDING_EXCHANGE",
                GridLevel = -1, Price = price, Quantity = Math.Abs(before), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Orders.Add(order); await db.SaveChangesAsync(ct);
            var result = await client.PlaceLimitAsync(accountId, config.Symbol, isBuy, price, order.Quantity, false, true,
                order.ClientOrderId, ct, immediateOrCancel: true);
            order.ExchangeOrderId = result.ExchangeOrderId ?? result.Cloid;
            order.Status = result.Status == "FILLED" ? "PARTIALLY_FILLED" : result.Status == "REJECTED" ? "REJECTED" : "UNKNOWN";
            await db.SaveChangesAsync(ct);
            await Task.Delay(400, ct);
        }
        var after = await client.GetPositionAsync(accountId, config.Symbol, ct);
        cycle.ActualNetQuantity = after;
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return after;
    }

    public async Task ReconcileAsync(StrategyEntity strategy, CycleEntity cycle, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        using var fills = await client.GetUserFillsAsync(strategy.ExchangeAccountId,
            Math.Max(0, cycle.LastReconciledAt.AddSeconds(-5).ToUnixTimeMilliseconds()), ct);
        if (fills.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fill in fills.RootElement.EnumerateArray().OrderBy(ReadTime))
                await ApplyFillAsync(strategy, cycle, config, fill, ct);
        }

        using var open = await client.GetOpenOrdersAsync(strategy.ExchangeAccountId, ct);
        var openIds = open.RootElement.ValueKind == JsonValueKind.Array
            ? open.RootElement.EnumerateArray().Select(x => x.GetProperty("oid").ToString()).ToHashSet()
            : [];
        var openByCloid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (open.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var row in open.RootElement.EnumerateArray())
                if (row.TryGetProperty("cloid", out var cloid) && cloid.ValueKind == JsonValueKind.String)
                    openByCloid[cloid.GetString()!] = row.GetProperty("oid").ToString();
        var pending = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Status == "PENDING_EXCHANGE").ToListAsync(ct);
        foreach (var order in pending)
            if (openByCloid.TryGetValue(Exchange.HyperliquidWireCodec.CreateCloid(order.ClientOrderId), out var exchangeId))
            { order.ExchangeOrderId = exchangeId; order.Status = "NEW"; order.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        await PlacePendingOrdersAsync(strategy.ExchangeAccountId, config, pending.Where(x => x.Status == "PENDING_EXCHANGE"), ct);
        foreach (var order in pending.Where(x => x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")) openIds.Add(order.ExchangeOrderId);
        var localActive = await db.Orders.Where(x => x.CycleId == cycle.Id &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN")).ToListAsync(ct);
        foreach (var order in localActive.Where(x => !openIds.Contains(x.ExchangeOrderId) && x.FilledQuantity < x.Quantity))
        {
            order.Status = order.FilledQuantity > 0m ? "PARTIALLY_FILLED" : "CANCELLED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var actualPosition = await client.GetPositionAsync(strategy.ExchangeAccountId, config.Symbol, ct);
        cycle.ActualNetQuantity = actualPosition;
        cycle.ReconstructedNetQuantity = await ReconstructedPosition(cycle.Id, ct);
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyFillAsync(StrategyEntity strategy, CycleEntity cycle, GridConfiguration config, JsonElement fill, CancellationToken ct)
    {
        var oid = fill.GetProperty("oid").ToString();
        var order = await db.Orders.SingleOrDefaultAsync(x => x.CycleId == cycle.Id && x.ExchangeOrderId == oid, ct);
        if (order is null && fill.TryGetProperty("cloid", out var cloid) && cloid.ValueKind == JsonValueKind.String)
        {
            var value = cloid.GetString();
            order = (await db.Orders.Where(x => x.CycleId == cycle.Id).ToListAsync(ct))
                .SingleOrDefault(x => Exchange.HyperliquidWireCodec.CreateCloid(x.ClientOrderId) == value);
            if (order is not null) order.ExchangeOrderId = oid;
        }
        if (order is null) return;
        var executionKey = $"hl:{fill.GetProperty("hash").GetString()}:{oid}:{ReadTime(fill)}:{ReadString(fill, "tid")}";
        if (await db.Executions.AnyAsync(x => x.ExchangeExecutionId == executionKey, ct)) return;
        var quantity = ReadDecimal(fill, "sz"); var price = ReadDecimal(fill, "px"); var fee = Math.Abs(ReadDecimal(fill, "fee"));
        var side = ReadString(fill, "side") == "B" ? "BUY" : "SELL";
        var execution = new ExecutionEntity
        {
            Id = Ids.New("execution"), ExchangeExecutionId = executionKey, CycleId = cycle.Id, OrderId = order.Id,
            Side = side, Price = price, Quantity = quantity, Fee = fee,
            OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(ReadTime(fill))
        };
        db.Executions.Add(execution);
        order.FilledQuantity = Math.Min(order.Quantity, order.FilledQuantity + quantity);
        order.Status = order.FilledQuantity >= order.Quantity ? "FILLED" : "PARTIALLY_FILLED";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        cycle.PaidFees += fee;
        cycle.ReconstructedNetQuantity += side == "BUY" ? quantity : -quantity;

        if (order.Kind == "ENTRY") await CreateTakeProfitAsync(strategy, cycle, config, order, execution, ct);
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAndReenterAsync(strategy, cycle, config, order, execution, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CreateTakeProfitAsync(StrategyEntity strategy, CycleEntity cycle, GridConfiguration config, OrderEntity entry,
        ExecutionEntity execution, CancellationToken ct)
    {
        var entrySide = Enum.Parse<OrderSide>(entry.Side, true);
        var tpSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var price = GridMath.TakeProfitPrice(entrySide, execution.Price, config.TakeProfitPoints, TradingService.SolRules.TickSize);
        var tp = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = $"tp-{execution.ExchangeExecutionId}", ExchangeOrderId = "pending",
            Symbol = entry.Symbol, Side = tpSide.ToString().ToUpperInvariant(), Kind = "TAKE_PROFIT", Status = "PENDING_EXCHANGE",
            GridLevel = entry.GridLevel, Price = price, Quantity = execution.Quantity, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(tp);
        db.VirtualLots.Add(new VirtualLotEntity
        {
            Id = Ids.New("lot"), CycleId = cycle.Id, EntryOrderId = entry.Id, TakeProfitOrderId = tp.Id, Side = entry.Side,
            Status = "TP_PENDING", GridLevel = entry.GridLevel, EntryFillPrice = execution.Price,
            FilledQuantity = execution.Quantity, RemainingQuantity = execution.Quantity, TakeProfitPrice = price, EntryFee = execution.Fee
        });
        await db.SaveChangesAsync(ct);
        await PlacePendingOrdersAsync(strategy.ExchangeAccountId, config, [tp], ct);
    }

    private async Task CloseLotAndReenterAsync(StrategyEntity strategy, CycleEntity cycle, GridConfiguration config, OrderEntity tp,
        ExecutionEntity execution, CancellationToken ct)
    {
        var lot = await db.VirtualLots.SingleOrDefaultAsync(x => x.TakeProfitOrderId == tp.Id, ct);
        if (lot is null) return;
        var closed = Math.Min(lot.RemainingQuantity, execution.Quantity);
        lot.RemainingQuantity -= closed; lot.ExitFee += execution.Fee;
        cycle.RealisedCyclePnl += lot.Side == "BUY"
            ? (execution.Price - lot.EntryFillPrice) * closed
            : (lot.EntryFillPrice - execution.Price) * closed;
        if (lot.RemainingQuantity > 0m) return;
        lot.Status = "CLOSED";
        if (cycle.State != "RUNNING") return;
        if (await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Side == lot.Side && x.GridLevel == lot.GridLevel && x.Status != "CLOSED" && x.Id != lot.Id, ct)) return;
        if (await db.Orders.AnyAsync(x => x.CycleId == cycle.Id && x.Kind == "ENTRY" && x.Side == lot.Side && x.GridLevel == lot.GridLevel &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "PENDING_EXCHANGE"), ct)) return;
        var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
        var side = Enum.Parse<OrderSide>(lot.Side, true);
        var level = plan.Levels.Single(x => x.Side == side && x.LevelIndex == lot.GridLevel);
        var active = await db.Orders.Where(x => x.CycleId == cycle.Id &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "PENDING_EXCHANGE")).ToListAsync(ct);
        var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, cycle.ActualNetQuantity,
            active.Select(x => new ActiveOrderReservation(Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity)),
            config.MaxNetLot, TradingService.SolRules);
        if (quantity <= 0m) return;
        var replacement = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = $"reentry-{lot.Id}", ExchangeOrderId = "pending",
            Symbol = config.Symbol, Side = lot.Side, Kind = "ENTRY", Status = "PENDING_EXCHANGE", GridLevel = lot.GridLevel,
            Price = level.EntryPrice, Quantity = quantity, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(replacement); await db.SaveChangesAsync(ct);
        await PlacePendingOrdersAsync(strategy.ExchangeAccountId, config, [replacement], ct);
    }

    private async Task<decimal> ReconstructedPosition(string cycleId, CancellationToken ct)
    {
        var executions = await db.Executions.Where(x => x.CycleId == cycleId).ToListAsync(ct);
        return executions.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);
    }

    private static bool IsActive(OrderEntity x) => x.Status is "NEW" or "PARTIALLY_FILLED" or "UNKNOWN";
    private static long ReadTime(JsonElement value) => value.GetProperty("time").GetInt64();
    private static string ReadString(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : "";
    private static decimal ReadDecimal(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        ? decimal.Parse(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString(), CultureInfo.InvariantCulture) : 0m;
}

public sealed class HyperliquidReconciliationService(IServiceScopeFactory scopeFactory, ILogger<HyperliquidReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await Tick(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Hyperliquid Testnet reconciliation tick failed: {ErrorType}", ex.GetType().Name); }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var coordinator = scope.ServiceProvider.GetRequiredService<HyperliquidCycleCoordinator>();
        var cycles = await db.Cycles.Where(x => !x.IsTerminal && (x.State == "RUNNING" || x.State == "PAUSED" || x.State == "CLOSING")).ToListAsync(ct);
        foreach (var cycle in cycles)
        {
            var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
            if (DateTimeOffset.UtcNow - cycle.LastReconciledAt < TimeSpan.FromSeconds(Math.Max(2, config.ReconcileIntervalSeconds))) continue;
            var strategy = await db.Strategies.FindAsync([cycle.StrategyId], ct);
            if (strategy is not null && await coordinator.IsTestnetAsync(strategy.ExchangeAccountId, ct))
            {
                try { await coordinator.ReconcileAsync(strategy, cycle, ct); }
                catch (TradingProblemException ex) when (ex.Code == "TESTNET_ORDER_REJECTED")
                {
                    cycle.State = "FAULT"; cycle.StateVersion++;
                    db.RiskAlerts.Add(new RiskAlertEntity { Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                        Code = "TESTNET_PROTECTIVE_ORDER_REJECTED", Message = "Hyperliquid rejected a strategy order; inspect actual position and close the cycle.", CreatedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(ct);
                    logger.LogError("Hyperliquid Testnet cycle {CycleId} entered FAULT after an order rejection.", cycle.Id);
                }
            }
        }
    }
}
