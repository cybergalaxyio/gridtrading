using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Hubs;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed class GridStrategyWorkflow(
    TradingDbContext db,
    MarketState market,
    PreviewStore previews,
    ExecutionEnvironmentRegistry environments,
    GridOrderLifecycle lifecycle,
    IHubContext<TradingHub> hub)
{
    public async Task<StrategyEntity> CreateStrategyAsync(StrategyRequest request, CancellationToken ct)
    {
        ValidateStrategy(request);
        var selection = await ResolveDefaultsAsync(request, ct);
        var now = DateTimeOffset.UtcNow;
        var entity = new StrategyEntity
        {
            Id = Ids.NewStrategy(), Name = request.Name.Trim(), StrategyType = "GRID",
            DefaultExecutionEnvironmentId = selection.EnvironmentId,
            DefaultExecutionAccountId = selection.AccountId,
            Symbol = request.Symbol.ToUpperInvariant(),
            ConfigurationJson = JsonSerializer.Serialize(request with
            {
                StrategyType = "GRID",
                DefaultExecutionEnvironmentId = selection.EnvironmentId,
                DefaultExecutionAccountId = selection.AccountId
            }, JsonSupport.Options),
            CreatedAt = now, UpdatedAt = now
        };
        db.Strategies.Add(entity);
        db.AuditLogs.Add(Audit(entity.Id, "STRATEGY_CREATED", "GRID strategy configuration created."));
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<StrategyEntity?> UpdateStrategyAsync(string id, StrategyRequest request, CancellationToken ct)
    {
        ValidateStrategy(request);
        var selection = await ResolveDefaultsAsync(request, ct);
        var entity = await db.Strategies.FindAsync([id], ct);
        if (entity is null || entity.Archived) return null;
        if (!entity.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{entity.StrategyType}' is not supported.");

        entity.Name = request.Name.Trim();
        entity.DefaultExecutionEnvironmentId = selection.EnvironmentId;
        entity.DefaultExecutionAccountId = selection.AccountId;
        entity.Symbol = request.Symbol.ToUpperInvariant();
        entity.ConfigurationJson = JsonSerializer.Serialize(request with
        {
            StrategyType = "GRID",
            DefaultExecutionEnvironmentId = selection.EnvironmentId,
            DefaultExecutionAccountId = selection.AccountId
        }, JsonSupport.Options);
        entity.Version++;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(id, "STRATEGY_UPDATED", $"Strategy version updated to {entity.Version}."));
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<PreviewCacheItem> CreatePreviewAsync(PreviewRequest request, CancellationToken ct)
    {
        GridConfiguration configuration;
        ExecutionSelection selection;
        var version = 0;
        if (!string.IsNullOrWhiteSpace(request.StrategyId))
        {
            var strategy = await db.Strategies.FindAsync([request.StrategyId], ct)
                ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
            EnsureGrid(strategy);
            if (request.StrategyVersion.HasValue && request.StrategyVersion != strategy.Version)
                throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy version has changed.");
            configuration = DeserializeStrategy(strategy).ToConfiguration(request.ConfirmedCenterPrice);
            selection = await environments.ResolveAsync(
                request.ExecutionEnvironmentId ?? strategy.DefaultExecutionEnvironmentId,
                request.ExecutionAccountId ?? strategy.DefaultExecutionAccountId, ct);
            version = strategy.Version;
        }
        else
        {
            var candidate = request.CandidateConfiguration
                ?? throw Problem(422, "CANDIDATE_REQUIRED", "Candidate configuration is required.");
            configuration = new GridConfiguration
            {
                Symbol = candidate.Symbol, GridMode = candidate.GridMode, CenterPrice = request.ConfirmedCenterPrice,
                MaxLevelsPerSide = candidate.MaxLevelsPerSide, WorkingEntriesPerSide = candidate.WorkingEntriesPerSide,
                InitialGapPoints = candidate.InitialGapPoints, GridSpacingPoints = candidate.GridSpacingPoints,
                GridSpacingStepPoints = candidate.GridSpacingStepPoints, TakeProfitPoints = candidate.TakeProfitPoints,
                BaseLotSize = candidate.BaseLotSize, LotSizeIncreasePercent = candidate.LotSizeIncreasePercent,
                MaxTradeLot = candidate.MaxTradeLot, MaxNetLot = candidate.MaxNetLot
            };
            selection = await environments.ResolveAsync(
                request.ExecutionEnvironmentId ?? candidate.ExecutionEnvironmentId,
                request.ExecutionAccountId ?? candidate.ExecutionAccountId ?? candidate.ExchangeAccountId, ct);
        }

        EnsureSafeSelection(selection);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var instrument = await adapter.GetInstrumentAsync(selection, configuration.Symbol, configuration.CenterPrice, ct);
        configuration = configuration with
        {
            TickSize = instrument.TickSize, QuantityStep = instrument.QuantityStep,
            MinOrderQuantity = instrument.MinOrderQuantity, MinOrderNotional = instrument.MinOrderNotional,
            MaxActiveOrders = instrument.MaxActiveOrders, SizeDecimals = instrument.SizeDecimals,
            MakerFeeRate = instrument.MakerFeeRate, TakerFeeRate = instrument.TakerFeeRate
        };
        var item = new PreviewCacheItem(Ids.New("preview"), request.StrategyId, version,
            selection.EnvironmentId, selection.AccountId, DateTimeOffset.UtcNow.AddMinutes(5),
            configuration, GridMath.BuildPlan(configuration, instrument.Rules));
        previews.Items[item.Id] = item;
        return item;
    }

    public async Task<(OperationEntity Operation, CycleEntity Cycle)> StartCycleAsync(
        string strategyId, StartCycleRequest request, string key, CancellationToken ct)
    {
        if (!previews.Items.TryGetValue(request.PreviewId, out var preview) || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw Problem(422, "PREVIEW_EXPIRED", "Create a fresh grid preview before starting.");
        if (preview.StrategyId != strategyId || preview.Plan.CenterPrice != request.ConfirmedCenterPrice)
            throw Problem(422, "PREVIEW_MISMATCH", "Preview does not match this strategy and centre.");

        var strategy = await db.Strategies.FindAsync([strategyId], ct)
            ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        EnsureGrid(strategy);
        if (strategy.Version != preview.StrategyVersion)
            throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy changed after preview.");
        if (await db.Cycles.AnyAsync(x => x.StrategyId == strategyId && !x.IsTerminal, ct))
            throw Problem(409, "ACTIVE_CYCLE_EXISTS", "Only one active cycle is allowed per strategy.");

        var selection = await environments.ResolveAsync(preview.ExecutionEnvironmentId, preview.ExecutionAccountId, ct);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var confirmation = request.OperatorConfirmation;
        var environmentConfirmed = confirmation.EnvironmentConfirmed.Equals(selection.EnvironmentId, StringComparison.OrdinalIgnoreCase) ||
            confirmation.EnvironmentConfirmed.Equals(adapter.Environment.Network, StringComparison.OrdinalIgnoreCase) ||
            confirmation.EnvironmentConfirmed.Equals(adapter.Environment.VenueType, StringComparison.OrdinalIgnoreCase);
        if (!confirmation.ParametersReviewed || !confirmation.CenterConfirmed || !environmentConfirmed)
            throw Problem(422, "OPERATOR_CONFIRMATION_REQUIRED", "The preview execution environment, parameters and centre must be confirmed.");

        var quote = await adapter.PreflightStartAsync(selection, strategy.Symbol, ct);
        var operation = await NewOperationAsync("START_CYCLE", strategyId, key, request, ct);
        if (operation.Status == "COMPLETED")
            return (operation, await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct));

        var now = DateTimeOffset.UtcNow;
        var cycle = new CycleEntity
        {
            Id = Ids.New("cycle"), StrategyId = strategyId,
            ExecutionEnvironmentId = selection.EnvironmentId, ExecutionAccountId = selection.AccountId,
            State = "STARTING", StateVersion = 1, FixedCenterPrice = request.ConfirmedCenterPrice,
            FrozenConfigurationJson = JsonSerializer.Serialize(preview.Configuration, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(preview.Plan, JsonSupport.Options),
            ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync(ct);

        var reservations = new List<ActiveOrderReservation>();
        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            if ((preview.Configuration.GridMode == GridMode.BuyOnly && side == OrderSide.Sell) ||
                (preview.Configuration.GridMode == GridMode.SellOnly && side == OrderSide.Buy)) continue;
            var level = GridMath.SelectWorkingEntryLevel(preview.Plan, side, quote.Mid, []);
            if (level is null) continue;
            var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, 0m, reservations,
                preview.Configuration.MaxNetLot, TradingService.RulesFor(preview.Configuration));
            if (quantity <= 0m) continue;
            db.Orders.Add(GridOrderLifecycle.CreateEntry(cycle, strategy.Symbol, level, quantity));
            reservations.Add(new ActiveOrderReservation(side, quantity));
        }
        await db.SaveChangesAsync(ct);

        try
        {
            await adapter.PlaceOrdersAsync(selection, preview.Configuration,
                db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct);
        }
        catch (Exception ex)
        {
            try { await adapter.CancelOrdersAsync(selection, db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct); }
            catch { }
            var errorCode = ex is TradingProblemException problem ? problem.Code : "EXECUTION_ERROR";
            cycle.State = "FAULT";
            cycle.IsTerminal = true;
            cycle.EndedAt = DateTimeOffset.UtcNow;
            cycle.ExitReason = "START_FAILED";
            operation.Status = "FAILED";
            operation.ErrorCode = errorCode;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            db.RiskAlerts.Add(FaultAlert(cycle, "START_FAILED",
                $"Cycle {cycle.Id} 进入 FAULT：启动阶段初始 Entry 挂单失败。原因 [{errorCode}]：{ex.Message}。" +
                "系统已尽力撤销初始挂单，并将 Cycle 标记为终态。"));
            await db.SaveChangesAsync(ct);
            throw;
        }

        cycle.State = "RUNNING";
        cycle.StateVersion++;
        operation.ResourceId = cycle.Id;
        operation.Status = "COMPLETED";
        operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycle.Id, "CYCLE_STARTED",
            $"GRID cycle started in {selection.EnvironmentId} using account {selection.AccountId}."));
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(cycle, ct);
        return (operation, cycle);
    }

    public async Task<OperationEntity> CommandAsync(string cycleId, string command, string reason, string key,
        long? expectedVersion, bool emergencyConfirmed, CancellationToken ct)
    {
        var cycle = await db.Cycles.FindAsync([cycleId], ct)
            ?? throw Problem(404, "CYCLE_NOT_FOUND", "Cycle was not found.");
        if (expectedVersion.HasValue && expectedVersion != cycle.StateVersion)
            throw Problem(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
        var operation = await NewOperationAsync(command, cycleId, key, new { command, reason }, ct);
        if (operation.Status == "COMPLETED") return operation;

        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        var selection = new ExecutionSelection(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);
        var adapter = environments.Adapter(selection.EnvironmentId);
        switch (command)
        {
            case "PAUSE_ENTRIES":
                EnsureState(cycle, "RUNNING", command);
                await adapter.CancelOrdersAsync(selection, await ActiveOrdersAsync(cycle.Id, "ENTRY", ct), ct);
                cycle.State = "PAUSED";
                break;
            case "RESUME_ENTRIES":
                EnsureState(cycle, "PAUSED", command);
                cycle.State = "RUNNING";
                await db.SaveChangesAsync(ct);
                await lifecycle.MaintainEntryOrdersAsync(cycle, config, null, ct);
                break;
            case "CLOSE":
            case "EMERGENCY_FLATTEN":
                if (command == "EMERGENCY_FLATTEN" && !emergencyConfirmed)
                    throw Problem(422, "EMERGENCY_CONFIRMATION_REQUIRED", "All emergency confirmations are required.");
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                cycle.State = "CLOSING";
                await db.SaveChangesAsync(ct);
                await lifecycle.ReconcileAsync(cycle, ct);
                var residual = await adapter.FlattenAsync(selection, cycle, config, ct);
                if (residual != 0m)
                {
                    cycle.State = "FAULT";
                    cycle.ExitReason = "FLATTEN_RESIDUAL_POSITION";
                    db.RiskAlerts.Add(FaultAlert(cycle, "FLATTEN_RESIDUAL_POSITION",
                        $"Cycle {cycle.Id} 进入 FAULT：平仓未完全成交，策略残余敞口为 {residual}。" +
                        "系统已执行策略挂单撤销；请核对实际仓位后重试 Exit 或紧急平仓。"));
                    await db.SaveChangesAsync(ct);
                    throw Problem(503, "FLATTEN_INCOMPLETE", $"Strategy exposure still has {residual} unfilled; the cycle remains non-terminal.");
                }
                await lifecycle.ReconcileAsync(cycle, ct);
                cycle.State = "WAITING_FOR_OPERATOR";
                cycle.IsTerminal = true;
                cycle.EndedAt = DateTimeOffset.UtcNow;
                cycle.OperatorResetRequired = command == "EMERGENCY_FLATTEN";
                cycle.ExitReason = command == "EMERGENCY_FLATTEN" ? "EMERGENCY_FLATTEN" :
                    string.IsNullOrWhiteSpace(reason) ? "OPERATOR_CLOSE" : reason;
                break;
            case "RECONCILE":
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                await lifecycle.ReconcileAsync(cycle, ct);
                break;
            default:
                throw Problem(404, "COMMAND_NOT_FOUND", "Unknown cycle command.");
        }

        cycle.StateVersion++;
        operation.Status = "COMPLETED";
        operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycleId, command, string.IsNullOrWhiteSpace(reason) ? "Operator command." : reason));
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(cycle, ct);
        return operation;
    }

    public object Snapshot(CycleEntity cycle)
    {
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        var quote = market.Snapshot(config.Symbol);
        var active = db.Orders.Where(x => x.CycleId == cycle.Id &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN")).ToArray();
        var openLots = db.VirtualLots
            .Where(x => x.CycleId == cycle.Id && x.Status != "CLOSED" && x.RemainingQuantity > 0m).ToArray();
        var takeProfits = db.Orders.Where(x => x.CycleId == cycle.Id && x.Kind == "TAKE_PROFIT")
            .ToDictionary(x => x.Id);
        var unprotectedExposureNotionalUsdt =
            GridOrderLifecycle.CalculateUnprotectedNotional(openLots, takeProfits);
        var finalFee = Math.Abs(cycle.ActualNetQuantity) *
            (cycle.ActualNetQuantity >= 0 ? quote.Bid : quote.Ask) * config.TakerFeeRate;
        var pnl = GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, 0m,
            cycle.PaidFees, cycle.AccruedFunding, finalFee, 0m));
        var usage = config.MaxNetLot == 0m ? 0m : Math.Abs(cycle.ActualNetQuantity) / config.MaxNetLot * 100m;
        var color = usage switch { >= 90m => "RED", >= 70m => "ORANGE", >= 40m => "YELLOW", _ => "GREEN" };
        return new
        {
            cycle = new
            {
                cycleId = cycle.Id, cycle.StrategyId, cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId,
                state = cycle.State, cycle.StateVersion, cycle.IsTerminal, cycle.OperatorResetRequired,
                cycle.StartedAt, fixedCenterPrice = cycle.FixedCenterPrice
            },
            market = quote,
            orders = new
            {
                activeEntryCount = active.Count(x => x.Kind == "ENTRY"),
                activeTakeProfitCount = active.Count(x => x.Kind == "TAKE_PROFIT"),
                unknownCount = active.Count(x => x.Status == "UNKNOWN")
            },
            position = new
            {
                actualNetQuantity = cycle.ActualNetQuantity,
                reconstructedNetQuantity = cycle.ReconstructedNetQuantity,
                absoluteMaxNetLotUsagePct = usage,
                netNotionalUsdt = cycle.ActualNetQuantity * quote.Mid
            },
            basketPnl = new
            {
                pnl.RealisedCyclePnl, pnl.UnrealisedAtExecutablePrice, pnl.PaidFees, pnl.AccruedFunding,
                pnl.EstimatedFinalTakerFee, pnl.EstimatedExitSlippage, pnl.LiquidationPnl,
                takeProfitTarget = config.BasketTakeProfitUsdt, stopLossLimit = config.BasketStopLossUsdt
            },
            risk = new
            {
                color, reasons = color == "GREEN" ? Array.Empty<string>() : new[] { "INVENTORY_ELEVATED" },
                usedBuyLevels = db.Orders.Count(x => x.CycleId == cycle.Id && x.Side == "BUY"),
                remainingBuyLevels = config.MaxLevelsPerSide,
                usedSellLevels = db.Orders.Count(x => x.CycleId == cycle.Id && x.Side == "SELL"),
                remainingSellLevels = config.MaxLevelsPerSide,
                unprotectedExposureNotionalUsdt,
                faultExposureThresholdUsdt = config.FaultExposureThresholdUsdt,
                faultExposureThresholdExceeded = unprotectedExposureNotionalUsdt > config.FaultExposureThresholdUsdt
            },
            health = new
            {
                exchange = "HEALTHY", marketData = quote.IsStale ? "STALE" : "FRESH",
                reconciliation = cycle.ActualNetQuantity == cycle.ReconstructedNetQuantity ? "IN_SYNC" : "MISMATCH",
                lastReconciledAt = cycle.LastReconciledAt
            },
            allowedCommands = CycleStateMachine.AllowedCommands(ParseState(cycle.State),
                active.Length > 0 || cycle.ActualNetQuantity != 0m)
        };
    }

    public static StrategyRequest DeserializeStrategy(StrategyEntity entity) =>
        JsonSerializer.Deserialize<StrategyRequest>(entity.ConfigurationJson, JsonSupport.Options)!;

    private async Task<ExecutionSelection> ResolveDefaultsAsync(StrategyRequest request, CancellationToken ct)
    {
        var accountId = request.EffectiveExecutionAccountId;
        var environmentId = request.DefaultExecutionEnvironmentId;
        return await environments.ResolveAsync(environmentId, accountId, ct);
    }

    private async Task<OperationEntity> NewOperationAsync(
        string type, string resourceId, string key, object request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw Problem(400, "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key header is required.");
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonSupport.Options))));
        var existing = await db.Operations.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                throw Problem(409, "IDEMPOTENCY_KEY_CONFLICT", "Idempotency key was used with another request.");
            return existing;
        }
        var operation = new OperationEntity
        {
            Id = Ids.New("op"), CommandId = Ids.New("cmd"), ResourceId = resourceId,
            Type = type, Status = "ACCEPTED", IdempotencyKey = key, RequestHash = hash,
            ErrorCode = "", AcceptedAt = DateTimeOffset.UtcNow
        };
        db.Operations.Add(operation);
        return operation;
    }

    private Task<List<OrderEntity>> ActiveOrdersAsync(string cycleId, string? kind, CancellationToken ct) =>
        db.Orders.Where(x => x.CycleId == cycleId && (kind == null || x.Kind == kind) &&
            (x.Status == "PENDING_EXCHANGE" || x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN"))
            .ToListAsync(ct);

    private async Task BroadcastAsync(CycleEntity cycle, CancellationToken ct) =>
        await hub.Clients.Group($"cycle:{cycle.Id}").SendAsync("CycleStateChanged", new
        {
            eventId = Ids.New("evt"), eventType = "CycleStateChanged", occurredAt = DateTimeOffset.UtcNow,
            aggregateType = "Cycle", aggregateId = cycle.Id, aggregateVersion = cycle.StateVersion,
            sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), correlationId = Ids.New("corr"),
            payload = new { state = cycle.State, cycle.StateVersion }
        }, ct);

    private static void ValidateStrategy(StrategyRequest request)
    {
        if (!request.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{request.StrategyType}' is not supported.");
        if (request.AutoRestart || !request.IncludeFunding)
            throw Problem(422, "V1_FIXED_CONSTRAINT", "V1 requires autoRestart=false and includeFunding=true.");
        _ = GridMath.BuildPlan(request.ToConfiguration(145.25m), TradingService.SolRules);
        if (request.FaultExposureThresholdUsdt < 0m)
            throw Problem(422, "FAULT_THRESHOLD_INVALID", "FAULT exposure threshold must be zero or greater.");
    }

    private static void EnsureGrid(StrategyEntity strategy)
    {
        if (!strategy.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{strategy.StrategyType}' is not supported.");
    }

    private static void EnsureSafeSelection(ExecutionSelection selection)
    {
        if (selection.EnvironmentId.Contains("mainnet", StringComparison.OrdinalIgnoreCase) ||
            selection.AccountId.Contains("live", StringComparison.OrdinalIgnoreCase) ||
            selection.AccountId.Contains("mainnet", StringComparison.OrdinalIgnoreCase))
            throw Problem(403, "FUNDED_LIVE_FORBIDDEN", "V1 rejects funded live and mainnet execution selections.");
    }

    private static AuditEntity Audit(string id, string action, string detail) => new()
        { ResourceId = id, Action = action, Actor = "local-operator", Detail = detail, OccurredAt = DateTimeOffset.UtcNow };
    private static RiskAlertEntity FaultAlert(CycleEntity cycle, string code, string message) => new()
    {
        Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL", Code = code,
        Message = message, CreatedAt = DateTimeOffset.UtcNow
    };
    private static void EnsureState(CycleEntity cycle, string required, string command)
    {
        if (cycle.State != required) throw InvalidState(cycle, command);
    }
    private static TradingProblemException InvalidState(CycleEntity cycle, string command) =>
        Problem(409, "INVALID_CYCLE_STATE", $"Command {command} is not allowed while cycle state is {cycle.State}.");
    private static TradingProblemException Problem(int status, string code, string message) => new(status, code, message);
    private static CycleState ParseState(string state) => state switch
    {
        "WAITING_FOR_OPERATOR" => CycleState.WaitingForOperator,
        "STARTING" => CycleState.Starting,
        "RUNNING" => CycleState.Running,
        "PAUSED" => CycleState.Paused,
        "CLOSING" => CycleState.Closing,
        _ => CycleState.Fault
    };
}
