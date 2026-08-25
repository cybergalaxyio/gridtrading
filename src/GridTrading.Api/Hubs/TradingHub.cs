using Microsoft.AspNetCore.SignalR;

namespace GridTrading.Api.Hubs;

public sealed class TradingHub(HyperliquidMarketSubscriptionRegistry marketSubscriptions) : Hub
{
    public Task SubscribeStrategy(string strategyId) => Groups.AddToGroupAsync(Context.ConnectionId, $"strategy:{strategyId}");
    public Task UnsubscribeStrategy(string strategyId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"strategy:{strategyId}");
    public Task SubscribeCycle(string cycleId) => Groups.AddToGroupAsync(Context.ConnectionId, $"cycle:{cycleId}");
    public Task UnsubscribeCycle(string cycleId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"cycle:{cycleId}");
    public Task SubscribeAccount(string accountId) => Groups.AddToGroupAsync(Context.ConnectionId, $"account:{accountId}");
    public Task SubscribeSymbol(string accountId, string symbol) => Groups.AddToGroupAsync(Context.ConnectionId, $"symbol:{accountId}:{symbol}");

    public async Task SubscribeHyperliquidSymbol(string symbol)
    {
        var coin = HyperliquidMarketGroups.Coin(symbol);
        marketSubscriptions.Subscribe(Context.ConnectionId, coin);
        await Groups.AddToGroupAsync(Context.ConnectionId, HyperliquidMarketGroups.Group(coin));
    }

    public async Task UnsubscribeHyperliquidSymbol(string symbol)
    {
        var coin = HyperliquidMarketGroups.Coin(symbol);
        marketSubscriptions.Unsubscribe(Context.ConnectionId, coin);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, HyperliquidMarketGroups.Group(coin));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        marketSubscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
