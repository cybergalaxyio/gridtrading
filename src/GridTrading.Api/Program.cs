using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Hubs;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("GridTrading") ?? "Data Source=data/grid-trading.db";
builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSignalR();
builder.Services.AddSingleton<GridTrading.Api.Hubs.HyperliquidMarketSubscriptionRegistry>();
builder.Services.AddSingleton<MarketState>();
builder.Services.AddSingleton<PreviewStore>();
builder.Services.AddSingleton<ReplayStore>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddHttpClient<HyperliquidInfoClient>();
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<GridTrading.Api.Exchange.HyperliquidL1Signer>();
builder.Services.AddScoped<HyperliquidNonceManager>();
builder.Services.AddHttpClient<HyperliquidTradingClient>();
builder.Services.AddHttpClient<HyperliquidMarketDataClient>();
builder.Services.AddScoped<HyperliquidAccountStatusService>();
builder.Services.AddScoped<HyperliquidOrderOwnershipService>();
builder.Services.AddSingleton<HyperliquidAccountOperationGate>();
builder.Services.AddScoped<HyperliquidCycleCoordinator>();
builder.Services.AddHostedService<HyperliquidAccountBootstrap>();
builder.Services.AddHostedService<HyperliquidStrategyBootstrap>();
builder.Services.AddHostedService<HyperliquidFillWebSocketService>();
builder.Services.AddHostedService<HyperliquidReconciliationService>();
builder.Services.AddScoped<LegacyTradingService>();
builder.Services.AddScoped<TradingService>();
builder.Services.AddHostedService<MarketBroadcastService>();
builder.Services.AddHostedService<PaperExecutionService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new DecimalStringJsonConverter());
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var mutating = context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE";
    var remote = context.Connection.RemoteIpAddress;
    if (mutating && (remote is null || !System.Net.IPAddress.IsLoopback(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { title = "Local operator required", status = 403, code = "LOCAL_OPERATOR_REQUIRED", detail = "V1 write operations are accepted only from the local machine." });
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (TradingProblemException ex)
    {
        context.Response.StatusCode = ex.Status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = $"https://gridtrading.local/problems/{ex.Code.ToLowerInvariant().Replace('_', '-')}",
            title = ex.Message, status = ex.Status, code = ex.Code, detail = ex.Message,
            correlationId = Ids.New("corr"), errors = Array.Empty<object>()
        });
    }
    catch (GridValidationException ex)
    {
        context.Response.StatusCode = 422;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { title = "Grid validation failed", status = 422, code = ex.Code, detail = ex.Message, correlationId = Ids.New("corr") });
    }
});

await InitializeDatabase(app.Services, connectionString);

var api = app.MapGroup("/api/v1");

api.MapGet("/system/status", async (TradingDbContext db, CancellationToken ct) => new
{
    status = "HEALTHY", version = "1.0.0", environment = "PAPER", liveTradingEnabled = false,
    database = await db.Database.CanConnectAsync(ct) ? "HEALTHY" : "UNAVAILABLE",
    signalR = "HEALTHY", backgroundTasks = "RUNNING", serverTime = DateTimeOffset.UtcNow, clockOffsetMs = 0
});
api.MapGet("/system/capabilities", () => new
{
    gridModes = new[] { "BUY_ONLY", "SELL_ONLY", "TWO_WAY" }, executionEnvironments = new[] { "REPLAY", "PAPER", "TESTNET" },
    liveTradingEnabled = false, autoRestartSupported = false, takeProfitModes = new[] { "POINTS" },
    automaticRegimeGateSupported = false
});
api.MapGet("/operations/{id}", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Operations.FindAsync([id], ct) is { } item ? Results.Ok(OperationDto(item)) : Results.NotFound());
api.MapGet("/operations/{id}/events", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Operations.AnyAsync(x => x.Id == id, ct)
        ? Results.Ok(new[] { new { status = "ACCEPTED", occurredAt = DateTimeOffset.UtcNow, detail = "Command accepted." }, new { status = "COMPLETED", occurredAt = DateTimeOffset.UtcNow, detail = "Authoritative state committed." } })
        : Results.NotFound());

