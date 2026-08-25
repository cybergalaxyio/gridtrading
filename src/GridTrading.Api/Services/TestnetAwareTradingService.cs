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

public sealed class TradingService(TradingDbContext db, PreviewStore previews, HyperliquidCycleCoordinator testnet,
    LegacyTradingService paper, IHubContext<TradingHub> hub)
{
    public static readonly InstrumentRules SolRules = new("SOLUSDT", .001m, .1m, .1m, 5m, 500);

    public static InstrumentRules RulesFor(GridConfiguration config) => new(config.Symbol,
        config.TickSize > 0m ? config.TickSize : SolRules.TickSize,
        config.QuantityStep > 0m ? config.QuantityStep : SolRules.QuantityStep,
        config.MinOrderQuantity > 0m ? config.MinOrderQuantity : SolRules.MinOrderQuantity,
        config.MinOrderNotional > 0m ? config.MinOrderNotional : SolRules.MinOrderNotional,
        config.MaxActiveOrders > 0 ? config.MaxActiveOrders : SolRules.MaxActiveOrders);

    public Task<StrategyEntity> CreateStrategy(StrategyRequest request, CancellationToken ct) => paper.CreateStrategy(request, ct);
    public Task<StrategyEntity?> UpdateStrategy(string id, StrategyRequest request, CancellationToken ct) => paper.UpdateStrategy(id, request, ct);
    public Task<PreviewCacheItem> CreatePreview(PreviewRequest request, CancellationToken ct) => paper.CreatePreview(request, ct);
    public object Snapshot(CycleEntity cycle) => paper.Snapshot(cycle);
    public static StrategyRequest DeserializeStrategy(StrategyEntity entity) =>
        JsonSerializer.Deserialize<StrategyRequest>(entity.ConfigurationJson, JsonSupport.Options)!;

    public async Task<(OperationEntity, CycleEntity)> StartCycle(string strategyId, StartCycleRequest request, string key, CancellationToken ct)
    {
        var strategy = await db.Strategies.FindAsync([strategyId], ct)
            ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        if (!await testnet.IsTestnetAsync(strategy.ExchangeAccountId, ct))
            return await paper.StartCycle(strategyId, request, key, ct);

        var confirmation = request.OperatorConfirmation;
        if (!confirmation.ParametersReviewed || !confirmation.CenterConfirmed || confirmation.EnvironmentConfirmed != "TESTNET")
            throw Problem(422, "OPERATOR_CONFIRMATION_REQUIRED", "The operator must explicitly confirm the TESTNET environment, parameters and centre.");
        if (!previews.Items.TryGetValue(request.PreviewId, out var preview) || preview.ExpiresAt <= DateTimeOffset.UtcNow)
            throw Problem(422, "PREVIEW_EXPIRED", "Create a fresh grid preview before starting.");
        if (preview.StrategyId != strategyId || preview.Plan.CenterPrice != request.ConfirmedCenterPrice)
            throw Problem(422, "PREVIEW_MISMATCH", "Preview does not match this strategy and centre.");
        if (strategy.Version != preview.StrategyVersion)
            throw Problem(412, "STRATEGY_VERSION_STALE", "Strategy changed after preview.");
        if (await db.Cycles.AnyAsync(x => x.StrategyId == strategyId && !x.IsTerminal, ct))
            throw Problem(409, "ACTIVE_CYCLE_EXISTS", "Only one active cycle is allowed per strategy.");

        var book = await testnet.PreflightStartAsync(strategy.ExchangeAccountId, strategy.Symbol, ct);
        var operation = await NewOperation("START_CYCLE", strategyId, key, request, ct);
        if (operation.Status == "COMPLETED")
            return (operation, await db.Cycles.SingleAsync(x => x.Id == operation.ResourceId, ct));
        var now = DateTimeOffset.UtcNow;
        var cycle = new CycleEntity
        {
            Id = Ids.New("cycle"), StrategyId = strategyId, State = "STARTING", StateVersion = 1,
            FixedCenterPrice = request.ConfirmedCenterPrice,
            FrozenConfigurationJson = JsonSerializer.Serialize(preview.Configuration, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(preview.Plan, JsonSupport.Options), ExitReason = "",
            StartedAt = now, LastReconciledAt = now
        };
        db.Cycles.Add(cycle); await db.SaveChangesAsync(ct);
        var reservations = new List<ActiveOrderReservation>();
        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            var level = GridMath.SelectWorkingEntryLevel(preview.Plan, side, book.Mid, []);
            if (level is null) continue;
            var allowed = GridMath.AllowedOrderQuantity(level.Side, level.PlannedQuantity, 0m, reservations,
                preview.Configuration.MaxNetLot, RulesFor(preview.Configuration));
            if (allowed <= 0m) continue;
            db.Orders.Add(CreateOrder(cycle, strategy.Symbol, level, allowed));
            reservations.Add(new ActiveOrderReservation(level.Side, allowed));
        }
        await db.SaveChangesAsync(ct);
        try
        {
            await testnet.PlacePendingOrdersAsync(strategy.ExchangeAccountId, preview.Configuration,
                db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct);
        }
        catch (Exception ex)
        {
            try { await testnet.CancelAsync(strategy.ExchangeAccountId, db.Orders.Local.Where(x => x.CycleId == cycle.Id), ct); }
            catch { /* Preserve the original rejection; reconciliation will reveal any surviving order. */ }
            cycle.State = "FAULT"; cycle.IsTerminal = true; cycle.EndedAt = DateTimeOffset.UtcNow;
            cycle.ExitReason = "START_FAILED"; operation.Status = "FAILED"; operation.ErrorCode = ex is TradingProblemException p ? p.Code : "EXCHANGE_ERROR";
            operation.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); throw;
        }
        cycle.State = "RUNNING"; cycle.StateVersion++;
        operation.ResourceId = cycle.Id; operation.Status = "COMPLETED"; operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycle.Id, "CYCLE_STARTED", "Hyperliquid Testnet preflight passed and initial orders were accepted by the exchange."));
        await db.SaveChangesAsync(ct); await Broadcast(cycle, ct);
        return (operation, cycle);
    }

    public async Task<OperationEntity> Command(string cycleId, string command, string reason, string key,
        long? expectedVersion, bool emergencyConfirmed, CancellationToken ct)
    {
        var cycle = await db.Cycles.FindAsync([cycleId], ct) ?? throw Problem(404, "CYCLE_NOT_FOUND", "Cycle was not found.");
        var strategy = await db.Strategies.FindAsync([cycle.StrategyId], ct) ?? throw Problem(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        if (!await testnet.IsTestnetAsync(strategy.ExchangeAccountId, ct))
            return await paper.Command(cycleId, command, reason, key, expectedVersion, emergencyConfirmed, ct);
        if (expectedVersion.HasValue && expectedVersion != cycle.StateVersion)
            throw Problem(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
        var operation = await NewOperation(command, cycleId, key, new { command, reason }, ct);
        if (operation.Status == "COMPLETED") return operation;
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        switch (command)
        {
            case "PAUSE_ENTRIES":
                EnsureState(cycle, "RUNNING", command);
                await testnet.CancelAsync(strategy.ExchangeAccountId,
                    await ActiveOrders(cycle.Id, "ENTRY", ct), ct);
                cycle.State = "PAUSED";
                break;
            case "RESUME_ENTRIES":
                EnsureState(cycle, "PAUSED", command);
                cycle.State = "RUNNING";
                await db.SaveChangesAsync(ct);
                await testnet.MaintainEntryOrdersAsync(strategy, cycle, config, book: null, ct);
                break;
            case "CLOSE":
            case "EMERGENCY_FLATTEN":
                if (command == "EMERGENCY_FLATTEN" && !emergencyConfirmed)
                    throw Problem(422, "EMERGENCY_CONFIRMATION_REQUIRED", "All emergency confirmations are required.");
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                cycle.State = "CLOSING"; await db.SaveChangesAsync(ct);
                var residual = await testnet.FlattenAsync(strategy.ExchangeAccountId, cycle, config, ct);
                if (residual != 0m)
                {
                    cycle.State = "FAULT"; cycle.ExitReason = "FLATTEN_RESIDUAL_POSITION";
                    await db.SaveChangesAsync(ct);
                    throw Problem(503, "FLATTEN_INCOMPLETE", $"Actual Testnet position is still {residual}; the cycle remains non-terminal.");
                }
                await testnet.ReconcileAsync(strategy, cycle, ct);
                cycle.State = "WAITING_FOR_OPERATOR"; cycle.IsTerminal = true;
                cycle.EndedAt = DateTimeOffset.UtcNow; cycle.OperatorResetRequired = command == "EMERGENCY_FLATTEN";
                cycle.ExitReason = command == "EMERGENCY_FLATTEN" ? "EMERGENCY_FLATTEN" : string.IsNullOrWhiteSpace(reason) ? "OPERATOR_CLOSE" : reason;
                break;
            case "RECONCILE":
                if (cycle.IsTerminal) throw InvalidState(cycle, command);
                await testnet.ReconcileAsync(strategy, cycle, ct);
                break;
            default: throw Problem(404, "COMMAND_NOT_FOUND", "Unknown cycle command.");
        }
        cycle.StateVersion++; operation.Status = "COMPLETED"; operation.CompletedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(Audit(cycleId, command, string.IsNullOrWhiteSpace(reason) ? "Operator command." : reason));
        await db.SaveChangesAsync(ct); await Broadcast(cycle, ct); return operation;
    }

    private async Task<List<OrderEntity>> ActiveOrders(string cycleId, string? kind, CancellationToken ct) =>
        await db.Orders.Where(x => x.CycleId == cycleId && (kind == null || x.Kind == kind) &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN" || x.Status == "PENDING_EXCHANGE")).ToListAsync(ct);

    private static OrderEntity CreateOrder(CycleEntity cycle, string symbol, GridLevel level, decimal quantity) => new()
    {
        Id = Ids.New("order"), CycleId = cycle.Id,
        ClientOrderId = Ids.New($"grid-{level.Side.ToString()[0]}-{level.LevelIndex}"), ExchangeOrderId = "pending",
        Symbol = symbol, Side = level.Side.ToString().ToUpperInvariant(), Kind = "ENTRY", Status = "PENDING_EXCHANGE",
        GridLevel = level.LevelIndex, Price = level.EntryPrice, Quantity = quantity,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

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
        var operation = new OperationEntity
        {
            Id = Ids.New("op"), CommandId = Ids.New("cmd"), ResourceId = resourceId, Type = type, Status = "ACCEPTED",
            IdempotencyKey = key, RequestHash = hash, ErrorCode = "", AcceptedAt = DateTimeOffset.UtcNow
        };
        db.Operations.Add(operation); return operation;
    }

    private async Task Broadcast(CycleEntity cycle, CancellationToken ct) => await hub.Clients.Group($"cycle:{cycle.Id}")
        .SendAsync("CycleStateChanged", new { eventId = Ids.New("evt"), eventType = "CycleStateChanged", occurredAt = DateTimeOffset.UtcNow,
            aggregateType = "Cycle", aggregateId = cycle.Id, aggregateVersion = cycle.StateVersion,
            sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), correlationId = Ids.New("corr"),
            payload = new { state = cycle.State, cycle.StateVersion } }, ct);

    private static AuditEntity Audit(string id, string action, string detail) => new()
        { ResourceId = id, Action = action, Actor = "local-operator", Detail = detail, OccurredAt = DateTimeOffset.UtcNow };
    private static void EnsureState(CycleEntity cycle, string state, string command)
    { if (cycle.State != state) throw InvalidState(cycle, command); }
    private static TradingProblemException InvalidState(CycleEntity cycle, string command) =>
        Problem(409, "INVALID_CYCLE_STATE", $"Command {command} is not allowed while cycle state is {cycle.State}.");
    private static TradingProblemException Problem(int status, string code, string message) => new(status, code, message);
}
