using System.Collections.Concurrent;

namespace GridTrading.Api.Execution;

public sealed class ExecutionAccountOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, byte> _recoveredCycles = new();

    public bool IsRecovered(string cycleId) => _recoveredCycles.ContainsKey(cycleId);
    public void MarkRecovered(string cycleId) => _recoveredCycles[cycleId] = 0;
    public void RequireRecovery(string cycleId) => _recoveredCycles.TryRemove(cycleId, out _);

    public async Task<T> RunAsync<T>(string accountId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public Task RunAsync(string accountId, Func<Task> action, CancellationToken ct) =>
        RunAsync(accountId, async () => { await action(); return true; }, ct);
}
