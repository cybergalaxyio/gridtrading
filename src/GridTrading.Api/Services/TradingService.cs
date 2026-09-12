using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;

namespace GridTrading.Api.Services;

public sealed class TradingService(GridStrategyWorkflow grid)
{
    public static readonly InstrumentRules SolRules = new("SOLUSDT", .001m, .1m, .1m, 5m, 500);

    public static InstrumentRules RulesFor(GridConfiguration config) => new(config.Symbol,
        config.TickSize > 0m ? config.TickSize : SolRules.TickSize,
        config.QuantityStep > 0m ? config.QuantityStep : SolRules.QuantityStep,
        config.MinOrderQuantity > 0m ? config.MinOrderQuantity : SolRules.MinOrderQuantity,
        config.MinOrderNotional > 0m ? config.MinOrderNotional : SolRules.MinOrderNotional,
        config.MaxActiveOrders > 0 ? config.MaxActiveOrders : SolRules.MaxActiveOrders);

    public Task<StrategyEntity> CreateStrategy(StrategyRequest request, CancellationToken ct) =>
        grid.CreateStrategyAsync(request, ct);
    public Task<StrategyEntity?> UpdateStrategy(string id, StrategyRequest request, CancellationToken ct) =>
        grid.UpdateStrategyAsync(id, request, ct);
    public Task<PreviewCacheItem> CreatePreview(PreviewRequest request, CancellationToken ct) =>
        grid.CreatePreviewAsync(request, ct);
    public Task<(OperationEntity, CycleEntity)> StartCycle(
        string strategyId, StartCycleRequest request, string key, CancellationToken ct) =>
        grid.StartCycleAsync(strategyId, request, key, ct);
    public Task<OperationEntity> Command(string cycleId, string command, string reason, string key,
        long? expectedVersion, bool emergencyConfirmed, CancellationToken ct, bool automaticClose = false) =>
        grid.CommandAsync(cycleId, command, reason, key, expectedVersion, emergencyConfirmed, ct, automaticClose);
    public object Snapshot(CycleEntity cycle) => grid.Snapshot(cycle);

    public static StrategyRequest DeserializeStrategy(StrategyEntity entity) =>
        GridStrategyWorkflow.DeserializeStrategy(entity);
}
