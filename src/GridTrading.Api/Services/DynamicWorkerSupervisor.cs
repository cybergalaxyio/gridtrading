namespace GridTrading.Api.Services;

/// <summary>Discovers workers without tying their lifetimes or retries to one another.</summary>
public static class DynamicWorkerSupervisor
{
    public static async Task RunAsync<T>(Func<CancellationToken, Task<IReadOnlyCollection<T>>> discover,
        Func<T, CancellationToken, Task> run, ILogger logger, CancellationToken ct,
        TimeSpan? discoveryInterval = null) where T : notnull
    {
        var workers = new Dictionary<T, (CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var desired = (await discover(ct)).ToHashSet();
                    foreach (var key in workers.Keys.Where(x => !desired.Contains(x)).ToArray())
                    {
                        var worker = workers[key];
                        worker.Stop.Cancel();
                        await worker.Task;
                        worker.Stop.Dispose();
                        workers.Remove(key);
                    }
                    foreach (var key in desired.Where(x => !workers.ContainsKey(x)))
                    {
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        workers.Add(key, (stop, Task.Run(() => RunWorker(key, stop.Token), CancellationToken.None)));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Keep known workers running when discovery temporarily fails.
                    logger.LogWarning("Worker discovery failed ({ErrorType}).", ex.GetType().Name);
                }
                await Task.Delay(discoveryInterval ?? TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) worker.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(x => x.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose();
        }

        async Task RunWorker(T key, CancellationToken stopping)
        {
            var retrySeconds = 1;
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    try { await run(key, stopping); retrySeconds = 1; }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Worker {Worker} failed ({ErrorType}); retrying in {DelaySeconds}s.",
                            key, ex.GetType().Name, retrySeconds);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stopping);
                    retrySeconds = Math.Min(30, retrySeconds * 2);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
    }
}
