using GridTrading.Api.Data;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    // All callers hold the account gate. Only complete reconciliations count toward recovery.
    private async Task UpdateRiskPauseAsync(CycleEntity cycle, CancellationToken ct,
        bool confirmedReconciliation = false, string? rejection = null)
    {
        if (cycle.IsTerminal || cycle.State is not ("RUNNING" or "PAUSED")) return;
        var exposure = await AffectedUnprotectedNotionalAsync(cycle.Id, ct);
        var threshold = Math.Max(0m, DeserializeConfig(cycle).FaultExposureThresholdUsdt);
        if (exposure > threshold)
        {
            if (!cycle.RiskPaused)
            {
                cycle.OperatorPaused = cycle.IsOperatorPaused;
                cycle.RiskPaused = true;
                cycle.State = "PAUSED";
                cycle.StateVersion++;
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                    Code = "ENTRY_RISK_PAUSED",
                    Message = $"Cycle {cycle.Id} 进入风险暂停开仓：未保护敞口 {exposure:F2} USD " +
                        $"超过阈值 {threshold:F2} USD。撤销 Entry 未成交余量，继续维护 TP；" +
                        "连续两次对账确认风险解除后自动解除风险暂停。" +
                        (rejection is null ? "" : $"原因：{rejection}。"),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            cycle.RiskRecoveryChecks = 0;
        }
        else if (cycle.RiskPaused)
        {
            // Equality is not recovery, except that a zero threshold permits a flat exposure.
            var recovered = (exposure == 0m || exposure < threshold) &&
                !(await ActiveOrdersAsync(cycle.Id, ct)).Any(x => x.Kind == "ENTRY" && x.Quantity > x.FilledQuantity);
            if (!recovered) cycle.RiskRecoveryChecks = 0;
            else if (confirmedReconciliation) cycle.RiskRecoveryChecks++;
            if (cycle.RiskRecoveryChecks >= 2)
            {
                cycle.RiskPaused = false;
                cycle.RiskRecoveryChecks = 0;
                cycle.State = cycle.OperatorPaused ? "PAUSED" : "RUNNING";
                cycle.StateVersion++;
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "INFO",
                    Code = "ENTRY_RISK_RECOVERED",
                    Message = $"Cycle {cycle.Id} 连续两次对账确认未保护敞口 {exposure:F2} USD " +
                        $"满足恢复条件（阈值 {threshold:F2} USD），已解除风险暂停。" +
                        (cycle.OperatorPaused ? "人工暂停仍保留。" : "恢复维护 Entry。"),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        // Persist the block before external cancellation; a failure is retried on the next sync.
        await db.SaveChangesAsync(ct);
        if (cycle.RiskPaused) await TryCancelPausedEntriesAsync(cycle, ct);
    }

    private async Task TryCancelPausedEntriesAsync(CycleEntity cycle, CancellationToken ct,
        bool propagateFailure = false)
    {
        if (cycle.IsTerminal || cycle.State != "PAUSED") return;
        try { await CancelEntryRemaindersAsync(cycle, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is TradingProblemException or HttpRequestException or TaskCanceledException)
        {
            // Background cancellation failures must not stop TP maintenance for either pause reason.
            var code = cycle.RiskPaused ? "RISK_ENTRY_CANCEL_PENDING" : "OPERATOR_ENTRY_CANCEL_PENDING";
            var reason = cycle.RiskPaused
                ? cycle.IsOperatorPaused ? "风险及人工暂停" : "风险暂停"
                : "人工暂停";
            if (!await db.RiskAlerts.AnyAsync(x => x.CycleId == cycle.Id &&
                x.Code == code && !x.Acknowledged, ct))
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "WARNING",
                    Code = code, CreatedAt = DateTimeOffset.UtcNow,
                    Message = $"{reason}撤销 Entry 尚未完成，将继续重试并维护 TP。原因：{ex.Message}"
                });
            await db.SaveChangesAsync(ct);
            // The initial operator command must still report its failed cancellation.
            if (propagateFailure) throw;
        }
    }

    private async Task CancelEntryRemaindersAsync(CycleEntity cycle, CancellationToken ct)
    {
        var entries = (await ActiveOrdersAsync(cycle.Id, ct))
            .Where(x => x.Kind == "ENTRY" && x.Quantity > x.FilledQuantity).ToArray();
        if (entries.Length == 0) return;
        var selection = Selection(cycle);
        await environments.Adapter(selection.EnvironmentId).CancelOrdersAsync(selection, entries, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task SetOperatorPauseAsync(CycleEntity cycle, bool paused, long? expectedVersion, CancellationToken ct) =>
        accountGate.RunAsync(cycle.ExecutionAccountId, async () =>
        {
            await db.Entry(cycle).ReloadAsync(ct);
            if (expectedVersion.HasValue && cycle.StateVersion != expectedVersion)
                throw new TradingProblemException(412, "STATE_VERSION_STALE", "Cycle state changed; refresh the snapshot.");
            if (cycle.IsTerminal || cycle.State is not ("RUNNING" or "PAUSED") ||
                (!paused && !cycle.IsOperatorPaused))
                throw new TradingProblemException(409, "INVALID_CYCLE_STATE", "This operator pause command is not available.");
            cycle.OperatorPaused = paused;
            cycle.State = paused || cycle.RiskPaused ? "PAUSED" : "RUNNING";
            cycle.StateVersion++;
            await db.SaveChangesAsync(ct);
            if (paused) await TryCancelPausedEntriesAsync(cycle, ct, propagateFailure: true);
            else
            {
                // Never let an operator resume bypass exposure that has not been assessed yet.
                await UpdateRiskPauseAsync(cycle, ct);
                await MaintainEntryOrdersAsync(cycle, DeserializeConfig(cycle), quote: null, ct);
            }
        }, ct);
}
