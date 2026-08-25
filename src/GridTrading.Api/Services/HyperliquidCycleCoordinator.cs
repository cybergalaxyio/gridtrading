using System.Globalization;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidCycleCoordinator(TradingDbContext db, HyperliquidTradingClient client,
    HyperliquidOrderOwnershipService ownership)
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
        if (check.TradingEquity <= 0m)
            throw new TradingProblemException(412, "TESTNET_ACCOUNT_UNFUNDED",
                $"The Hyperliquid Testnet {check.AccountMode} account has no Trading Equity. Fund it before starting.");
        if (check.AvailableBalance <= 0m)
            throw new TradingProblemException(412, "TESTNET_ACCOUNT_NO_AVAILABLE_BALANCE",
                $"The Hyperliquid Testnet {check.AccountMode} account has {check.TradingEquity} USDC Trading Equity but no available balance for new orders.");
        if (check.NetPosition != 0m)
            throw new TradingProblemException(409, "TESTNET_POSITION_NOT_FLAT", $"Start is blocked because the actual {symbol} position is {check.NetPosition}.");
        if (check.OpenOrderCount != 0)
        {
            using var openOrders = await client.GetOpenOrdersAsync(accountId, ct);
            var trackedCount = await ownership.CountTrackedOpenOrdersAsync(accountId, symbol, null, openOrders.RootElement, ct);
            if (trackedCount != 0)
                throw new TradingProblemException(409, "TESTNET_OPEN_ORDERS_EXIST", $"Start is blocked because {trackedCount} tracked strategy {symbol} order(s) remain open.");
        }
        return await client.GetBookAsync(symbol, ct);
    }

    public async Task PlacePendingOrdersAsync(string accountId, GridConfiguration config, IEnumerable<OrderEntity> source, CancellationToken ct)
    {
        foreach (var order in source.Where(x => x.Status == "PENDING_EXCHANGE").ToArray())
        {
            if (order.Status != "PENDING_EXCHANGE") continue;
            var result = await client.PlaceLimitAsync(accountId, order.Symbol, order.Side == "BUY", order.Price, order.Quantity - order.FilledQuantity,
                order.Kind == "ENTRY" ? config.PostOnlyEntries : config.PostOnlyTakeProfits,
                order.ClientOrderId, ct);
            if (result.Status == "REJECTED" && order.Kind == "TAKE_PROFIT" && config.PostOnlyTakeProfits)
                result = await client.PlaceLimitAsync(accountId, order.Symbol, order.Side == "BUY", order.Price, order.Quantity - order.FilledQuantity,
                    false, order.ClientOrderId, ct);
            if (result.Status == "REJECTED")
            {
                order.Status = "REJECTED"; order.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                throw new TradingProblemException(422, "TESTNET_ORDER_REJECTED", result.Error ?? "Hyperliquid rejected an order.");
            }
            ApplyPlacement(order, result);
            await db.SaveChangesAsync(ct);
        }
    }

    private static void ApplyPlacement(OrderEntity order, HyperliquidOrderResult result)
    {
        order.ExchangeOrderId = result.ExchangeOrderId ?? result.Cloid;
        order.Status = result.Status switch
        {
            "REJECTED" => "REJECTED",
            "WAITING" => "PENDING_EXCHANGE",
            "UNKNOWN" => "UNKNOWN",
            "FILLED" => "PARTIALLY_FILLED",
            _ => "NEW"
        };
        order.UpdatedAt = DateTimeOffset.UtcNow;
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
        using var openOrders = await client.GetOpenOrdersAsync(accountId, ct);
        var remainingOrders = await ownership.CountTrackedOpenOrdersAsync(accountId, config.Symbol, cycle.Id, openOrders.RootElement, ct);
        if (remainingOrders != 0)
            throw new TradingProblemException(503, "CANCEL_INCOMPLETE", $"Close is blocked because {remainingOrders} tracked strategy {config.Symbol} order(s) remain open.");
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
            var result = await client.PlaceLimitAsync(accountId, config.Symbol, isBuy, price, order.Quantity, false,
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

    public async Task<decimal> ReconcileAsync(StrategyEntity strategy, CycleEntity cycle, CancellationToken ct)
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
        var position = await client.GetPositionSnapshotAsync(strategy.ExchangeAccountId, config.Symbol, ct);
        cycle.ActualNetQuantity = position.Quantity;
        cycle.ReconstructedNetQuantity = await ReconstructedPosition(cycle.Id, ct);
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (cycle.State == "RUNNING")
            await MaintainEntryOrdersAsync(strategy, cycle, config, book: null, ct);
        var estimatedFinalTakerFee = Math.Abs(position.PositionValue) * config.TakerFeeRate;
        var estimatedExitSlippage = Math.Abs(position.PositionValue) * config.EstimatedExitSlippagePct / 100m;
        return GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, position.UnrealizedPnl,
            cycle.PaidFees, config.IncludeFunding ? cycle.AccruedFunding : 0m, estimatedFinalTakerFee, estimatedExitSlippage)).LiquidationPnl;
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
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAsync(cycle, order, execution, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CreateTakeProfitAsync(StrategyEntity strategy, CycleEntity cycle, GridConfiguration config, OrderEntity entry,
        ExecutionEntity execution, CancellationToken ct)
    {
        var entrySide = Enum.Parse<OrderSide>(entry.Side, true);
        var tpSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var price = GridMath.TakeProfitPrice(entrySide, execution.Price, config.TakeProfitPoints, TradingService.RulesFor(config).TickSize);
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

    private async Task CloseLotAsync(CycleEntity cycle, OrderEntity tp, ExecutionEntity execution, CancellationToken ct)
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
    }

    public async Task MaintainEntryOrdersAsync(StrategyEntity strategy, CycleEntity cycle, GridConfiguration config,
        HyperliquidBook? book, CancellationToken ct)
    {
        if (cycle.State != "RUNNING") return;
        book ??= await client.GetBookAsync(config.Symbol, ct);
        var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
        var active = await db.Orders.Where(x => x.CycleId == cycle.Id &&
            (x.Status == "PENDING_EXCHANGE" || x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN")).ToListAsync(ct);
        var openLots = await db.VirtualLots.Where(x => x.CycleId == cycle.Id && x.Status != "CLOSED").ToListAsync(ct);
        var created = new List<OrderEntity>();
        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            var sideName = side.ToString().ToUpperInvariant();
            if (active.Any(x => x.Kind == "ENTRY" && x.Side == sideName)) continue;
            var occupied = active.Where(x => x.Kind == "ENTRY" && x.Side == sideName).Select(x => x.GridLevel)
                .Concat(openLots.Where(x => x.Side == sideName).Select(x => x.GridLevel));
            var level = GridMath.SelectWorkingEntryLevel(plan, side, book.Mid, occupied);
            if (level is null) continue;
            var reservations = active.Select(x => new ActiveOrderReservation(Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity));
            var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, cycle.ActualNetQuantity,
                reservations, config.MaxNetLot, TradingService.RulesFor(config));
            if (quantity <= 0m) continue;
            var order = new OrderEntity
            {
                Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"grid-{sideName[0]}-{level.LevelIndex}"),
                ExchangeOrderId = "pending", Symbol = config.Symbol, Side = sideName, Kind = "ENTRY", Status = "PENDING_EXCHANGE",
                GridLevel = level.LevelIndex, Price = level.EntryPrice, Quantity = quantity,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Orders.Add(order); active.Add(order); created.Add(order);
        }
        if (created.Count == 0) return;
        await db.SaveChangesAsync(ct);
        await PlacePendingOrdersAsync(strategy.ExchangeAccountId, config, created, ct);
    }

    private async Task<decimal> ReconstructedPosition(string cycleId, CancellationToken ct)
    {
        var executions = await db.Executions.Where(x => x.CycleId == cycleId).ToListAsync(ct);
        return executions.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);
    }

    private static bool IsActive(OrderEntity x) => x.Status is "PENDING_EXCHANGE" or "NEW" or "PARTIALLY_FILLED" or "UNKNOWN";
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
        var trading = scope.ServiceProvider.GetRequiredService<TradingService>();
        var cycles = await db.Cycles.Where(x => !x.IsTerminal && (x.State == "RUNNING" || x.State == "PAUSED" || x.State == "CLOSING")).ToListAsync(ct);
        foreach (var cycle in cycles)
        {
            var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
            if (DateTimeOffset.UtcNow - cycle.LastReconciledAt < TimeSpan.FromSeconds(Math.Max(2, config.ReconcileIntervalSeconds))) continue;
            var strategy = await db.Strategies.FindAsync([cycle.StrategyId], ct);
            if (strategy is not null && await coordinator.IsTestnetAsync(strategy.ExchangeAccountId, ct))
            {
                try
                {
                    var liquidationPnl = await coordinator.ReconcileAsync(strategy, cycle, ct);
                    var takeProfitTriggered = config.BasketTakeProfitUsdt > 0m && liquidationPnl >= config.BasketTakeProfitUsdt;
                    var stopLossTriggered = GridMath.BasketStopLossTriggered(liquidationPnl, config.BasketStopLossUsdt);
                    if (cycle.State is "RUNNING" or "PAUSED" && (takeProfitTriggered || stopLossTriggered))
                    {
                        var reason = takeProfitTriggered ? "BASKET_TAKE_PROFIT" : "BASKET_STOP_LOSS";
                        await trading.Command(cycle.Id, "CLOSE", reason, $"basket-{cycle.Id}-{cycle.StateVersion}", null, false, ct);
                    }
                }
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
