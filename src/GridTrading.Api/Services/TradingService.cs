using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Hubs;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class PreviewStore
{
    public ConcurrentDictionary<string, PreviewCacheItem> Items { get; } = new();
}

public sealed class LegacyTradingService(TradingDbContext db, MarketState market, PreviewStore previews, IHubContext<TradingHub> hub,
    HyperliquidCycleCoordinator testnet, HyperliquidInfoClient instruments)
{
    public static readonly InstrumentRules SolRules = new("SOLUSDT", .001m, .1m, .1m, 5m, 500);

    public async Task<StrategyEntity> CreateStrategy(StrategyRequest request, CancellationToken ct)
    {
        ValidateStrategy(request);
        await testnet.EnsureKnownAccountAsync(request.ExchangeAccountId, ct);
        var now = DateTimeOffset.UtcNow;
        var entity = new StrategyEntity
        {
            Id = Ids.New("strategy"), Name = request.Name.Trim(), ExchangeAccountId = request.ExchangeAccountId,
            Symbol = request.Symbol.ToUpperInvariant(), ConfigurationJson = JsonSerializer.Serialize(request, JsonSupport.Options),
            CreatedAt = now, UpdatedAt = now
        };
        db.Strategies.Add(entity);
        db.AuditLogs.Add(Audit(entity.Id, "STRATEGY_CREATED", "Strategy configuration created."));
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<StrategyEntity?> UpdateStrategy(string id, StrategyRequest request, CancellationToken ct)
    {
        ValidateStrategy(request);
        await testnet.EnsureKnownAccountAsync(request.ExchangeAccountId, ct);
        var entity = await db.Strategies.FindAsync([id], ct);
        if (entity is null || entity.Archived) return null;
        var hasActiveCycle = await db.Cycles.AnyAsync(x => x.StrategyId == id && !x.IsTerminal, ct);
        var changesExecutionIdentity = !string.Equals(entity.ExchangeAccountId, request.ExchangeAccountId, StringComparison.Ordinal)
            || !string.Equals(entity.Symbol, request.Symbol, StringComparison.OrdinalIgnoreCase);
        if (hasActiveCycle && changesExecutionIdentity)
            throw new TradingProblemException(409, "STRATEGY_EXECUTION_IDENTITY_LOCKED",
                "Close the active cycle before changing its exchange account or symbol. Other template parameters can be edited now.");
        entity.Name = request.Name.Trim(); entity.ExchangeAccountId = request.ExchangeAccountId;
        entity.Symbol = request.Symbol.ToUpperInvariant(); entity.ConfigurationJson = JsonSerializer.Serialize(request, JsonSupport.Options);
        entity.Version++; entity.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(id, "STRATEGY_UPDATED", $"Strategy version updated to {entity.Version}."));
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<PreviewCacheItem> CreatePreview(PreviewRequest request, CancellationToken ct)
    {
        GridConfiguration configuration; string accountId; var version = 0;
        if (!string.IsNullOrWhiteSpace(request.StrategyId))
        {
            var strategy = await db.Strategies.FindAsync([request.StrategyId], ct)
                ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
            if (request.StrategyVersion.HasValue && request.StrategyVersion != strategy.Version)
                throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy version has changed.");
            configuration = DeserializeStrategy(strategy).ToConfiguration(request.ConfirmedCenterPrice);
            accountId = strategy.ExchangeAccountId; version = strategy.Version;
        }
        else
        {
            var c = request.CandidateConfiguration ?? throw Problem(422, "CANDIDATE_REQUIRED", "Candidate configuration is required.");
            configuration = new GridConfiguration
            {
                Symbol = c.Symbol, GridMode = c.GridMode, CenterPrice = request.ConfirmedCenterPrice, MaxLevelsPerSide = c.MaxLevelsPerSide,
                WorkingEntriesPerSide = c.WorkingEntriesPerSide, InitialGapPoints = c.InitialGapPoints,
                GridSpacingPoints = c.GridSpacingPoints, GridSpacingStepPoints = c.GridSpacingStepPoints,
                TakeProfitPoints = c.TakeProfitPoints, BaseLotSize = c.BaseLotSize,
                LotSizeIncreasePercent = c.LotSizeIncreasePercent, MaxTradeLot = c.MaxTradeLot, MaxNetLot = c.MaxNetLot
            };
            accountId = c.ExchangeAccountId;
        }
        EnsureSafeAccount(accountId);
        await testnet.EnsureKnownAccountAsync(accountId, ct);
        var rules = SolRules;
        if (accountId != "acct_paper_01")
        {
            var account = await db.HyperliquidAccounts.SingleAsync(x => x.Id == accountId, ct);
            var metadata = await instruments.GetPerpetualInstrument(configuration.Symbol, configuration.CenterPrice, account.AccountAddress, ct);
            rules = new InstrumentRules(configuration.Symbol, metadata.TickSize, metadata.QuantityStep,
                metadata.MinOrderQuantity, metadata.MinOrderNotional, metadata.MaxActiveOrders);
            configuration = configuration with
            {
                TickSize = metadata.TickSize, QuantityStep = metadata.QuantityStep,
                MinOrderQuantity = metadata.MinOrderQuantity, MinOrderNotional = metadata.MinOrderNotional,
                MaxActiveOrders = metadata.MaxActiveOrders, SizeDecimals = metadata.SizeDecimals,
                MakerFeeRate = metadata.MakerFeeRate, TakerFeeRate = metadata.TakerFeeRate
            };
        }
        else
        {
            configuration = configuration with
            {
                TickSize = rules.TickSize, QuantityStep = rules.QuantityStep,
                MinOrderQuantity = rules.MinOrderQuantity, MinOrderNotional = rules.MinOrderNotional,
                MaxActiveOrders = rules.MaxActiveOrders, SizeDecimals = 1,
                MakerFeeRate = .0002m, TakerFeeRate = .00055m
            };
        }
        var item = new PreviewCacheItem(Ids.New("preview"), request.StrategyId, version, accountId,
            DateTimeOffset.UtcNow.AddMinutes(5), configuration, GridMath.BuildPlan(configuration, rules));
        previews.Items[item.Id] = item;
        return item;
    }

    public async Task<(OperationEntity, CycleEntity)> StartCycle(string strategyId, StartCycleRequest request, string key, CancellationToken ct)
    {
        var confirmation = request.OperatorConfirmation;
        if (!confirmation.ParametersReviewed || !confirmation.CenterConfirmed || confirmation.EnvironmentConfirmed is not ("PAPER" or "TESTNET"))
            throw Problem(422, "OPERATOR_CONFIRMATION_REQUIRED", "Paper/Testnet parameters and centre must be confirmed.");
        if (!previews.Items.TryGetValue(request.PreviewId, out var preview) || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw Problem(422, "PREVIEW_EXPIRED", "Create a fresh grid preview before starting.");
        if (preview.StrategyId != strategyId || preview.Plan.CenterPrice != request.ConfirmedCenterPrice)
            throw Problem(422, "PREVIEW_MISMATCH", "Preview does not match this strategy and centre.");
        var strategy = await db.Strategies.FindAsync([strategyId], ct) ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        if (strategy.Version != preview.StrategyVersion) throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy changed after preview.");
        EnsureSafeAccount(strategy.ExchangeAccountId);
        if (await db.Cycles.AnyAsync(x => x.StrategyId == strategyId && !x.IsTerminal, ct))
            throw Problem(409, "ACTIVE_CYCLE_EXISTS", "Only one active cycle is allowed per strategy.");
        if (market.Snapshot(strategy.Symbol).IsStale) throw Problem(503, "MARKET_DATA_STALE", "Fresh market data is required.");

        var operation = await NewOperation("START_CYCLE", strategyId, key, request, ct);
        if (operation.Status == "COMPLETED") return (operation, await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct));
        var now = DateTimeOffset.UtcNow;
        var cycle = new CycleEntity
        {
            Id = Ids.New("cycle"), StrategyId = strategyId, State = "STARTING", StateVersion = 1,
            FixedCenterPrice = request.ConfirmedCenterPrice,
            FrozenConfigurationJson = JsonSerializer.Serialize(preview.Configuration, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(preview.Plan, JsonSupport.Options), ExitReason = "",
            StartedAt = now, LastReconciledAt = now
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync(ct); // The frozen plan is durable before orders are created.
        var reservations = new List<ActiveOrderReservation>();
        foreach (var level in preview.Plan.Levels.Where(x => x.LevelIndex < preview.Configuration.WorkingEntriesPerSide))
        {
            var allowed = GridMath.AllowedOrderQuantity(level.Side, level.PlannedQuantity, 0m, reservations,
                preview.Configuration.MaxNetLot, TradingService.RulesFor(preview.Configuration));
            if (allowed <= 0m) continue;
            db.Orders.Add(CreateOrder(cycle, strategy.Symbol, level, allowed, "ENTRY"));
            reservations.Add(new ActiveOrderReservation(level.Side, allowed));
        }
        cycle.State = "RUNNING"; cycle.StateVersion++;
        operation.ResourceId = cycle.Id; operation.Status = "COMPLETED"; operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycle.Id, "CYCLE_STARTED", "Grid plan frozen and rolling entries created."));
        await db.SaveChangesAsync(ct); await Broadcast(cycle, ct);
        return (operation, cycle);
    }

    public async Task<OperationEntity> Command(string cycleId, string command, string reason, string key,
        long? expectedVersion, bool emergencyConfirmed, CancellationToken ct)
    {
        var cycle = await db.Cycles.FindAsync([cycleId], ct) ?? throw Problem(404, "CYCLE_NOT_FOUND", "Cycle was not found.");
        if (expectedVersion.HasValue && expectedVersion != cycle.StateVersion)
            throw Problem(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
        var operation = await NewOperation(command, cycleId, key, new { command, reason }, ct);
        if (operation.Status == "COMPLETED") return operation;
        switch (command)
        {
            case "PAUSE_ENTRIES":
                EnsureState(cycle, "RUNNING", command); CancelOrders(cycleId, "ENTRY"); cycle.State = "PAUSED"; break;
            case "RESUME_ENTRIES":
                EnsureState(cycle, "PAUSED", command); cycle.State = "RUNNING"; ReplenishEntries(cycle); break;
            case "CLOSE":
                if (cycle.State is not ("RUNNING" or "PAUSED")) throw InvalidState(cycle, command);
                CloseCycle(cycle, reason, false); break;
            case "EMERGENCY_FLATTEN":
                if (!emergencyConfirmed) throw Problem(422, "EMERGENCY_CONFIRMATION_REQUIRED", "All emergency confirmations are required.");
                CloseCycle(cycle, reason, true); cycle.OperatorResetRequired = true; break;
            case "RECONCILE":
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                cycle.ReconstructedNetQuantity = cycle.ActualNetQuantity; cycle.LastReconciledAt = DateTimeOffset.UtcNow; break;
            default: throw Problem(404, "COMMAND_NOT_FOUND", "Unknown cycle command.");
        }
        cycle.StateVersion++; operation.Status = "COMPLETED"; operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycleId, command, string.IsNullOrWhiteSpace(reason) ? "Operator command." : reason));
        await db.SaveChangesAsync(ct); await Broadcast(cycle, ct); return operation;
    }

    public object Snapshot(CycleEntity cycle)
    {
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        var quote = market.Snapshot(config.Symbol);
        var active = db.Orders.Where(x => x.CycleId == cycle.Id && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")).ToArray();
        var finalFee = Math.Abs(cycle.ActualNetQuantity) * (cycle.ActualNetQuantity >= 0 ? quote.Bid : quote.Ask) * config.TakerFeeRate;
        var pnl = GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, 0m, cycle.PaidFees, cycle.AccruedFunding, finalFee, 0m));
        var usage = config.MaxNetLot == 0m ? 0m : Math.Abs(cycle.ActualNetQuantity) / config.MaxNetLot * 100m;
        var color = usage switch { >= 90m => "RED", >= 70m => "ORANGE", >= 40m => "YELLOW", _ => "GREEN" };
        return new
        {
            cycle = new { cycleId = cycle.Id, cycle.StrategyId, state = cycle.State, cycle.StateVersion, cycle.IsTerminal,
                cycle.OperatorResetRequired, cycle.StartedAt, fixedCenterPrice = cycle.FixedCenterPrice },
            market = quote,
            orders = new { activeEntryCount = active.Count(x => x.Kind == "ENTRY"), activeTakeProfitCount = active.Count(x => x.Kind == "TAKE_PROFIT"), unknownCount = active.Count(x => x.Status == "UNKNOWN") },
            position = new { actualNetQuantity = cycle.ActualNetQuantity, reconstructedNetQuantity = cycle.ReconstructedNetQuantity,
                absoluteMaxNetLotUsagePct = usage, netNotionalUsdt = cycle.ActualNetQuantity * quote.Mid },
            basketPnl = new { pnl.RealisedCyclePnl, pnl.UnrealisedAtExecutablePrice, pnl.PaidFees, pnl.AccruedFunding,
                pnl.EstimatedFinalTakerFee, pnl.EstimatedExitSlippage, pnl.LiquidationPnl,
                takeProfitTarget = config.BasketTakeProfitUsdt, stopLossLimit = config.BasketStopLossUsdt },
            risk = new { color, reasons = color == "GREEN" ? Array.Empty<string>() : new[] { "INVENTORY_ELEVATED" },
                usedBuyLevels = db.Orders.Count(x => x.CycleId == cycle.Id && x.Side == "BUY"), remainingBuyLevels = config.MaxLevelsPerSide,
                usedSellLevels = db.Orders.Count(x => x.CycleId == cycle.Id && x.Side == "SELL"), remainingSellLevels = config.MaxLevelsPerSide },
            health = new { exchange = "HEALTHY", marketData = quote.IsStale ? "STALE" : "FRESH",
                reconciliation = cycle.ActualNetQuantity == cycle.ReconstructedNetQuantity ? "IN_SYNC" : "MISMATCH", lastReconciledAt = cycle.LastReconciledAt },
            allowedCommands = CycleStateMachine.AllowedCommands(ParseState(cycle.State), active.Length > 0 || cycle.ActualNetQuantity != 0m)
        };
    }

    public static StrategyRequest DeserializeStrategy(StrategyEntity entity) =>
        JsonSerializer.Deserialize<StrategyRequest>(entity.ConfigurationJson, JsonSupport.Options)!;

    private async Task<OperationEntity> NewOperation(string type, string resourceId, string key, object request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) throw Problem(400, "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key header is required.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonSupport.Options))));
        var existing = await db.Operations.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw Problem(409, "IDEMPOTENCY_KEY_CONFLICT", "Idempotency key was used with another request.");
            return existing;
        }
        var operation = new OperationEntity { Id = Ids.New("op"), CommandId = Ids.New("cmd"), ResourceId = resourceId,
            Type = type, Status = "ACCEPTED", IdempotencyKey = key, RequestHash = hash, ErrorCode = "", AcceptedAt = DateTimeOffset.UtcNow };
        db.Operations.Add(operation); return operation;
    }

    private void ReplenishEntries(CycleEntity cycle)
    {
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
        var active = db.Orders.Where(x => x.CycleId == cycle.Id && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")).ToList();
        foreach (var level in plan.Levels.Where(x => x.LevelIndex < config.WorkingEntriesPerSide))
        {
            if (active.Any(x => x.Kind == "ENTRY" && x.Side == level.Side.ToString().ToUpperInvariant() && x.GridLevel == level.LevelIndex)) continue;
            var reservations = active.Select(x => new ActiveOrderReservation(Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity));
            var qty = GridMath.AllowedOrderQuantity(level.Side, level.PlannedQuantity, cycle.ActualNetQuantity, reservations, config.MaxNetLot, TradingService.RulesFor(config));
            if (qty <= 0m) continue;
            var order = CreateOrder(cycle, config.Symbol, level, qty, "ENTRY"); db.Orders.Add(order); active.Add(order);
        }
    }

    private void CloseCycle(CycleEntity cycle, string reason, bool emergency)
    {
        cycle.State = "CLOSING"; CancelOrders(cycle.Id, null);
        if (cycle.ActualNetQuantity != 0m)
        {
            var side = cycle.ActualNetQuantity > 0 ? "SELL" : "BUY"; var quote = market.Snapshot("SOLUSDT");
            var price = side == "SELL" ? quote.Bid : quote.Ask; var qty = Math.Abs(cycle.ActualNetQuantity);
            var order = new OrderEntity { Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New("flatten"),
                ExchangeOrderId = Ids.New("paper"), Symbol = "SOLUSDT", Side = side, Kind = "FLATTEN", Status = "FILLED",
                GridLevel = -1, Price = price, Quantity = qty, FilledQuantity = qty, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Orders.Add(order); var fee = price * qty * .00055m;
            db.Executions.Add(new ExecutionEntity { Id = Ids.New("execution"), ExchangeExecutionId = Ids.New("paper_fill"), CycleId = cycle.Id,
                OrderId = order.Id, Side = side, Price = price, Quantity = qty, Fee = fee, OccurredAt = DateTimeOffset.UtcNow });
            cycle.PaidFees += fee; cycle.ActualNetQuantity = 0m; cycle.ReconstructedNetQuantity = 0m;
        }
        cycle.State = "WAITING_FOR_OPERATOR"; cycle.IsTerminal = true; cycle.EndedAt = DateTimeOffset.UtcNow;
        cycle.ExitReason = emergency ? "EMERGENCY_FLATTEN" : string.IsNullOrWhiteSpace(reason) ? "OPERATOR_CLOSE" : reason;
    }

    private void CancelOrders(string cycleId, string? kind)
    {
        var orders = db.Orders.Where(x => x.CycleId == cycleId && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED") && (kind == null || x.Kind == kind));
        foreach (var order in orders) { order.Status = "CANCELLED"; order.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    private static OrderEntity CreateOrder(CycleEntity cycle, string symbol, GridLevel level, decimal quantity, string kind)
    {
        var side = level.Side.ToString().ToUpperInvariant();
        return new OrderEntity { Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"grid-{side[0]}-{level.LevelIndex}"),
            ExchangeOrderId = Ids.New("paper"), Symbol = symbol, Side = side, Kind = kind, Status = "NEW", GridLevel = level.LevelIndex,
            Price = level.EntryPrice, Quantity = quantity, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private async Task Broadcast(CycleEntity cycle, CancellationToken ct) => await hub.Clients.Group($"cycle:{cycle.Id}")
        .SendAsync("CycleStateChanged", new { eventId = Ids.New("evt"), eventType = "CycleStateChanged", occurredAt = DateTimeOffset.UtcNow,
            aggregateType = "Cycle", aggregateId = cycle.Id, aggregateVersion = cycle.StateVersion,
            sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), correlationId = Ids.New("corr"),
            payload = new { state = cycle.State, cycle.StateVersion } }, ct);

    private static AuditEntity Audit(string id, string action, string detail) => new()
        { ResourceId = id, Action = action, Actor = "local-operator", Detail = detail, OccurredAt = DateTimeOffset.UtcNow };

    private static void ValidateStrategy(StrategyRequest request)
    {
        if (request.AutoRestart || !request.IncludeFunding)
            throw Problem(422, "V1_FIXED_CONSTRAINT", "V1 requires autoRestart=false and includeFunding=true.");
        EnsureSafeAccount(request.ExchangeAccountId); _ = GridMath.BuildPlan(request.ToConfiguration(145.25m), SolRules);
    }
    private static void EnsureSafeAccount(string id)
    {
        if (id.Contains("live", StringComparison.OrdinalIgnoreCase) || id.Contains("mainnet", StringComparison.OrdinalIgnoreCase))
            throw Problem(403, "FUNDED_LIVE_FORBIDDEN", "V1 rejects funded live and mainnet accounts.");
    }
    private static void EnsureState(CycleEntity cycle, string required, string command)
    { if (cycle.State != required) throw InvalidState(cycle, command); }
    private static TradingProblemException InvalidState(CycleEntity cycle, string command) =>
        Problem(409, "INVALID_CYCLE_STATE", $"Command {command} is not allowed while cycle state is {cycle.State}.");
    private static TradingProblemException Problem(int status, string code, string message) => new(status, code, message);
    private static CycleState ParseState(string state) => state switch
    {
        "WAITING_FOR_OPERATOR" => CycleState.WaitingForOperator, "STARTING" => CycleState.Starting,
        "RUNNING" => CycleState.Running, "PAUSED" => CycleState.Paused, "CLOSING" => CycleState.Closing,
        _ => CycleState.Fault
    };
}

public sealed class TradingProblemException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