api.MapGet("/exchange-accounts", async (TradingDbContext db, CancellationToken ct) =>
{
    var items = new List<object>
    {
        new { accountId = "acct_paper_01", name = "Weekend Paper", exchange = "PAPER", environment = "PAPER", queryAddress = "local-simulator", signingAddress = (string?)null, tradingEnabled = true }
    };
    items.AddRange((await db.HyperliquidAccounts.Where(x => x.Enabled).ToListAsync(ct)).Select(x => (object)new
        { accountId = x.Id, x.Name, exchange = "HYPERLIQUID", environment = x.Environment, queryAddress = x.AccountAddress, signingAddress = x.AgentAddress, tradingEnabled = true }));
    return items;
});
api.MapGet("/exchange-accounts/{id}", async (string id, TradingDbContext db, CancellationToken ct) =>
{
    if (id == "acct_paper_01") return Results.Ok(new { accountId = id, exchange = "PAPER", environment = "PAPER", funded = false, permissions = new[] { "READ", "PAPER_TRADE" } });
    var account = await db.HyperliquidAccounts.FindAsync([id], ct);
    return account is null ? Results.NotFound() : Results.Ok(HyperliquidAccountStatusService.Public(account));
});
api.MapGet("/exchange-accounts/{id}/health", async (string id, HyperliquidAccountStatusService status, CancellationToken ct) =>
    id == "acct_paper_01" ? Results.Ok(new { accountId = id, rest = "HEALTHY", permissions = "PAPER_ONLY", asOf = DateTimeOffset.UtcNow })
        : Results.Ok(await status.HealthAsync(id, ct)));
api.MapGet("/exchange-accounts/{id}/balances", (string id) => IsAccount(id)
    ? Results.Ok(new { equityUsdt = 13420.50m, availableBalanceUsdt = 8420m, marginUsedUsdt = 1080.50m })
    : Results.NotFound());
api.MapGet("/exchange-accounts/{id}/positions", async (string id, string? symbol, TradingDbContext db, CancellationToken ct) =>
    IsAccount(id) ? Results.Ok(await db.Cycles.Where(x => !x.IsTerminal && (symbol == null || x.FrozenConfigurationJson.Contains(symbol)))
        .Select(x => new { cycleId = x.Id, symbol = symbol ?? "SOLUSDT", netQuantity = x.ActualNetQuantity, reconstructedQuantity = x.ReconstructedNetQuantity }).ToListAsync(ct)) : Results.NotFound());
api.MapGet("/exchange-accounts/{id}/instruments/{symbol}", async (string id, string symbol, decimal? referencePrice,
    TradingDbContext db, HyperliquidInfoClient instruments, CancellationToken ct) =>
{
    if (id == "acct_paper_01" && symbol.Equals("SOLUSDT", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new ExchangeInstrumentMetadata("SOLUSDT", "PAPER", 0, 1, referencePrice is > 0m ? referencePrice.Value : 145.25m,
            .001m, .1m, .1m, 5m, 500, .0002m, .00055m, "PAPER_SIMULATOR", DateTimeOffset.UtcNow));
    var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == id && x.Enabled && x.Environment == "TESTNET", ct);
    if (account is null)
        return Results.NotFound();
    try { return Results.Ok(await instruments.GetPerpetualInstrument(symbol, referencePrice, account.AccountAddress, ct)); }
    catch (HttpRequestException) { return Results.Problem(statusCode: 503, title: "Hyperliquid Testnet metadata is unavailable."); }
});

api.MapGet("/market-data/{accountId}/{symbol}/snapshot", (string accountId, string symbol, MarketState market) =>
    IsAccount(accountId) ? Results.Ok(market.Snapshot(symbol)) : Results.NotFound());
api.MapGet("/market-data/{accountId}/{symbol}/candles", (string accountId, string symbol, MarketState market) =>
    IsAccount(accountId) ? Results.Ok(market.Candles()) : Results.NotFound());
