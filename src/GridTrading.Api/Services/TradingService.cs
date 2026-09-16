using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;

namespace GridTrading.Api.Services;

public sealed class TradingService(GridStrategyWorkflow grid)
{
    public static readonly InstrumentRules SolRules = GridInstrumentRules.LegacyDefaults;

    public static InstrumentRules RulesFor(GridConfiguration config) =>
        GridInstrumentRules.FromConfiguration(config);

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
        GridConfigurationCodec.ReadStrategy(entity.ConfigurationJson);
}
