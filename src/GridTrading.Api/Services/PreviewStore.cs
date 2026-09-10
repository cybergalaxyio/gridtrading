using System.Collections.Concurrent;
using GridTrading.Api.Contracts;

namespace GridTrading.Api.Services;

public sealed class PreviewStore
{
    public ConcurrentDictionary<string, PreviewCacheItem> Items { get; } = new();
}
