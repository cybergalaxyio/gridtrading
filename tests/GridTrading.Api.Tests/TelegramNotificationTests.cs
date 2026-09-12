using System.Net;
using System.Text;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridTrading.Api.Tests;

public sealed class TelegramNotificationTests
{
    private const string Token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghi";

    [Fact]
    public void TelegramEncryptionUsesADifferentPurposeWithoutChangingHyperliquidCiphertext()
    {
        var protector = Protector();
        var hyperliquidCiphertext = protector.Protect("secret");
        var telegramCiphertext = protector.ProtectTelegramBotToken("secret");

        Assert.Equal("secret", protector.Unprotect(hyperliquidCiphertext));
        Assert.Equal("secret", protector.UnprotectTelegramBotToken(telegramCiphertext));
        Assert.ThrowsAny<Exception>(() => protector.UnprotectTelegramBotToken(hyperliquidCiphertext));
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(telegramCiphertext));
    }

    [Fact]
    public async Task SettingsEncryptTokenRequireSuccessfulTestAndInvalidateOnChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await using var db = Database(connection);
        var bot = new FakeBot();
        var service = new TelegramNotificationSettingsService(db, Protector(), bot);

        var saved = await service.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
        Assert.True(saved.TokenStored);
        Assert.False(saved.Enabled);
        Assert.DoesNotContain(Token, (await db.TelegramNotificationSettings.SingleAsync(ct)).EncryptedBotToken);

        var enabled = await service.TestAndEnableAsync(ct);
        Assert.True(enabled.Enabled);
        Assert.Equal("grid_alert_bot", enabled.BotUsername);
        Assert.NotNull(enabled.EnabledAt);
        Assert.Single(bot.Messages);

        var unchanged = await service.SaveAsync(new TelegramSettingsRequest(null, "-100123"), ct);
        Assert.True(unchanged.Enabled);

        var changed = await service.SaveAsync(new TelegramSettingsRequest(null, "-100456"), ct);
        Assert.False(changed.Enabled);
        Assert.Null(changed.VerifiedAt);
        Assert.Null(changed.EnabledAt);
    }

    [Fact]
    public async Task FailedTestLeavesConfigurationDisabledAndRecordsSafeError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await using var db = Database(connection);
        var bot = new FakeBot { Failure = new TelegramBotApiException("chat not found") };
        var service = new TelegramNotificationSettingsService(db, Protector(), bot);
        await service.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);

        var error = await Assert.ThrowsAsync<TradingProblemException>(() => service.TestAndEnableAsync(ct));

        Assert.Equal(422, error.Status);
        var status = await service.GetAsync(ct);
        Assert.False(status.Enabled);
        Assert.Equal("chat not found", status.LastTestError);
        Assert.NotNull(status.LastTestedAt);
    }

    [Fact]
    public async Task MissingCredentialKeyRejectsSecretStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await using var db = Database(connection);
        var service = new TelegramNotificationSettingsService(
            db, new CredentialProtector(new ConfigurationBuilder().Build()), new FakeBot());

        var error = await Assert.ThrowsAsync<TradingProblemException>(() =>
            service.SaveAsync(new TelegramSettingsRequest(Token, "123"), ct));

        Assert.Equal(503, error.Status);
        Assert.Equal("CREDENTIAL_KEY_NOT_CONFIGURED", error.Code);
    }

    [Fact]
    public async Task DispatcherSendsOnlyPostEnableAlertsOnceAndDoesNotRetryFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);

        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.Add(Alert("old", "INFO", DateTimeOffset.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync(ct);
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
            await settings.TestAndEnableAsync(ct);
        });

        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.AddRange(
                Alert("info", "INFO", DateTimeOffset.UtcNow),
                Alert("warning", "WARNING", DateTimeOffset.UtcNow.AddTicks(1)),
                Alert("critical", "CRITICAL", DateTimeOffset.UtcNow.AddTicks(2)));
            await db.SaveChangesAsync(ct);
        });

        var dispatcher = Dispatcher(provider, bot);
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        Assert.False(await dispatcher.ProcessNextAsync(ct));
        Assert.Equal(4, bot.Messages.Count);
        Assert.Contains(bot.Messages, message => message.Text.Contains("Severity: CRITICAL", StringComparison.Ordinal));

        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.Add(Alert("failed", "CRITICAL", DateTimeOffset.UtcNow.AddTicks(3)));
            await db.SaveChangesAsync(ct);
        });
        bot.Failure = new TelegramBotApiException("temporary failure", serviceUnavailable: true);
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        bot.Failure = null;

        var restarted = Dispatcher(provider, bot);
        Assert.False(await restarted.ProcessNextAsync(ct));
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(4, await db.TelegramAlertDeliveries.CountAsync(ct));
            var failed = await db.TelegramAlertDeliveries.SingleAsync(x => x.RiskAlertId == "failed", ct);
            Assert.Null(failed.DeliveredAt);
            Assert.Equal("temporary failure", failed.Error);
            var status = await db.TelegramNotificationSettings.SingleAsync(ct);
            Assert.Equal("FAILED", status.LastDeliveryStatus);
        });

        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.DisableAsync(ct);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.Add(Alert("while-disabled", "INFO", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
        });
        Assert.False(await restarted.ProcessNextAsync(ct));

        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.TestAndEnableAsync(ct);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.Add(Alert("after-reenable", "INFO", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
        });
        Assert.True(await restarted.ProcessNextAsync(ct));
        Assert.False(await restarted.ProcessNextAsync(ct));
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.False(await db.TelegramAlertDeliveries.AnyAsync(x => x.RiskAlertId == "while-disabled", ct));
            Assert.True(await db.TelegramAlertDeliveries.AnyAsync(x => x.RiskAlertId == "after-reenable", ct));
        });
    }

    [Fact]
    public void MessageFormattingIsPlainTextCompleteAndRuneSafe()
    {
        var alert = Alert("long", "CRITICAL", DateTimeOffset.Parse("2026-09-12T01:02:03Z"));
        alert.CycleId = "cycle-1";
        alert.Code = "TEST_CODE";
        alert.Message = string.Concat(Enumerable.Repeat("😀", 5000));

        var text = TelegramAlertDispatcher.FormatMessage(alert);

        Assert.Contains("Severity: CRITICAL", text);
        Assert.Contains("Code: TEST_CODE", text);
        Assert.Contains("Cycle: cycle-1", text);
        Assert.Contains("2026-09-12 01:02:03 UTC", text);
        Assert.True(text.EnumerateRunes().Count() <= 4096);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public async Task BotClientUsesOfficialMethodsAndRedactsTokenFromErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"username":"grid_alert_bot"}}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":1}}"""));
        using var client = new TelegramBotClient(handler);

        var identity = await client.GetIdentityAsync(Token, ct);
        await client.SendMessageAsync(Token, "-100123", "hello", ct);

        Assert.Equal("grid_alert_bot", identity.Username);
        Assert.EndsWith("/getMe", handler.Requests[0].Uri.AbsolutePath);
        Assert.EndsWith("/sendMessage", handler.Requests[1].Uri.AbsolutePath);
        Assert.Contains("\"chat_id\":\"-100123\"", handler.Requests[1].Body);

        using var rejected = new TelegramBotClient(new QueueHandler(

            Json(HttpStatusCode.BadRequest,
                $$"""{"ok":false,"description":"bad token {{Token}}"}""")));
        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() =>
            rejected.GetIdentityAsync(Token, ct));
        Assert.DoesNotContain(Token, error.Message);
        Assert.Contains("[redacted]", error.Message);
    }

    private static RiskAlertEntity Alert(string id, string severity, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Severity = severity,
        Code = id.ToUpperInvariant(),
        Message = "alert " + id,
        CreatedAt = createdAt
    };

    private static TelegramAlertDispatcher Dispatcher(ServiceProvider provider, FakeBot bot) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        provider.GetRequiredService<CredentialProtector>(),
        bot,
        NullLogger<TelegramAlertDispatcher>.Instance);

    private static async Task InScope(ServiceProvider provider, Func<IServiceScope, Task> action)
    {
        using var scope = provider.CreateScope();
        await action(scope);
    }

    private static ServiceProvider Services(SqliteConnection connection, FakeBot bot)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration());
        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<ITelegramBotClient>(bot);
        services.AddDbContext<TradingDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<TelegramNotificationSettingsService>();
        return services.BuildServiceProvider();
    }

    private static TradingDbContext Database(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);

    private static async Task<SqliteConnection> OpenDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = Database(connection);
        await db.Database.EnsureCreatedAsync(ct);
        return connection;
    }

    private static CredentialProtector Protector() => new(Configuration());

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray())
        }).Build();

    private sealed class FakeBot : ITelegramBotClient
    {
        public List<(string ChatId, string Text)> Messages { get; } = [];
        public TelegramBotApiException? Failure { get; set; }

        public Task<TelegramBotIdentity> GetIdentityAsync(string botToken, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult(new TelegramBotIdentity("grid_alert_bot"));

        }

        public Task SendMessageAsync(string botToken, string chatId, string text, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            Messages.Add((chatId, text));
            return Task.CompletedTask;
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Body);

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri!,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
