using System.Collections.Concurrent;
using GridTrading.Api.Contracts;

namespace GridTrading.Api.Services;

public sealed class PreviewStore
{
    public ConcurrentDictionary<string, PreviewCacheItem> Items { get; } = new();
}

public sealed class TradingProblemException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
