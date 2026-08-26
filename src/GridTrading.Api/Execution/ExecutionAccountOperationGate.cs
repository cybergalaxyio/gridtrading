using System.Collections.Concurrent;

namespace GridTrading.Api.Execution;

public sealed class ExecutionAccountOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

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
