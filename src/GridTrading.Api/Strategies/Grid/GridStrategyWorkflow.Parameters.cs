using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridStrategyWorkflow
{
    public async Task<OperationEntity> UpdateParametersAsync(string cycleId, UpdateCycleParametersRequest request,
        string key, long? expectedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw Problem(400, "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key header is required.");
        if (!expectedVersion.HasValue)
            throw Problem(428, "STATE_VERSION_REQUIRED", "If-Match with the current cycle version is required.");
        var accountId = await db.Cycles.Where(x => x.Id == cycleId).Select(x => x.ExecutionAccountId)
            .SingleOrDefaultAsync(ct) ?? throw Problem(404, "CYCLE_NOT_FOUND", "Cycle was not found.");
        var operation = await lifecycle.RunAccountOperationAsync(accountId, async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var payload = new { cycleId, command = "UPDATE_PARAMETERS", parameters = request };
            // Resolve retries before version/state checks: the original commit advanced the version.
            if (await db.Operations.AnyAsync(x => x.IdempotencyKey == key, ct))
                return await NewOperationAsync("UPDATE_PARAMETERS", cycleId, key, payload, ct);
            var cycle = await db.Cycles.SingleAsync(x => x.Id == cycleId, ct);
            await db.Entry(cycle).ReloadAsync(ct);
            if (cycle.StateVersion != expectedVersion)
                throw Problem(412, "STATE_VERSION_STALE", "Cycle changed; refresh its settings before saving again.");
            if (cycle.IsTerminal || cycle.State is not ("RUNNING" or "PAUSED"))
                throw InvalidState(cycle, "UPDATE_PARAMETERS");
            if (request.TakeProfitPoints is null && request.MaxLevelsPerSide is null && request.BaseLotSize is null)
                throw Problem(422, "PARAMETERS_REQUIRED", "Provide TP, maximum levels, or base lot size.");
            var before = cycle.EffectiveConfiguration;
            var after = before with
            {
                TakeProfitPoints = request.TakeProfitPoints ?? before.TakeProfitPoints,
                MaxLevelsPerSide = request.MaxLevelsPerSide ?? before.MaxLevelsPerSide,
                BaseLotSize = request.BaseLotSize ?? before.BaseLotSize
            };
            if (after.MaxLevelsPerSide < before.MaxLevelsPerSide)
                throw Problem(422, "LEVEL_REDUCTION_NOT_ALLOWED", "Current cycle grid levels can only increase.");
            GridPlan plan;
            try
            {
                plan = GridMath.UpdatePlan(after, GridInstrumentRules.FromConfiguration(after), cycle.EffectivePlan);
            }
            catch (GridValidationException ex) { throw Problem(422, ex.Code, ex.Message); }
            catch (OverflowException) { throw Problem(422, "GRID_VALUE_OVERFLOW", "The proposed grid quantities or prices are too large."); }

            var result = await NewOperationAsync("UPDATE_PARAMETERS", cycleId, key, payload, ct);
            cycle.LiveConfigurationJson = GridConfigurationCodec.WriteFrozen(after);
            // The saved plan is unshifted; EffectivePlan applies the current movement offset once.
            cycle.LivePlanJson = JsonSerializer.Serialize(
                SingleModeEntryRules.ShiftPlan(plan, -cycle.EntryGridPriceOffset), JsonSupport.Options);
            cycle.StateVersion++;
            result.Status = "COMPLETED";
            result.CompletedAt = DateTimeOffset.UtcNow;
            db.AuditLogs.Add(Audit(cycleId, "CYCLE_PARAMETERS_UPDATED", JsonSerializer.Serialize(new
            {
                before = new { before.TakeProfitPoints, before.MaxLevelsPerSide, before.BaseLotSize },
                after = new { after.TakeProfitPoints, after.MaxLevelsPerSide, after.BaseLotSize },
                appliesTo = "New entry orders in this cycle only"
            }, JsonSupport.Options)));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, ct);
        await BroadcastAsync(await db.Cycles.SingleAsync(x => x.Id == cycleId, ct), ct);
        return operation;
    }
}
