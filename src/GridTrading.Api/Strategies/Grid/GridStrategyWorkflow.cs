using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Hubs;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridStrategyWorkflow(
    TradingDbContext db,
    MarketState market,
    PreviewStore previews,
    ExecutionEnvironmentRegistry environments,
    GridOrderLifecycle lifecycle,
    IHubContext<TradingHub> hub, ExecutionAccountOperationGate? startGate = null,
    GridConfigurationService? configurationService = null)
{
    private readonly GridConfigurationService configurations = configurationService ?? new(environments);

    public async Task<StrategyEntity> CreateStrategyAsync(StrategyRequest request, CancellationToken ct)
    {
        GridConfigurationService.ValidateRequest(request);
        var selection = await ResolveDefaultsAsync(request, ct);
        await configurations.ValidateSavedStrategyAsync(request, selection, ct);
        var now = DateTimeOffset.UtcNow;
        var entity = new StrategyEntity
        {
            Id = Ids.NewStrategy(), Name = request.Name.Trim(), StrategyType = "GRID",
            DefaultExecutionEnvironmentId = selection.EnvironmentId,
            DefaultExecutionAccountId = selection.AccountId,
            Symbol = request.Symbol.ToUpperInvariant(),
            ConfigurationJson = GridConfigurationCodec.WriteStrategy(request with
            {
                StrategyType = "GRID",
                DefaultExecutionEnvironmentId = selection.EnvironmentId,
                DefaultExecutionAccountId = selection.AccountId
            }),
            CreatedAt = now, UpdatedAt = now
        };
        db.Strategies.Add(entity);
        db.AuditLogs.Add(Audit(entity.Id, "STRATEGY_CREATED", "GRID strategy configuration created."));
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<StrategyEntity?> UpdateStrategyAsync(string id, StrategyRequest request, CancellationToken ct)
    {
        GridConfigurationService.ValidateRequest(request);
        var selection = await ResolveDefaultsAsync(request, ct);
        var entity = await db.Strategies.FindAsync([id], ct);
        if (entity is null || entity.Archived) return null;
        if (!entity.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{entity.StrategyType}' is not supported.");

        await configurations.ValidateSavedStrategyAsync(request, selection, ct);

        entity.Name = request.Name.Trim();
        entity.DefaultExecutionEnvironmentId = selection.EnvironmentId;
        entity.DefaultExecutionAccountId = selection.AccountId;
        entity.Symbol = request.Symbol.ToUpperInvariant();
        entity.ConfigurationJson = GridConfigurationCodec.WriteStrategy(request with
        {
            StrategyType = "GRID",
            DefaultExecutionEnvironmentId = selection.EnvironmentId,
            DefaultExecutionAccountId = selection.AccountId
        });
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
            configuration = GridConfigurationMapper.FromStrategy(
                GridConfigurationCodec.ReadStrategy(strategy.ConfigurationJson), request.ConfirmedCenterPrice);
            selection = await environments.ResolveAsync(
                request.ExecutionEnvironmentId ?? strategy.DefaultExecutionEnvironmentId,
                request.ExecutionAccountId ?? strategy.DefaultExecutionAccountId, ct);
            version = strategy.Version;
        }
        else
        {
            var candidate = request.CandidateConfiguration
                ?? throw Problem(422, "CANDIDATE_REQUIRED", "Candidate configuration is required.");
            configuration = GridConfigurationMapper.FromCandidate(candidate, request.ConfirmedCenterPrice);
            selection = await environments.ResolveAsync(
                request.ExecutionEnvironmentId ?? candidate.ExecutionEnvironmentId,
                request.ExecutionAccountId ?? candidate.ExecutionAccountId ?? candidate.ExchangeAccountId, ct);
        }

        EnsureSafeSelection(selection);
        var prepared = await configurations.PreparePreviewAsync(configuration, selection, ct);
        var item = new PreviewCacheItem(Ids.New("preview"), request.StrategyId, version,
            selection.EnvironmentId, selection.AccountId, DateTimeOffset.UtcNow.AddMinutes(5),
            prepared.Configuration, prepared.Plan);
        previews.Items[item.Id] = item;
        return item;
    }

    public async Task<(OperationEntity Operation, CycleEntity Cycle)> StartCycleAsync(
        string strategyId, StartCycleRequest request, string key, CancellationToken ct)
    {
        if (!previews.Items.TryGetValue(request.PreviewId, out var preview))
            throw Problem(422, "PREVIEW_EXPIRED", "Create a fresh preview before starting.");
        if (startGate is null) return await StartCoreAsync(strategyId, request, key, ct);
        (OperationEntity Operation, CycleEntity Cycle) result = default;
        await startGate.RunAsync(preview.ExecutionAccountId, async () =>
            { result = await StartCoreAsync(strategyId, request, key, ct); }, ct);
        return result;
    }

    private async Task<(OperationEntity Operation, CycleEntity Cycle)> StartCoreAsync(
        string strategyId, StartCycleRequest request, string key, CancellationToken ct)
    {
        if (!previews.Items.TryGetValue(request.PreviewId, out var preview) || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw Problem(422, "PREVIEW_EXPIRED", "Create a fresh grid preview before starting.");
        if (preview.StrategyId != strategyId ||
            (preview.Configuration.CenterSuggestionMode != "CURRENT_MID" && preview.Plan.CenterPrice != request.ConfirmedCenterPrice))
            throw Problem(422, "PREVIEW_MISMATCH", "Preview does not match this strategy and centre.");

        var strategy = await db.Strategies.FindAsync([strategyId], ct)
            ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        EnsureGrid(strategy);
        if (strategy.Version != preview.StrategyVersion)
            throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy changed after preview.");
        var selection = await environments.ResolveAsync(preview.ExecutionEnvironmentId, preview.ExecutionAccountId, ct);
        var adapter = environments.Adapter(selection.EnvironmentId);
        await EnsureMarketAvailableAsync(strategyId, selection, preview.Configuration.Symbol, ct);
        var confirmation = request.OperatorConfirmation;
        var environmentConfirmed = confirmation.EnvironmentConfirmed.Equals(selection.EnvironmentId, StringComparison.OrdinalIgnoreCase) ||
            confirmation.EnvironmentConfirmed.Equals(adapter.Environment.Network, StringComparison.OrdinalIgnoreCase) ||
            confirmation.EnvironmentConfirmed.Equals(adapter.Environment.VenueType, StringComparison.OrdinalIgnoreCase);
        if (!confirmation.ParametersReviewed || !confirmation.CenterConfirmed || !environmentConfirmed)
            throw Problem(422, "OPERATOR_CONFIRMATION_REQUIRED", "The preview execution environment, parameters and centre must be confirmed.");

        var quote = await adapter.PreflightStartAsync(selection, preview.Configuration.Symbol, ct);
        var prepared = await configurations.PrepareStartupAsync(new(preview.Configuration, preview.Plan), selection, quote, ct);
        preview = preview with { Configuration = prepared.Configuration, Plan = prepared.Plan };
        OperationEntity operation;
        CycleEntity cycle;
        // SQLite's serializable transaction acquires the write reservation before the final
        // check. Separate processes therefore cannot both reserve this market. Keep venue
        // requests outside the transaction and persist STARTING before placing any orders.
        await using (var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct))
        {
            await EnsureMarketAvailableAsync(strategyId, selection, preview.Configuration.Symbol, ct);
            operation = await NewOperationAsync("START_CYCLE", strategyId, key, request, ct);
            if (operation.Status == "COMPLETED")
                return (operation, await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct));
            var now = DateTimeOffset.UtcNow;
            cycle = new CycleEntity
            {
                Id = Ids.New("cycle"), StrategyId = strategyId,
                ExecutionEnvironmentId = selection.EnvironmentId, ExecutionAccountId = selection.AccountId,
                State = "STARTING", StateVersion = 1, FixedCenterPrice = preview.Plan.CenterPrice,
                FrozenConfigurationJson = GridConfigurationCodec.WriteFrozen(preview.Configuration),
                FrozenPlanJson = JsonSerializer.Serialize(preview.Plan, JsonSupport.Options),
                ExitReason = "", StartedAt = now, LastReconciledAt = now
            };
            db.Cycles.Add(cycle);
            operation.ResourceId = cycle.Id;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var reservations = new List<ActiveOrderReservation>();
        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            if ((preview.Configuration.GridMode == GridMode.BuyOnly && side == OrderSide.Sell) ||
                (preview.Configuration.GridMode == GridMode.SellOnly && side == OrderSide.Buy)) continue;
            if (!await lifecycle.CanPlaceNewEntryOrderAsync(cycle, preview.Configuration, side, ct)) continue;
            var level = GridMath.SelectWorkingEntryLevel(preview.Plan, side, quote.Mid, []);
            if (level is null) continue;
            var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, 0m, reservations,
                preview.Configuration.MaxNetLot, GridInstrumentRules.FromConfiguration(preview.Configuration));
            if (quantity <= 0m) continue;
            db.Orders.Add(GridOrderLifecycle.CreateEntry(cycle, preview.Configuration.Symbol, level, quantity));
            reservations.Add(new ActiveOrderReservation(side, quantity));
        }
        await db.SaveChangesAsync(ct);

        try
        {
            await lifecycle.PlaceNewEntryOrdersAsync(cycle, preview.Configuration,
                db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct);
        }
        catch (Exception ex)
        {
            try { await adapter.CancelOrdersAsync(selection, db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct); }
            catch { }
            var errorCode = ex is TradingProblemException problem ? problem.Code : "EXECUTION_ERROR";
            cycle.State = "FAULT";
            cycle.IsTerminal = selection.EnvironmentId != ExecutionEnvironmentIds.HyperliquidMainnet;
            cycle.EndedAt = cycle.IsTerminal ? DateTimeOffset.UtcNow : null;
            cycle.ExitReason = "START_FAILED";
            operation.Status = "FAILED";
            operation.ErrorCode = errorCode;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            db.RiskAlerts.Add(FaultAlert(cycle, "START_FAILED",
                $"Cycle {cycle.Id} 进入 FAULT：启动阶段初始 Entry 挂单失败。原因 [{errorCode}]：{ex.Message}。" +
                "系统已尽力撤销初始挂单；Mainnet Cycle 保持可对账及可关闭状态，请检查实际仓位。"));
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

    private async Task EnsureMarketAvailableAsync(string strategyId, ExecutionSelection selection, string symbol, CancellationToken ct)
    {
        if (await db.Cycles.AnyAsync(x => x.StrategyId == strategyId && !x.IsTerminal, ct))
            throw Problem(409, "ACTIVE_CYCLE_EXISTS", "Only one active cycle is allowed per strategy.");

        var configurations = await db.Cycles.AsNoTracking()
            .Where(x => x.ExecutionEnvironmentId == selection.EnvironmentId &&
                x.ExecutionAccountId == selection.AccountId && !x.IsTerminal)
            .Select(x => x.FrozenConfigurationJson).ToListAsync(ct);
        var coin = HyperliquidTradingClient.ToCoin(symbol);
        if (configurations.Any(json => HyperliquidTradingClient.ToCoin(GridConfigurationCodec.ReadFrozen(json).Symbol) == coin))
            throw Problem(409, selection.EnvironmentId == ExecutionEnvironmentIds.HyperliquidMainnet
                ? "MAINNET_SYMBOL_BUSY" : "ACCOUNT_SYMBOL_BUSY",
                $"Only one active cycle is allowed for {coin} on account {selection.AccountId} ({selection.EnvironmentId}).");
    }

    public async Task<OperationEntity> CommandAsync(string cycleId, string command, string reason, string key,
        long? expectedVersion, bool emergencyConfirmed, CancellationToken ct, bool automaticClose = false)
    {
        var cycle = await db.Cycles.FindAsync([cycleId], ct)
            ?? throw Problem(404, "CYCLE_NOT_FOUND", "Cycle was not found.");
        if (expectedVersion.HasValue && expectedVersion != cycle.StateVersion)
            throw Problem(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
        var operation = await NewOperationAsync(command, cycleId, key, new { command, reason }, ct);
        if (operation.Status == "COMPLETED") return operation;

        var config = GridConfigurationCodec.ReadFrozen(cycle.FrozenConfigurationJson);
        var selection = new ExecutionSelection(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var restartAfterClose = automaticClose && command == "CLOSE" && config.AutoRestart &&
            IsBasketClose(reason) && !cycle.IsOperatorPaused && !cycle.OperatorResetRequired && cycle.State != "FAULT";
        switch (command)
        {
            case "PAUSE_ENTRIES":
            case "RESUME_ENTRIES":
                await lifecycle.SetOperatorPauseAsync(cycle, command == "PAUSE_ENTRIES", expectedVersion, ct);
                break;
            case "CLOSE":
            case "EMERGENCY_FLATTEN":
                if (command == "EMERGENCY_FLATTEN" && !emergencyConfirmed)
                    throw Problem(422, "EMERGENCY_CONFIRMATION_REQUIRED", "All emergency confirmations are required.");
                await lifecycle.BeginClosingAsync(cycle, expectedVersion, ct);
                restartAfterClose = restartAfterClose && !cycle.OperatorPaused && !cycle.OperatorResetRequired;
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
                if (selection.EnvironmentId == ExecutionEnvironmentIds.HyperliquidMainnet && cycle.ActualNetQuantity != 0m)
                    throw Problem(503, "FLATTEN_INCOMPLETE", "Mainnet position is not flat after reconciliation; close remains pending.");
                cycle.State = "WAITING_FOR_OPERATOR";
                cycle.OperatorPaused = false;
                cycle.RiskPaused = false;
                cycle.RiskRecoveryChecks = 0;
                cycle.IsTerminal = true;
                cycle.EndedAt = DateTimeOffset.UtcNow;
                cycle.OperatorResetRequired = command == "EMERGENCY_FLATTEN";
                cycle.ExitReason = command == "EMERGENCY_FLATTEN" ? "EMERGENCY_FLATTEN" :
                    string.IsNullOrWhiteSpace(reason) ? "OPERATOR_CLOSE" : reason;
                if (restartAfterClose)
                    await NewOperationAsync("AUTO_RESTART", cycle.Id, AutoRestartKey(cycle.Id), new { cycleId = cycle.Id }, ct);
                break;
            case "RECONCILE":
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                await lifecycle.ReconcileAsync(cycle, ct);
                break;
            default:
                throw Problem(404, "COMMAND_NOT_FOUND", "Unknown cycle command.");
        }

        if (command is not ("PAUSE_ENTRIES" or "RESUME_ENTRIES")) cycle.StateVersion++;
        operation.Status = "COMPLETED";
        operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycleId, command, string.IsNullOrWhiteSpace(reason) ? "Operator command." : reason));
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(cycle, ct);
        return operation;
    }

    public async Task<object> SnapshotAsync(CycleEntity cycle, CancellationToken ct)
    {
        var config = GridConfigurationCodec.ReadFrozen(cycle.FrozenConfigurationJson);
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
                cycleId = cycle.Id, cycle.StrategyId, symbol = config.Symbol, cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId,
                state = cycle.State, cycle.StateVersion, cycle.IsTerminal, cycle.OperatorResetRequired,
                cycle.RiskPaused, operatorPaused = cycle.IsOperatorPaused, cycle.EntryPauseReasons, cycle.RiskRecoveryChecks,
                cycle.StartedAt, fixedCenterPrice = cycle.FixedCenterPrice,
                cycle.EntryGridPriceOffset, effectivePlan = cycle.EffectivePlan
            },
            entryHolds = await lifecycle.ReadEntryHoldsAsync(cycle, config, ct),
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
            allowedCommands = cycle.State == "PAUSED"
                ? new[] { cycle.IsOperatorPaused ? "RESUME_ENTRIES" : "PAUSE_ENTRIES", "CLOSE", "EMERGENCY_FLATTEN", "RECONCILE" }
                : CycleStateMachine.AllowedCommands(ParseState(cycle.State), active.Length > 0 || cycle.ActualNetQuantity != 0m)
        };
    }

    public static StrategyRequest DeserializeStrategy(StrategyEntity entity) =>
        GridConfigurationCodec.ReadStrategy(entity.ConfigurationJson);

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

    private static void EnsureGrid(StrategyEntity strategy)
    {
        if (!strategy.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{strategy.StrategyType}' is not supported.");
    }

    private static void EnsureSafeSelection(ExecutionSelection selection)
    {
        if (selection.EnvironmentId is not (ExecutionEnvironmentIds.PaperLocal or ExecutionEnvironmentIds.HyperliquidTestnet or ExecutionEnvironmentIds.HyperliquidMainnet))
            throw Problem(403, "EXECUTION_ENVIRONMENT_UNSUPPORTED", "Unsupported execution environment.");
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
