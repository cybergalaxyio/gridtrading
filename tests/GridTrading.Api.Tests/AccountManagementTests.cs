using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridTrading.Api.Tests;

public sealed class AccountManagementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Address = "0x00000000000000000000000000000000000000ab";
    private static string Key(int value) => "0x" + value.ToString("x64");

    [Fact]
    public async Task CreatesDisabledEncryptedAccountsWithPublicResponsesAndNoExchangeActions()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("MAINNET", new("  Main trading  ", Address.ToUpperInvariant(), Key(1)), Ct);
        Assert.False(account.Enabled);
        Assert.Equal("Main trading", account.Name);
        Assert.Equal(Address, account.AccountAddress);
        var stored = await f.Db.HyperliquidAccounts.SingleAsync(Ct);
        Assert.NotEqual(Key(1), stored.EncryptedAgentPrivateKey);
        Assert.Equal(Key(1), f.Protector.Unprotect(stored.EncryptedAgentPrivateKey));
        var json = JsonSerializer.Serialize(await f.Service.ListAsync("MAINNET", Ct));
        Assert.DoesNotContain(Key(1), json);
        Assert.DoesNotContain(stored.EncryptedAgentPrivateKey, json);
        Assert.DoesNotContain("EncryptedAgentPrivateKey", json);
        Assert.DoesNotContain(Key(1), (await f.Db.AuditLogs.SingleAsync(Ct)).Detail);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task DuplicateAccountAndAgentAreRejectedAcrossDisabledRowsAndNetworks()
    {
        await using var f = await Fixture.Create();
        await f.Service.CreateAsync("TESTNET", new("First", Address, Key(1)), Ct);
        Assert.Equal("ACCOUNT_ALREADY_CONFIGURED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.CreateAsync("TESTNET", new("Duplicate", Address.ToUpperInvariant(), Key(2)), Ct))).Code);
        Assert.Equal("AGENT_ALREADY_CONFIGURED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.CreateAsync("MAINNET", new("Agent reused", Address, Key(1)), Ct))).Code);
        var mainnet = await f.Service.CreateAsync("MAINNET", new("Separate network", Address, Key(2)), Ct);
        Assert.Equal("MAINNET", mainnet.Environment);
        Assert.Equal(2, await f.Db.HyperliquidAccounts.CountAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sensitive-invalid-secret")]
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public async Task InvalidKeysAreSanitizedAndNeverPersisted(string key)
    {
        await using var f = await Fixture.Create();
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => f.Service.CreateAsync("TESTNET", new("Invalid", Address, key), Ct));
        Assert.Equal("API_WALLET_KEY_INVALID", error.Code);
        if (key.Length > 0) Assert.DoesNotContain(key, error.ToString());
        Assert.Empty(await f.Db.HyperliquidAccounts.ToListAsync(Ct));
    }

    [Fact]
    public async Task MainWalletAndMissingEncryptionKeyAreRejected()
    {
        await using var f = await Fixture.Create();
        Assert.Equal("MAIN_WALLET_KEY_REJECTED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.CreateAsync("TESTNET", new("Main wallet", HyperliquidL1Signer.DeriveAddress(Key(1)), Key(1)), Ct))).Code);
        var unconfigured = new HyperliquidAccountManagementService(f.Db,
            new CredentialProtector(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["GRID_TRADING_CREDENTIAL_KEY"] = "invalid" }).Build()), f.Client, f.Gate);
        Assert.Equal("CREDENTIAL_KEY_NOT_CONFIGURED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            unconfigured.CreateAsync("MAINNET", new("Unconfigured", Address, Key(1)), Ct))).Code);
        Assert.Empty(await f.Db.HyperliquidAccounts.ToListAsync(Ct));
    }

    [Fact]
    public async Task DisabledHealthAndEnableVerifyAuthorizationWithoutPlacingOrdersOrRequiringFunds()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("TESTNET", new("Test", Address, Key(1)), Ct);
        var health = new HyperliquidAccountStatusService(f.Db, f.Protector, f.Client);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(await health.HealthAsync(account.AccountId, Ct), JsonSupport.Options));
        Assert.True(json.RootElement.GetProperty("agentApproved").GetBoolean());
        Assert.False(json.RootElement.GetProperty("tradingReady").GetBoolean());
        Assert.False((await f.Db.HyperliquidAccounts.SingleAsync(Ct)).Enabled);
        Assert.True((await f.Service.EnableAsync("TESTNET", account.AccountId, Ct)).Enabled);
        Assert.All(f.Handler.Requests, x => Assert.EndsWith("/info", x));
        Assert.Equal(0, (await f.Db.HyperliquidAccounts.SingleAsync(Ct)).LastNonce);
        Assert.Empty(await f.Db.Orders.ToListAsync(Ct));
        Assert.False((await f.Service.DisableAsync("TESTNET", account.AccountId, Ct)).Enabled);
        f.Handler.Approved = false;
        Assert.Equal("API_WALLET_NOT_APPROVED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.EnableAsync("TESTNET", account.AccountId, Ct))).Code);
        Assert.False((await f.Db.HyperliquidAccounts.SingleAsync(Ct)).Enabled);
    }

    [Fact]
    public async Task ReplacementDisablesAccountPreservesIdentityAndNonceAndNeedsFreshVerification()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("MAINNET", new("Test", Address, Key(1)), Ct);
        await f.Service.EnableAsync("MAINNET", account.AccountId, Ct);
        (await f.Db.HyperliquidAccounts.SingleAsync(Ct)).LastNonce = 123456;
        await f.Db.SaveChangesAsync(Ct);
        var replaced = await f.Service.ReplaceCredentialsAsync("MAINNET", account.AccountId, new(Key(2)), Ct);
        Assert.False(replaced.Enabled);
        Assert.Equal(account.AccountId, replaced.AccountId);
        Assert.Equal(HyperliquidL1Signer.DeriveAddress(Key(2)), replaced.AgentAddress);
        Assert.Equal(123456, (await f.Db.HyperliquidAccounts.SingleAsync(Ct)).LastNonce);
        Assert.Equal(Key(2), f.Protector.Unprotect((await f.Db.HyperliquidAccounts.SingleAsync(Ct)).EncryptedAgentPrivateKey));
        Assert.Equal("EXECUTION_ACCOUNT_NOT_FOUND", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.DisableAsync("TESTNET", account.AccountId, Ct))).Code);
    }

    [Fact]
    public async Task UnreadableCredentialsCannotEnable()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("TESTNET", new("Test", Address, Key(1)), Ct);
        (await f.Db.HyperliquidAccounts.SingleAsync(Ct)).EncryptedAgentPrivateKey = "corrupt";
        await f.Db.SaveChangesAsync(Ct);
        Assert.Equal("ACCOUNT_CREDENTIALS_UNREADABLE", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Service.EnableAsync("TESTNET", account.AccountId, Ct))).Code);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task ActiveAndRestartingStrategiesBlockDisruptiveChangesButAllowRename()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("TESTNET", new("Test", Address, Key(1)), Ct);
        var cycle = Cycle(account.AccountId);
        f.Db.Cycles.Add(cycle);
        f.Db.Strategies.Add(new StrategyEntity { Id = cycle.StrategyId, Name = "Blocking grid", Symbol = "SOL", ConfigurationJson = "{}" });
        await f.Db.SaveChangesAsync(Ct);
        var described = await f.Service.RenameAsync("TESTNET", account.AccountId, new("Renamed"), Ct);
        Assert.Equal(1, described.ActiveCycleCount);
        Assert.Equal("Blocking grid", Assert.Single(described.Blockers).StrategyName);
        Assert.Equal("ACCOUNT_IN_USE", (await Assert.ThrowsAsync<TradingProblemException>(() => f.Service.DisableAsync("TESTNET", account.AccountId, Ct))).Code);
        Assert.Equal("ACCOUNT_IN_USE", (await Assert.ThrowsAsync<TradingProblemException>(() => f.Service.ReplaceCredentialsAsync("TESTNET", account.AccountId, new(Key(2)), Ct))).Code);
        cycle.IsTerminal = true;
        f.Db.Operations.Add(new OperationEntity { Id = "restart", CommandId = "restart", ResourceId = cycle.Id, Type = "AUTO_RESTART", Status = "ACCEPTED", IdempotencyKey = "restart", RequestHash = "", ErrorCode = "" });
        await f.Db.SaveChangesAsync(Ct);
        described = await f.Service.GetAsync("TESTNET", account.AccountId, Ct);
        Assert.Equal(0, described.ActiveCycleCount);
        Assert.Equal("AUTO_RESTART_PENDING", Assert.Single(described.Blockers).Reason);
        Assert.Equal("ACCOUNT_IN_USE", (await Assert.ThrowsAsync<TradingProblemException>(() => f.Service.DisableAsync("TESTNET", account.AccountId, Ct))).Code);
    }

    [Fact]
    public async Task DisableWaitsForStartGateAndRechecksNewCycles()
    {
        await using var f = await Fixture.Create();
        var account = await f.Service.CreateAsync("TESTNET", new("Test", Address, Key(1)), Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = f.Gate.RunAsync(account.AccountId, async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(Ct);
            f.Db.Cycles.Add(Cycle(account.AccountId));
            await f.Db.SaveChangesAsync(Ct);
        }, Ct);
        await entered.Task.WaitAsync(Ct);
        var disable = f.Service.DisableAsync("TESTNET", account.AccountId, Ct);
        Assert.False(disable.IsCompleted);
        release.SetResult();
        await start;
        Assert.Equal("ACCOUNT_IN_USE", (await Assert.ThrowsAsync<TradingProblemException>(() => disable)).Code);
    }

    [Fact]
    public async Task LegacyBootstrapDoesNotOverwritePortalManagedAccountEvenWithStaleInvalidEnvironment()
    {
        await using var f = await Fixture.Create();
        var services = new ServiceCollection().AddSingleton(f.Db).AddSingleton(f.Protector).BuildServiceProvider();
        await using var provider = services;
        var now = DateTimeOffset.UtcNow;
        f.Db.HyperliquidAccounts.Add(new HyperliquidAccountEntity { Id = "hl_testnet_default", Name = "Portal name", AccountAddress = Address,
            AgentAddress = HyperliquidL1Signer.DeriveAddress(Key(1)), EncryptedAgentPrivateKey = f.Protector.Protect(Key(1)), Environment = "TESTNET", Enabled = false, CreatedAt = now, UpdatedAt = now });
        await f.Db.SaveChangesAsync(Ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS"] = "stale-invalid-value",
            ["GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY"] = "stale-invalid-key"
        }).Build();
        var bootstrap = new HyperliquidAccountBootstrap(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<HyperliquidAccountBootstrap>.Instance);
        await bootstrap.StartAsync(Ct);
        var stored = await f.Db.HyperliquidAccounts.SingleAsync(Ct);
        Assert.Equal("Portal name", stored.Name);
        Assert.False(stored.Enabled);
        Assert.Equal(Key(1), f.Protector.Unprotect(stored.EncryptedAgentPrivateKey));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:5050", "http://localhost:5050", "application/json", true)]
    [InlineData("::1", "[::1]:5050", "http://[::1]:5050", "application/json", true)]
    [InlineData("127.0.0.1", "localhost:5050", "http://localhost:5173", "application/json; charset=utf-8", true)]
    [InlineData("192.168.1.5", "localhost:5050", "http://localhost:5050", "application/json", false)]
    [InlineData("127.0.0.1", "attacker.example", "http://attacker.example", "application/json", false)]
    [InlineData("127.0.0.1", "localhost:5050", "https://attacker.example", "application/json", false)]
    [InlineData("127.0.0.1", "localhost:5050", "http://localhost:9999", "application/json", false)]
    [InlineData("127.0.0.1", "localhost:5050", "", "application/json", false)]
    [InlineData("127.0.0.1", "localhost:5050", "http://localhost:5050", "text/plain", false)]
    public void MutationsRequireTrustedLocalHostOriginAndJson(string remote, string host, string origin, string contentType, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;
        context.Request.ContentType = contentType;
        Assert.Equal(allowed, LocalAccountRequestPolicy.IsAllowed(context));
    }

    [Fact]
    public async Task RealHttpRoutesCreateListRenameEnableAndEnforceOriginAndNetwork()
    {
        await using var f = await Fixture.Create();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlite(f.Connection));
        builder.Services.AddSingleton(f.Protector);
        builder.Services.AddSingleton(f.Gate);
        builder.Services.AddSingleton(f.Http);
        builder.Services.AddSingleton<HyperliquidL1Signer>();
        builder.Services.AddScoped<HyperliquidNonceManager>();
        builder.Services.AddScoped<HyperliquidTradingClient>();
        builder.Services.AddScoped<HyperliquidInfoClient>();
        builder.Services.AddScoped<HyperliquidMarketDataClient>();
        builder.Services.AddScoped<HyperliquidOrderOwnershipService>();
        builder.Services.AddScoped<HyperliquidAccountManagementService>();
        builder.Services.AddScoped<HyperliquidAccountStatusService>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (TradingProblemException ex)
            {
                context.Response.StatusCode = ex.Status;
                await context.Response.WriteAsJsonAsync(new { code = ex.Code, detail = ex.Message });
            }
        });
        app.MapHyperliquidTestnetEndpoints();
        await app.StartAsync(Ct);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            const string route = "/api/v1/hyperliquid-testnet/accounts";
            var request = new CreateAccountRequest("HTTP account", Address, Key(1));
            using var forbidden = await http.PostAsJsonAsync(route, request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            http.DefaultRequestHeaders.Add("Origin", http.BaseAddress.GetLeftPart(UriPartial.Authority));
            using var created = await http.PostAsJsonAsync(route, request, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var account = (await created.Content.ReadFromJsonAsync<HyperliquidAccountResponse>(Ct))!;
            Assert.False(account.Enabled);
            using var health = await http.GetAsync($"{route}/{account.AccountId}/health", Ct);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var enabled = await http.PostAsJsonAsync($"{route}/{account.AccountId}/test-and-enable", new { }, Ct);
            Assert.True((await enabled.Content.ReadFromJsonAsync<HyperliquidAccountResponse>(Ct))!.Enabled);
            using var renamed = await http.PatchAsJsonAsync($"{route}/{account.AccountId}", new RenameAccountRequest("Renamed over HTTP"), Ct);
            Assert.Equal("Renamed over HTTP", (await renamed.Content.ReadFromJsonAsync<HyperliquidAccountResponse>(Ct))!.Name);
            using var replaced = await http.PutAsJsonAsync($"{route}/{account.AccountId}/credentials", new ReplaceAccountCredentialsRequest(Key(2)), Ct);
            Assert.False((await replaced.Content.ReadFromJsonAsync<HyperliquidAccountResponse>(Ct))!.Enabled);
            using var wrongNetwork = await http.PostAsJsonAsync($"/api/v1/hyperliquid-mainnet/accounts/{account.AccountId}/disable", new { }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, wrongNetwork.StatusCode);
            var list = await http.GetStringAsync(route, Ct);
            Assert.Contains("Renamed over HTTP", list);
            Assert.DoesNotContain(Key(1), list);
            Assert.DoesNotContain(Key(2), list);
            Assert.DoesNotContain("encrypted", list, StringComparison.OrdinalIgnoreCase);
            Assert.All(f.Handler.Requests, x => Assert.EndsWith("/info", x));
        }
        finally { await app.StopAsync(Ct); }
    }

    [Fact]
    public async Task NonceReservationsRemainIndependentForTwoAccountsOnTheSameNetwork()
    {
        await using var f = await Fixture.Create();
        var first = await f.Service.CreateAsync("TESTNET", new("First", Address, Key(1)), Ct);
        var second = await f.Service.CreateAsync("TESTNET", new("Second", "0x00000000000000000000000000000000000000ac", Key(2)), Ct);
        var accounts = await f.Db.HyperliquidAccounts.ToListAsync(Ct);
        foreach (var account in accounts) account.Enabled = true;
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        accounts.Single(x => x.Id == first.AccountId).LastNonce = future;
        await f.Db.SaveChangesAsync(Ct);
        var nonces = new HyperliquidNonceManager(f.Db);
        Assert.Equal(future + 1, await nonces.NextAsync(first.AccountId, Ct));
        var other = await nonces.NextAsync(second.AccountId, Ct);
        Assert.True(other < future);
        Assert.True(await nonces.NextAsync(second.AccountId, Ct) > other);
        Assert.Equal(future + 1, accounts.Single(x => x.Id == first.AccountId).LastNonce);
    }

    private static CycleEntity Cycle(string accountId) => new() { Id = "cycle", StrategyId = "strategy", ExecutionAccountId = accountId,
        ExecutionEnvironmentId = "hyperliquid-testnet", State = "RUNNING", FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "" };

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required TradingDbContext Db { get; init; }
        public required CredentialProtector Protector { get; init; }
        public required HttpClient Http { get; init; }
        public required HyperliquidTradingClient Client { get; init; }
        public required InfoHandler Handler { get; init; }
        public ExecutionAccountOperationGate Gate { get; } = new();
        public HyperliquidAccountManagementService Service => new(Db, Protector, Client, Gate);
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(Ct);
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(Ct);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(new byte[32]) }).Build();
            var protector = new CredentialProtector(configuration);
            var handler = new InfoHandler();
            var http = new HttpClient(handler);
            return new Fixture { Connection = connection, Db = db, Protector = protector, Handler = handler, Http = http,
                Client = new HyperliquidTradingClient(http, configuration, db, protector, new HyperliquidNonceManager(db), new HyperliquidL1Signer()) };
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class InfoHandler : HttpMessageHandler
    {
        public bool Approved { get; set; } = true;
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.EndsWith("/info", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var response = body.RootElement.GetProperty("type").GetString() switch
            {
                "userRole" => JsonSerializer.Serialize(new { role = Approved ? "agent" : "missing", data = new { user = Address } }),
                "clearinghouseState" => """{"assetPositions":[],"marginSummary":{"accountValue":"0"},"withdrawable":"0"}""",
                "spotClearinghouseState" => """{"balances":[]}""",
                "userAbstraction" => "\"disabled\"",
                "openOrders" => "[]",
                _ => throw new InvalidOperationException("Unexpected exchange action")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
