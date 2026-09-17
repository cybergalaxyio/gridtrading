using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;
using GridTrading.Domain.Advisory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GridTrading.Api.Services;

public sealed record GridAdvisoryResponse(string EnvironmentId, string Symbol, string? AccountId, string? StrategyId,
    int? StrategyVersion, string? StrategyName, GridMode? GridMode, bool StrategyMatchesSymbol,
    string RuleVersion, DateTimeOffset AsOf, DateTimeOffset? QuoteAsOf, IReadOnlyList<AdvisoryFrame> Frames,
    string Status, IReadOnlyList<AdvisoryCheck> Checks, IReadOnlyList<AdvisoryScenario> Scenarios, string? Notice);

public sealed class GridAdvisoryService(TradingDbContext db, ExecutionEnvironmentRegistry environments,
    HyperliquidMarketDataClient market, HyperliquidInfoClient info, IMemoryCache cache, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly (string Interval, int Seconds)[] Intervals = [("15m", 900), ("1h", 3600), ("4h", 14400), ("1d", 86400)];

    public async Task<GridAdvisoryResponse> GetAsync(string environmentId, string symbol, string? accountId, string? strategyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new TradingProblemException(422, "SYMBOL_REQUIRED", "Select a symbol for analysis.");
        symbol = HyperliquidTradingClient.ToCoin(symbol.Trim());
        var adapter = environments.Adapter(environmentId);
        var network = adapter.Environment.Network;
        StrategyEntity? strategy = null;
        if (!string.IsNullOrWhiteSpace(strategyId))
            strategy = await db.Strategies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == strategyId, ct)
                ?? throw new TradingProblemException(404, "STRATEGY_NOT_FOUND", "Strategy was not found.");
        var matches = strategy is not null && HyperliquidTradingClient.ToCoin(strategy.Symbol).Equals(symbol, StringComparison.OrdinalIgnoreCase);
        var request = strategy is null ? null : JsonSerializer.Deserialize<StrategyRequest>(strategy.ConfigurationJson, JsonSupport.Options);
        GridMode? mode = matches ? request?.GridMode : null;
        string? notice = strategy is not null && !matches ? $"Selected symbol {symbol} differs from saved strategy {strategy.Symbol}. Strategy-dependent checks are unavailable."
            : strategy is null ? "Select a matching saved strategy to assess grid settings, costs, and exposure." : null;
        if (environmentId == ExecutionEnvironmentIds.PaperLocal)
            return Response([], GridAdvisory.Evaluate(new([], null, null, null, null, null, null, "Advisory is unavailable for simulated Paper data.")), null,
                "Advisory is unavailable for simulated Paper data.");

        // These requests never touch EF's scoped context and may run concurrently.
        var frames = await Task.WhenAll(Intervals.Select(async interval =>
        {
            try
            {
                var candles = await cache.GetOrCreateAsync($"advisory:candles:{environmentId}:{symbol}:{interval.Interval}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                    return await market.GetCandlesAsync(symbol, interval.Interval, 300, ct, network);
                });
                return GridAdvisory.Frame(interval.Interval, interval.Seconds,
                    candles!.Select(x => new MarketBar(x.Time, x.Open, x.High, x.Low, x.Close)), clock.GetUtcNow());
            }
            catch (Exception ex) when (Recoverable(ex, ct))
            {
                return new AdvisoryFrame(interval.Interval, null, null, null, null, null, ex is ArgumentException ? ex.Message : "Market history is temporarily unavailable.");
            }
        }));
        GridConfiguration? config = null;
        GridPlan? plan = null;
        ExecutionQuote? quote = null;
        decimal? equity = null, funding = null;
        string? invalid = null, feeSource = null;
        var unavailable = notice;
        if (matches && request is not null)
        {
            config = request.ToConfiguration();
            if (string.IsNullOrWhiteSpace(accountId)) unavailable = "Select an execution account to assess costs and exposure.";
            else
            {
                var selection = await environments.ResolveAsync(environmentId, accountId, ct);
                try { equity = (await market.GetAccountStateAsync(selection.AccountId, symbol, ct)).TradingEquity; }
                catch (Exception ex) when (Recoverable(ex, ct)) { unavailable = "Account equity is temporarily unavailable."; }
                try
                {
                    var metadata = JsonSerializer.SerializeToElement(await info.GetPerpetualMetadata(ct, network));
                    var item = metadata.GetProperty("universe").EnumerateArray().FirstOrDefault(x => x.GetProperty("symbol").GetString() == symbol);
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("fundingRate", out var rate) && rate.ValueKind == JsonValueKind.Number)
                        funding = rate.GetDecimal();
                }
                catch (Exception ex) when (Recoverable(ex, ct)) { /* Missing funding is reported by the costs check. */ }
                try
                {
                    quote = await adapter.GetQuoteAsync(selection, symbol, ct);
                    var instrument = await adapter.GetInstrumentAsync(selection, symbol,
                        config.CenterSuggestionMode == "MANUAL" ? config.CenterPrice : quote.Mid, ct);
                    feeSource = instrument.FeeSource;
                    if (quote.Bid <= 0 || quote.Ask < quote.Bid || quote.AsOf > clock.GetUtcNow().AddSeconds(2) ||
                        clock.GetUtcNow() - quote.AsOf > TimeSpan.FromSeconds(config.MarketDataStaleSeconds))
                    { quote = null; unavailable = "A fresh, valid quote is required to assess this grid."; }
                    else
                    {
                        if (config.CenterSuggestionMode == "CURRENT_MID") config = config with { CenterPrice = quote.Mid, InitialBid = quote.Bid, InitialAsk = quote.Ask };
                        if (config.CenterSuggestionMode is not ("CURRENT_MID" or "MANUAL")) throw new GridValidationException("CENTER_MODE_INVALID", "Choose Current Mid or Manual.");
                        config = config with { TickSize = instrument.TickSize, QuantityStep = instrument.QuantityStep,
                            MinOrderQuantity = instrument.MinOrderQuantity, MinOrderNotional = instrument.MinOrderNotional,
                            MaxActiveOrders = instrument.MaxActiveOrders, SizeDecimals = instrument.SizeDecimals,
                            MakerFeeRate = instrument.MakerFeeRate, TakerFeeRate = instrument.TakerFeeRate };
                        plan = GridMath.BuildPlan(config, instrument.Rules);
                    }
                }
                catch (GridValidationException ex) { invalid = ex.Message; }
                catch (Exception ex) when (Recoverable(ex, ct)) { quote = null; unavailable = "Quote or instrument rules are temporarily unavailable."; }
            }
        }
        var result = GridAdvisory.Evaluate(new(frames, config, plan, quote?.Bid, quote?.Ask, equity, funding,
            unavailable, invalid, TakeProfitMayTakeLiquidity: true, FeeSource: feeSource));
        return Response(frames, result, quote?.AsOf, notice);

        GridAdvisoryResponse Response(IReadOnlyList<AdvisoryFrame> data, AdvisoryResult result, DateTimeOffset? quoteTime, string? message) =>
            new(environmentId, symbol, accountId, strategy?.Id, strategy?.Version, strategy?.Name, mode, matches,
                AdvisoryRules.Version, clock.GetUtcNow(), quoteTime, data, result.Status, result.Checks, result.Scenarios, message);
    }

    private static bool Recoverable(Exception ex, CancellationToken ct) => ex is HttpRequestException or JsonException or ArgumentException or TradingProblemException or FormatException
        || ex is OperationCanceledException && !ct.IsCancellationRequested;
}