api.MapGet("/market-data/{accountId}/{symbol}/center-suggestion", (string accountId, string symbol, string? mode, MarketState market) =>
{
    if (!IsAccount(accountId)) return Results.NotFound();
    var quote = market.Snapshot(symbol);
    return Results.Ok(new { mode = mode ?? "CURRENT_MID", suggestedCenterPrice = quote.Mid, quote.AsOf, quote.IsStale, inputs = new { quote.Bid, quote.Ask, lookback = mode == "VWAP_EMA" ? "60m" : "instant" } });
});
api.MapGet("/market-data/{accountId}/{symbol}/advisory", (string accountId, string symbol, MarketState market) =>
    Results.Ok(new { color = "GREEN", adx = 19.4m, atr = .84m, realisedVolatilityPct = 1.8m, volumeVsMedian = 1.04m, fundingRate = .0001m, reasons = Array.Empty<string>(), asOf = market.Snapshot(symbol).AsOf }));

api.MapGet("/strategies", async (TradingDbContext db, CancellationToken ct) =>
{
    var items = (await db.Strategies.Where(x => !x.Archived).ToListAsync(ct)).OrderByDescending(x => x.UpdatedAt).ToList();
    var active = await db.Cycles.Where(x => !x.IsTerminal).ToDictionaryAsync(x => x.StrategyId, ct);
    return items.Select(x => StrategyDto(x, active.GetValueOrDefault(x.Id))).ToArray();
});
api.MapPost("/strategies", async (StrategyRequest request, TradingService service, CancellationToken ct) =>
{
    var created = await service.CreateStrategy(request, ct);
    return Results.Created($"/api/v1/strategies/{created.Id}", StrategyDto(created, null));
});
api.MapGet("/strategies/{id}", async (string id, TradingDbContext db, CancellationToken ct) =>
{
    var strategy = await db.Strategies.FindAsync([id], ct); if (strategy is null) return Results.NotFound();
    var cycle = await db.Cycles.SingleOrDefaultAsync(x => x.StrategyId == id && !x.IsTerminal, ct);
    return Results.Ok(StrategyDto(strategy, cycle));
});
api.MapMethods("/strategies/{id}", new[] { "PATCH" }, async (string id, StrategyRequest request, TradingService service, CancellationToken ct) =>
    await service.UpdateStrategy(id, request, ct) is { } item ? Results.Ok(StrategyDto(item, null)) : Results.NotFound());
api.MapPost("/strategies/{id}/archive", async (string id, TradingDbContext db, CancellationToken ct) =>
{
    var item = await db.Strategies.FindAsync([id], ct); if (item is null) return Results.NotFound();
    if (await db.Cycles.AnyAsync(x => x.StrategyId == id && !x.IsTerminal, ct)) return Results.Conflict();
    item.Archived = true; await db.SaveChangesAsync(ct); return Results.NoContent();
});
api.MapGet("/strategies/{id}/versions", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Strategies.FindAsync([id], ct) is { } item ? Results.Ok(new[] { new { version = item.Version, item.UpdatedAt, configuration = TradingService.DeserializeStrategy(item) } }) : Results.NotFound());
api.MapGet("/strategies/{id}/versions/{version:int}", async (string id, int version, TradingDbContext db, CancellationToken ct) =>
    await db.Strategies.FindAsync([id], ct) is { } item && item.Version == version ? Results.Ok(new { version, configuration = TradingService.DeserializeStrategy(item) }) : Results.NotFound());
api.MapGet("/strategies/{id}/active-cycle", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.SingleOrDefaultAsync(x => x.StrategyId == id && !x.IsTerminal, ct) is { } cycle ? Results.Ok(CycleDto(cycle)) : Results.NoContent());

api.MapPost("/grid-plan-previews", async (PreviewRequest request, TradingService service, CancellationToken ct) =>
    Results.Ok(PreviewDto(await service.CreatePreview(request, ct))));
