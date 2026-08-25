using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace GridTrading.Api.Hubs;

public sealed class HyperliquidMarketSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connections =
        new(StringComparer.Ordinal);

    public void Subscribe(string connectionId, string symbol)
    {
        var symbols = _connections.GetOrAdd(connectionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        symbols[HyperliquidMarketGroups.Coin(symbol)] = 0;
    }

    public void Unsubscribe(string connectionId, string symbol)
    {
        if (!_connections.TryGetValue(connectionId, out var symbols)) return;
        symbols.TryRemove(HyperliquidMarketGroups.Coin(symbol), out _);
        if (symbols.IsEmpty) _connections.TryRemove(connectionId, out _);
    }

    public void RemoveConnection(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyList<string> ActiveSymbols() => _connections.Values
        .SelectMany(x => x.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public static class HyperliquidMarketGroups
{
    public static string Coin(string symbol)
    {
        var value = symbol.Trim().ToUpperInvariant();
        if (value.EndsWith("USDT") || value.EndsWith("USDC")) value = value[..^4].TrimEnd('-', '_', '/');
        if (string.IsNullOrWhiteSpace(value) || value.Any(x => !char.IsLetterOrDigit(x) && x is not ':' and not '@'))
            throw new HubException("Invalid Hyperliquid symbol.");
        return value;
    }

    public static string Group(string symbol) => $"hyperliquid-market:{Coin(symbol)}";
}
