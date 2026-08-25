using System.Collections.Concurrent;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> RunAsync<T>(string accountId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public async Task RunAsync(string accountId, Func<Task> action, CancellationToken ct)
    {
        await RunAsync(accountId, async () =>
        {
            await action();
            return true;
        }, ct);
    }
}