api.MapGet("/grid-plan-previews/{id}", (string id, PreviewStore store) =>
    store.Items.TryGetValue(id, out var item) ? Results.Ok(PreviewDto(item)) : Results.NotFound());

api.MapPost("/strategies/{id}/cycles", async (string id, StartCycleRequest request, HttpRequest http, HttpResponse response, TradingService service, CancellationToken ct) =>
{
    var (operation, _) = await service.StartCycle(id, request, Header(http, "Idempotency-Key"), ct);
    response.Headers.Location = $"/api/v1/operations/{operation.Id}";
    return Results.Accepted(value: OperationDto(operation));
});
api.MapGet("/cycles", async (string? strategyId, string? state, TradingDbContext db, CancellationToken ct) =>
{
    var cycles = await db.Cycles.Where(x => (strategyId == null || x.StrategyId == strategyId) && (state == null || x.State == state)).ToListAsync(ct);
    cycles = cycles.OrderByDescending(x => x.StartedAt).ToList();
    return cycles.Select(CycleDto).ToArray();
});
api.MapGet("/cycles/{id}", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } cycle ? Results.Ok(CycleDto(cycle)) : Results.NotFound());
api.MapGet("/cycles/{id}/snapshot", async (string id, TradingDbContext db, TradingService service, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } cycle ? Results.Ok(service.Snapshot(cycle)) : Results.NotFound());
api.MapGet("/cycles/{id}/plan", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(JsonSerializer.Deserialize<GridPlan>(c.FrozenPlanJson, JsonSupport.Options)) : Results.NotFound());
api.MapGet("/cycles/{id}/levels", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(JsonSerializer.Deserialize<GridPlan>(c.FrozenPlanJson, JsonSupport.Options)!.Levels) : Results.NotFound());
api.MapGet("/cycles/{id}/lots", async (string id, TradingDbContext db, CancellationToken ct) => await db.VirtualLots.Where(x => x.CycleId == id).ToListAsync(ct));
api.MapGet("/cycles/{id}/position", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(new { actualNetQuantity = c.ActualNetQuantity, reconstructedNetQuantity = c.ReconstructedNetQuantity, inSync = c.ActualNetQuantity == c.ReconstructedNetQuantity }) : Results.NotFound());
api.MapGet("/cycles/{id}/pnl", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(new { realisedCyclePnl = c.RealisedCyclePnl, paidFees = c.PaidFees, accruedFunding = c.AccruedFunding, liquidationPnl = c.RealisedCyclePnl - c.PaidFees - c.AccruedFunding }) : Results.NotFound());
api.MapGet("/cycles/{id}/risk-advisory", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.AnyAsync(x => x.Id == id, ct) ? Results.Ok(new { color = "GREEN", reasons = Array.Empty<string>(), adx = 19.4m, atr = .84m, fundingRate = .0001m }) : Results.NotFound());
api.MapGet("/cycles/{id}/reconciliation", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(new { status = c.ActualNetQuantity == c.ReconstructedNetQuantity ? "IN_SYNC" : "MISMATCH", c.LastReconciledAt, differences = Array.Empty<object>() }) : Results.NotFound());
api.MapGet("/cycles/{id}/operator-actions", async (string id, TradingDbContext db, CancellationToken ct) => (await db.AuditLogs.Where(x => x.ResourceId == id).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList());
api.MapGet("/cycles/{id}/report", async (string id, TradingDbContext db, CancellationToken ct) =>
    await db.Cycles.FindAsync([id], ct) is { } c ? Results.Ok(new { cycle = CycleDto(c), c.ExitReason, c.MaximumAdverseExcursion, c.MaximumDrawdown, orders = await db.Orders.Where(x => x.CycleId == id).ToListAsync(ct), executions = await db.Executions.Where(x => x.CycleId == id).ToListAsync(ct) }) : Results.NotFound());

MapCycleCommand(api, "pause-entries", "PAUSE_ENTRIES");
MapCycleCommand(api, "resume-entries", "RESUME_ENTRIES");
MapCycleCommand(api, "close", "CLOSE");
MapCycleCommand(api, "reconcile", "RECONCILE");
api.MapPost("/cycles/{id}/commands/emergency-flatten", async (string id, EmergencyCommandRequest request, HttpRequest http,
    HttpResponse response, TradingService service, CancellationToken ct) =>
{
    var c = request.Confirmation; var confirmed = c.CancelAllStrategyOrders && c.FlattenActualNetPosition && c.AcknowledgedTakerExecution;
    var operation = await service.Command(id, "EMERGENCY_FLATTEN", request.Reason, Header(http, "Idempotency-Key"), IfMatch(http), confirmed, ct);
    response.Headers.Location = $"/api/v1/operations/{operation.Id}"; return Results.Accepted(value: OperationDto(operation));
});

api.MapGet("/orders", async (string? cycleId, string? status, string? kind, TradingDbContext db, CancellationToken ct) =>
    (await db.Orders.Where(x => (cycleId == null || x.CycleId == cycleId) && (status == null || x.Status == status) && (kind == null || x.Kind == kind)).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList());
api.MapGet("/orders/{id}", async (string id, TradingDbContext db, CancellationToken ct) => await db.Orders.FindAsync([id], ct) is { } x ? Results.Ok(x) : Results.NotFound());
api.MapGet("/orders/{id}/events", async (string id, TradingDbContext db, CancellationToken ct) => await db.Orders.FindAsync([id], ct) is { } x ? Results.Ok(new[] { new { status = x.Status, x.UpdatedAt } }) : Results.NotFound());
api.MapGet("/orders/{id}/executions", async (string id, TradingDbContext db, CancellationToken ct) => await db.Executions.Where(x => x.OrderId == id).ToListAsync(ct));
api.MapGet("/executions", async (string? cycleId, string? side, TradingDbContext db, CancellationToken ct) => (await db.Executions.Where(x => (cycleId == null || x.CycleId == cycleId) && (side == null || x.Side == side)).ToListAsync(ct)).OrderByDescending(x => x.OccurredAt).ToList());
api.MapGet("/executions/{id}", async (string id, TradingDbContext db, CancellationToken ct) => await db.Executions.FindAsync([id], ct) is { } x ? Results.Ok(x) : Results.NotFound());
api.MapGet("/lots/{id}", async (string id, TradingDbContext db, CancellationToken ct) => await db.VirtualLots.FindAsync([id], ct) is { } x ? Results.Ok(x) : Results.NotFound());
api.MapGet("/risk-alerts", async (string? severity, TradingDbContext db, CancellationToken ct) => (await db.RiskAlerts.Where(x => severity == null || x.Severity == severity).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList());
api.MapGet("/risk-alerts/{id}", async (string id, TradingDbContext db, CancellationToken ct) => await db.RiskAlerts.FindAsync([id], ct) is { } x ? Results.Ok(x) : Results.NotFound());
api.MapPost("/risk-alerts/{id}/acknowledgements", async (string id, AcknowledgementRequest request, TradingDbContext db, CancellationToken ct) =>
{
    var alert = await db.RiskAlerts.FindAsync([id], ct); if (alert is null) return Results.NotFound();
    alert.Acknowledged = true; alert.AcknowledgementNote = request.Note; await db.SaveChangesAsync(ct); return Results.Ok(alert);
});

app.MapReplayAndExchangeEndpoints();
app.MapHyperliquidTestnetEndpoints();
app.MapHub<TradingHub>("/hubs/trading");
app.MapFallbackToFile("index.html");
app.Run();

static void MapCycleCommand(RouteGroupBuilder api, string route, string command) => api.MapPost($"/cycles/{{id}}/commands/{route}",
    async (string id, CommandRequest request, HttpRequest http, HttpResponse response, TradingService service, CancellationToken ct) =>
    {
        var operation = await service.Command(id, command, request.Reason, Header(http, "Idempotency-Key"), IfMatch(http), false, ct);
        response.Headers.Location = $"/api/v1/operations/{operation.Id}"; return Results.Accepted(value: OperationDto(operation));
    });

static string Header(HttpRequest request, string name) => request.Headers.TryGetValue(name, out var value) ? value.ToString() : "";
static long? IfMatch(HttpRequest request)
{
    var value = Header(request, "If-Match").Trim('"'); return long.TryParse(value, out var parsed) ? parsed : null;
}
static bool IsAccount(string id) => id == "acct_paper_01";
static object OperationDto(OperationEntity x) => new { operationId = x.Id, x.CommandId, x.ResourceId, type = x.Type, status = x.Status, x.AcceptedAt, x.CompletedAt };
static object CycleDto(CycleEntity x) => new
{
    cycleId = x.Id, x.StrategyId, state = x.State, x.StateVersion, x.IsTerminal, x.OperatorResetRequired,
    x.FixedCenterPrice, frozenConfiguration = JsonSerializer.Deserialize<GridConfiguration>(x.FrozenConfigurationJson, JsonSupport.Options),
    x.StartedAt, x.EndedAt, x.ExitReason
};
static object StrategyDto(StrategyEntity x, CycleEntity? cycle) => new { strategyId = x.Id, x.Name, x.ExchangeAccountId, x.Symbol, x.Version, x.Archived, configuration = TradingService.DeserializeStrategy(x), activeCycle = cycle is null ? null : CycleDto(cycle), x.CreatedAt, x.UpdatedAt };
static object PreviewDto(PreviewCacheItem x) => new
{
    previewId = x.Id, x.ExpiresAt, strategyVersion = x.StrategyVersion, marketDataAsOf = DateTimeOffset.UtcNow,
    confirmedCenterPrice = x.Plan.CenterPrice, x.Plan.OutermostBuyPrice, x.Plan.OutermostSellPrice,
    x.Plan.CoverageBelowPct, x.Plan.CoverageAbovePct,
    maximumPlannedQuantityPerSide = x.Plan.Levels.GroupBy(l => l.Side).Select(g => g.Sum(l => l.PlannedQuantity)).DefaultIfEmpty(0m).Max(),
    maximumPlannedNotionalPerSide = x.Plan.Levels.GroupBy(l => l.Side).Select(g => g.Sum(l => l.OrderNotional)).DefaultIfEmpty(0m).Max(),
    startEligible = true, blockingErrors = Array.Empty<object>(), warnings = Array.Empty<object>(), x.Plan.Levels
};

static async Task InitializeDatabase(IServiceProvider services, string connectionString)
{
    var sqlitePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    if (!string.IsNullOrWhiteSpace(sqlitePath)) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sqlitePath))!);
    using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseCompatibility.EnsureTestnetSchemaAsync(db);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    if (!await db.Strategies.AnyAsync())
    {
        var now = DateTimeOffset.UtcNow; var request = StrategyRequest.Default;
        db.Strategies.Add(new StrategyEntity { Id = "strategy_weekend_sol", Name = request.Name, ExchangeAccountId = request.ExchangeAccountId,
            Symbol = request.Symbol, ConfigurationJson = JsonSerializer.Serialize(request, JsonSupport.Options), CreatedAt = now, UpdatedAt = now });
        db.RiskAlerts.AddRange(
            new RiskAlertEntity { Id = "alert_001", Severity = "CRITICAL", Code = "MARKET_DATA_STALE", Message = "行情曾短暂超过新鲜度阈值，已阻止创建新敞口。", CreatedAt = now.AddMinutes(-42) },
            new RiskAlertEntity { Id = "alert_002", Severity = "WARNING", Code = "VOLATILITY_ELEVATED", Message = "近期波动率升高，建议复核网格间距。", CreatedAt = now.AddMinutes(-18) },
            new RiskAlertEntity { Id = "alert_003", Severity = "INFO", Code = "RECONCILIATION_OK", Message = "Paper 账户订单与仓位 Sync 完成。", CreatedAt = now.AddMinutes(-2) });
        await db.SaveChangesAsync();
    }
}

public partial class Program;
