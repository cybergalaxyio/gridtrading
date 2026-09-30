using System.Net;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridTrading.Api.Tests;

public sealed partial class TelegramNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrderActionsRequireVerifiedPrivateChatAndNoWebhook()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            Assert.False((await settings.GetAsync(Ct)).OrderActionsEnabled);
            await settings.SaveAsync(new(Token, "-100123", true, "-100123"), Ct);
            await Assert.ThrowsAsync<TradingProblemException>(() => settings.TestAndEnableAsync(Ct));
            Assert.False((await settings.GetAsync(Ct)).OrderActionsReady);
            // Group notifications continue to work without actions.
            await settings.SaveAsync(new(null, "-100123", false), Ct);
            Assert.True((await settings.TestAndEnableAsync(Ct)).Enabled);
            await settings.SaveAsync(new(null, "123", true, "123"), Ct);
            bot.HasWebhook = true;
            var conflict = await Assert.ThrowsAsync<TradingProblemException>(() => settings.TestAndEnableAsync(Ct));
            Assert.Contains("webhook", conflict.Message);
            bot.HasWebhook = false;
            Assert.True((await settings.TestAndEnableAsync(Ct)).OrderActionsReady);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var generation = (await db.TelegramNotificationSettings.SingleAsync(Ct)).ActionsGeneration;
            await settings.DisableAsync(Ct);
            Assert.NotEqual(generation, (await db.TelegramNotificationSettings.SingleAsync(Ct)).ActionsGeneration);
            Assert.False((await settings.GetAsync(Ct)).OrderActionsReady);
        });
    }

    [Theory]
    [InlineData("ENTRY", "PLACE")]
    [InlineData("TAKE_PROFIT", "PLACE")]
    [InlineData("TAKE_PROFIT", "AMEND")]
    [InlineData("FLATTEN", "FLATTEN")]
    public async Task PendingOrdersSendExactDetailsAndBothActions(string kind, string action)
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await EnableActions(provider);
        await SeedReview(provider, "review", kind, action);
        await InScope(provider, async scope =>
        {
            var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            Assert.True(await service.DispatchNextAsync(Ct));
            Assert.False(await service.DispatchNextAsync(Ct));
        });
        var message = Assert.Single(bot.ButtonMessages);
        Assert.Contains("Price: 150.25", message.Text);
        Assert.Contains("Quantity: 2", message.Text);
        Assert.Contains("Order value: 300.5", message.Text);
        Assert.Contains($"Action: {action} / {kind}", message.Text);
        Assert.Contains("Strategy: Telegram grid", message.Text);
        Assert.Contains("Environment: paper-local", message.Text);
        Assert.Contains("Account: acct_paper_01", message.Text);
        Assert.Contains("NOT SENT", message.Text);
        Assert.Equal(2, Assert.Single(message.Buttons).Length);
        Assert.All(message.Buttons[0], button => Assert.True(System.Text.Encoding.UTF8.GetByteCount(button.Data) <= 64));
    }

    [Fact]
    public async Task TelegramAndDashboardDecisionsShareStateAndReplayCannotUndoANewerDecision()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review");
        await Dispatch(provider);
        var message = bot.ButtonMessages.Single();
        var callback = Callback(message, "decision", approve: true);
        await InScope(provider, async scope =>
        {
            var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            Assert.Equal("Approved—waiting for submission", await service.HandleCallbackAsync(generation, callback, Ct));
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("APPROVED", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Equal("telegram:123", (await db.AuditLogs.SingleAsync(Ct)).Actor);
            await scope.ServiceProvider.GetRequiredService<OrderApprovalService>().RejectAsync((await db.OrderApprovals.SingleAsync(Ct)).Id, Ct);
        });
        // A fresh scope simulates a restarted callback consumer.
        await InScope(provider, async scope =>
        {
            var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            await service.HandleCallbackAsync(generation, callback, Ct);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("REJECTED", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Equal(2, await db.AuditLogs.CountAsync(Ct));
            Assert.Single(await db.TelegramCallbackReceipts.ToListAsync(Ct));
        });
        await Dispatch(provider);
        Assert.Contains("Rejected", bot.Edits.Last().Text);
        Assert.StartsWith("Confirm", Assert.Single(Assert.Single(bot.Edits.Last().Buttons)).Text);
        await InScope(provider, async scope =>
        {
            await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>()
                .HandleCallbackAsync(generation, Callback(message, "reconsider", true), Ct);
        });
        await Dispatch(provider);
        Assert.StartsWith("Reject", Assert.Single(Assert.Single(bot.Edits.Last().Buttons)).Text);
    }

    [Theory]
    [InlineData(999, 123, "private", 0)]
    [InlineData(123, 999, "private", 0)]
    [InlineData(123, 123, "group", 0)]
    [InlineData(123, 123, "private", 99)]
    public async Task UnauthorizedOrForwardedCallbacksCannotApprove(long sender, long chat, string chatType, long messageOffset)
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        var callback = Callback(bot.ButtonMessages.Single(), "bad", true);
        callback = callback with { SenderId = sender, Message = callback.Message! with { ChatId = chat, ChatType = chatType,
            MessageId = callback.Message.MessageId + messageOffset } };
        await InScope(provider, async scope =>
        {
            await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>().HandleCallbackAsync(generation, callback, Ct);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("PENDING", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Empty(await db.AuditLogs.ToListAsync(Ct));
        });
    }

    [Fact]
    public async Task StaleOrderAndOldConfigurationButtonsNeverAuthorizeNewOrders()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        var callback = Callback(bot.ButtonMessages.Single(), "old", true);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            (await db.Orders.SingleAsync(Ct)).CancellationPending = true;
            await db.SaveChangesAsync(Ct);
            var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            Assert.Contains("changed", await service.HandleCallbackAsync(generation, callback, Ct));
        });
        await Dispatch(provider);
        Assert.Empty(bot.Edits.Last().Buttons);
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new(Token, "123", true, "123"), Ct);
            await settings.TestAndEnableAsync(Ct);
            var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            Assert.Contains("unavailable", await service.HandleCallbackAsync(generation, callback with { Id = "new-click" }, Ct));
            var newGeneration = (await service.SettingsAsync(Ct))!.ActionsGeneration;
            Assert.Contains("no longer valid", await service.HandleCallbackAsync(newGeneration, callback with { Id = "new-click" }, Ct));
        });
    }

    [Fact]
    public async Task PendingCommandPaginatesIncludesRejectedAndCursorSurvivesRestart()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        for (var i = 0; i < 11; i++) await SeedReview(provider, $"review-{i:D2}");
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            (await db.OrderApprovals.FirstAsync(Ct)).Status = "REJECTED";
            await db.SaveChangesAsync(Ct);
        });
        var worker = Worker(provider, bot);
        var command = new TelegramUpdate(40, new(123, "private", 123, 1, "/pending"), null);
        await worker.ProcessUpdateAsync(generation, Token, command, Ct);
        Assert.Equal(11, bot.ButtonMessages.Count); // ten order cards and a page footer
        Assert.Contains("Page 1/2", bot.ButtonMessages.Last().Text);
        Assert.Contains(bot.ButtonMessages, x => x.Text.Contains("Rejected"));
        await Worker(provider, bot).ProcessUpdateAsync(generation, Token, command, Ct);
        Assert.Equal(11, bot.ButtonMessages.Count);
        var footer = bot.ButtonMessages.Last();
        var next = new TelegramCallback("next", footer.Buttons[0][0].Data, 123, new(123, "private", 777, footer.Id, null));
        await worker.ProcessUpdateAsync(generation, Token, new(41, null, next), Ct);
        Assert.Equal(13, bot.ButtonMessages.Count);
        Assert.Contains("Page 2/2", bot.ButtonMessages.Last().Text);
        await InScope(provider, async scope => Assert.Equal(42, (await scope.ServiceProvider.GetRequiredService<TradingDbContext>()
            .TelegramNotificationSettings.SingleAsync(Ct)).NextUpdateId));
    }

    [Fact]
    public async Task UncertainSendIsNotRetriedAndPendingCommandCanRecoverIt()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review");
        bot.ButtonFailure = new("Telegram request timed out.", true);
        await Assert.ThrowsAsync<TelegramBotApiException>(() => Dispatch(provider));
        bot.ButtonFailure = null;
        await Dispatch(provider);
        Assert.Empty(bot.ButtonMessages);
        await Worker(provider, bot).ProcessUpdateAsync(generation, Token, new(1, new(123, "private", 123, 1, "/pending"), null), Ct);
        Assert.Equal(2, bot.ButtonMessages.Count);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("PENDING", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Equal(2, await db.TelegramApprovalMessages.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task FailedMessageEditDoesNotUndoCommittedApproval()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        await Worker(provider, bot).ProcessUpdateAsync(generation, Token,
            new(1, null, Callback(bot.ButtonMessages.Single(), "click", true)), Ct);
        bot.EditFailure = new("rate limited", true, 60);
        await Assert.ThrowsAsync<TelegramBotApiException>(() => Dispatch(provider));
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("APPROVED", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.True((await db.TelegramApprovalMessages.SingleAsync(Ct)).RetryAt > DateTimeOffset.UtcNow.AddSeconds(55));
            Assert.Equal(2, (await db.TelegramNotificationSettings.SingleAsync(Ct)).NextUpdateId);
        });
        await Dispatch(provider); // Backoff suppresses another edit attempt.
        Assert.Empty(bot.Edits);
    }

    [Fact]
    public async Task DisabledThresholdAndAtThresholdOrdersProduceNoTelegramReviews()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await EnableActions(provider);
        await SeedReview(provider, "equal", threshold: 300.5m);
        await Dispatch(provider);
        Assert.Empty(bot.ButtonMessages);
        await SeedReview(provider, "above", threshold: 300.49m);
        await Dispatch(provider);
        Assert.Single(bot.ButtonMessages);
    }

    [Fact]
    public async Task ReceiptFailureRollsBackApprovalAndAuditTogether()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        var callback = Callback(bot.ButtonMessages.Single(), "atomic", true);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_receipt BEFORE INSERT ON TelegramCallbackReceipts
                BEGIN SELECT RAISE(ABORT, 'simulated receipt failure'); END;
                """, Ct);
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>()
                .HandleCallbackAsync(generation, callback, Ct));
        });
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("PENDING", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Empty(await db.AuditLogs.ToListAsync(Ct));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_receipt", Ct);
            Assert.Contains("Approved", await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>()
                .HandleCallbackAsync(generation, callback, Ct));
        });
    }

    [Fact]
    public async Task SubmittedApprovalRemovesButtonsAndRepeatedClickDoesNotSendAgain()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        var callback = Callback(bot.ButtonMessages.Single(), "confirm", true);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>().HandleCallbackAsync(generation, callback, Ct);
            var order = await db.Orders.SingleAsync(Ct);
            var cycle = await db.Cycles.SingleAsync(Ct);
            Assert.True(await OrderApprovalService.AuthorizeAsync(db, new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId),
                order, "PLACE", order.Price, order.Quantity, "Gtc", false, Ct));
        });
        await Dispatch(provider);
        Assert.Empty(bot.Edits.Last().Buttons);
        await InScope(provider, async scope =>
        {
            await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>().HandleCallbackAsync(generation, callback with { Id = "again" }, Ct);
            Assert.Equal("SUBMITTED", (await scope.ServiceProvider.GetRequiredService<TradingDbContext>().OrderApprovals.SingleAsync(Ct)).Status);
        });
    }

    [Fact]
    public async Task ExistingTelegramConfigurationUpgradesWithActionsDisabled()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        await using var db = Database(connection);
        var service = new TelegramNotificationSettingsService(db, Protector(), new FakeBot());
        await service.SaveAsync(new(Token, "123"), Ct);
        await service.TestAndEnableAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE TelegramApprovalMessages;
            DROP TABLE TelegramCallbackReceipts;
            ALTER TABLE TelegramNotificationSettings DROP COLUMN OrderActionsEnabled;
            ALTER TABLE TelegramNotificationSettings DROP COLUMN ActionsGeneration;
            ALTER TABLE TelegramNotificationSettings DROP COLUMN VerifiedPrivateChatId;
            ALTER TABLE TelegramNotificationSettings DROP COLUMN NextUpdateId;
            ALTER TABLE TelegramNotificationSettings DROP COLUMN LastActionError;
            """, Ct);
        db.ChangeTracker.Clear();
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        var settings = await service.GetAsync(Ct);
        Assert.True(settings.Enabled);
        Assert.False(settings.OrderActionsEnabled);
        Assert.False(settings.OrderActionsReady);
        await service.SaveAsync(new(null, "123", true, "123"), Ct);
        await service.TestAndEnableAsync(Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        Assert.True((await service.GetAsync(Ct)).OrderActionsReady);
        Assert.Empty(await db.TelegramApprovalMessages.ToListAsync(Ct));
        Assert.Empty(await db.TelegramCallbackReceipts.ToListAsync(Ct));
    }

    [Fact]
    public async Task BotTransportUsesInlineButtonsAcknowledgementsAndPersistentUpdateOffset()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"id":123,"type":"private"}}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"url":""}}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":55}}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":true}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":true}"""),
            Json(HttpStatusCode.OK, """
                {"ok":true,"result":[{"update_id":42,"callback_query":{"id":"query","from":{"id":123},"data":"oa:a:id",
                "message":{"message_id":55,"chat":{"id":123,"type":"private"},"from":{"id":777}}}}]}
                """));
        using var client = new TelegramBotClient(handler);
        Assert.Equal(new TelegramChat(123, "private"), await client.GetChatAsync(Token, "123", Ct));
        Assert.False(await client.HasWebhookAsync(Token, Ct));
        Assert.Equal(55, await client.SendButtonsAsync(Token, "123", "Order", [[new("Confirm", "oa:a:id")]], Ct));
        await client.EditButtonsAsync(Token, "123", 55, "Submitted", [], Ct);
        await client.AnswerCallbackAsync(Token, "query", "Approved", Ct);
        var update = Assert.Single(await client.GetUpdatesAsync(Token, 42, Ct));
        Assert.Equal(42, update.Id);
        Assert.Equal(123, update.Callback!.SenderId);
        Assert.Equal(55, update.Callback.Message!.MessageId);
        Assert.Contains("\"inline_keyboard\":[[{\"text\":\"Confirm\",\"callback_data\":\"oa:a:id\"}]]", handler.Requests[2].Body);
        Assert.Contains("\"inline_keyboard\":[]", handler.Requests[3].Body);
        Assert.Contains("\"callback_query_id\":\"query\"", handler.Requests[4].Body);
        Assert.Contains("\"offset\":42", handler.Requests[5].Body);
        Assert.Contains("\"timeout\":20", handler.Requests[5].Body);
        using var limited = new TelegramBotClient(new QueueHandler(Json(HttpStatusCode.TooManyRequests,
            """{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":35}}""")));
        var error = await Assert.ThrowsAsync<TelegramBotApiException>(() => limited.GetUpdatesAsync(Token, 42, Ct));
        Assert.Equal(35, error.RetryAfterSeconds);
        Assert.True(error.ServiceUnavailable);
        // No second queued HTTP response: the shared token cooldown must stop this request locally.
        var blocked = await Assert.ThrowsAsync<TelegramBotApiException>(() => limited.GetChatAsync(Token, "123", Ct));
        Assert.True(blocked.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task DisableDuringVerificationCannotReenableOldConfiguration()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await EnableActions(provider);
        bot.BeforeGetChat = () => InScope(provider, async scope =>
            await scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>().DisableAsync(Ct));
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            var error = await Assert.ThrowsAsync<TradingProblemException>(() => settings.TestAndEnableAsync(Ct));
            Assert.Equal("TELEGRAM_SETTINGS_CHANGED", error.Code);
        });
        await InScope(provider, async scope => Assert.False((await scope.ServiceProvider
            .GetRequiredService<TelegramNotificationSettingsService>().GetAsync(Ct)).OrderActionsReady));
    }

    [Fact]
    public async Task SimultaneousWebAndTelegramDecisionsUseTheSameAccountGate()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        var gate = provider.GetRequiredService<ExecutionAccountOperationGate>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = gate.RunAsync("acct_paper_01", async () => { entered.SetResult(); await release.Task.WaitAsync(Ct); }, Ct);
        await entered.Task.WaitAsync(Ct);
        var telegram = InScope(provider, async scope => await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>()
            .HandleCallbackAsync(generation, Callback(bot.ButtonMessages.Single(), "concurrent", true), Ct));
        var web = InScope(provider, async scope =>
        {
            var id = await scope.ServiceProvider.GetRequiredService<TradingDbContext>().OrderApprovals.Select(x => x.Id).SingleAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<OrderApprovalService>().RejectAsync(id, Ct);
        });
        try { Assert.False(telegram.IsCompleted); Assert.False(web.IsCompleted); }
        finally { release.SetResult(); }
        await Task.WhenAll(hold, telegram, web);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal("REJECTED", (await db.OrderApprovals.SingleAsync(Ct)).Status);
            Assert.Equal(2, await db.AuditLogs.CountAsync(Ct));
            Assert.Single(await db.TelegramCallbackReceipts.ToListAsync(Ct));
        });
    }

    [Fact]
    public async Task TemporarilyPausedApprovalRegainsButtonsOnResume()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await EnableActions(provider);
        await SeedReview(provider, "review"); await Dispatch(provider);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            (await db.Cycles.SingleAsync(Ct)).OperatorPaused = true;
            await db.SaveChangesAsync(Ct);
        });
        await Dispatch(provider);
        Assert.Empty(bot.Edits.Last().Buttons);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.False((await db.TelegramApprovalMessages.SingleAsync(Ct)).Finished);
            (await db.Cycles.SingleAsync(Ct)).OperatorPaused = false;
            await db.SaveChangesAsync(Ct);
        });
        await Dispatch(provider);
        Assert.Equal(2, bot.Edits.Last().Buttons.Single().Length);
        Assert.Single(bot.ButtonMessages); // Restore the original message; do not send a duplicate.
    }

    [Fact]
    public async Task KnownCooldownDefersUnsentMessagesWithoutReplayingFailedRequests()
    {
        var handler = new QueueHandler(
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"parameters":{"retry_after":1},"description":"limited"}"""),
            Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":1}}"""));
        using var client = new TelegramBotClient(handler);
        // The request rejected by Telegram is still attempted only once.
        await Assert.ThrowsAsync<TelegramBotApiException>(() => client.SendMessageAsync(Token, "123", "first", Ct));
        Assert.Single(handler.Requests);
        // A different queued destination bypasses the first chat's pacing lane, but must wait for the bot cooldown.
        await client.SendButtonsAsync(Token, "456", "second", [[new("Confirm", "oa:a:pending")]], Ct);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("second", handler.Requests[1].Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ActionsRequireExplicitDestinationEvenWithPrivateNotificationChat(string? chat)
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        await using var db = Database(connection);
        var service = new TelegramNotificationSettingsService(db, Protector(), new FakeBot());
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => service.SaveAsync(new(Token, "123", true, chat), Ct));
        Assert.Equal("TELEGRAM_ORDER_ACTIONS_CHAT_REQUIRED", error.Code);
        Assert.Empty(await db.TelegramNotificationSettings.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("-100123", 2)]
    [InlineData("456", 2)]
    [InlineData("123", 1)]
    public async Task IndependentDestinationRoutesNotificationsAndApprovalsSeparately(string notificationChat, int testMessageCount)
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var generation = await EnableActions(provider, notificationChat, "123");
        Assert.Equal(testMessageCount, bot.Messages.Count);
        Assert.Contains(bot.Messages, x => x.ChatId == notificationChat && x.Text.Contains("notifications"));
        Assert.Contains(bot.Messages, x => x.ChatId == "123" && x.Text.Contains("order actions"));
        await SeedReview(provider, "review"); await Dispatch(provider);
        Assert.Equal("123", Assert.Single(bot.ButtonMessages).ChatId);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.RiskAlerts.Add(Alert("separate-destination", "INFO", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
            var dto = await scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>().GetAsync(Ct);
            Assert.Equal(notificationChat, dto.ChatId);
            Assert.Equal("123", dto.OrderActionsChatId);
        });
        await Dispatcher(provider, bot).ProcessNextAsync(Ct);
        Assert.Equal(notificationChat, bot.Messages.Last().ChatId);
        var before = bot.ButtonMessages.Count;
        await Worker(provider, bot).ProcessUpdateAsync(generation, Token, new(1, new(456, "private", 456, 1, "/pending"), null), Ct);
        Assert.Equal(before, bot.ButtonMessages.Count);
        await Worker(provider, bot).ProcessUpdateAsync(generation, Token, new(2, new(123, "private", 123, 2, "/pending"), null), Ct);
        Assert.True(bot.ButtonMessages.Count > before);
        Assert.All(bot.ButtonMessages, x => Assert.Equal("123", x.ChatId));
    }

    [Fact]
    public async Task FailedActionDestinationTestDoesNotEnableEitherDestination()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        await using var db = Database(connection);
        var bot = new FakeBot { FailMessageChatId = "123" };
        var service = new TelegramNotificationSettingsService(db, Protector(), bot);
        await service.SaveAsync(new(Token, "-100123", true, "123"), Ct);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => service.TestAndEnableAsync(Ct));
        Assert.Equal("TELEGRAM_VERIFICATION_FAILED", error.Code);
        Assert.Equal("-100123", Assert.Single(bot.Messages).ChatId);
        var settings = await service.GetAsync(Ct);
        Assert.False(settings.Enabled);
        Assert.False(settings.OrderActionsReady);
        Assert.Null((await db.TelegramNotificationSettings.SingleAsync(Ct)).VerifiedPrivateChatId);
    }

    [Fact]
    public async Task ChangingOnlyActionDestinationInvalidatesOldButtonsAndPersistsNewTarget()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        var oldGeneration = await EnableActions(provider, "-100123");
        await SeedReview(provider, "review"); await Dispatch(provider);
        var oldCallback = Callback(bot.ButtonMessages.Single(), "old-target", true);
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            var saved = await settings.SaveAsync(new(null, "-100123", true, " 456 "), Ct);
            Assert.Equal("456", saved.OrderActionsChatId);
            Assert.False(saved.OrderActionsReady);
            Assert.False(saved.Enabled);
            var actions = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            Assert.Contains("unavailable", await actions.HandleCallbackAsync(oldGeneration, oldCallback, Ct));
        });
        string generation = "";
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            Assert.Equal("456", (await settings.GetAsync(Ct)).OrderActionsChatId);
            Assert.True((await settings.TestAndEnableAsync(Ct)).OrderActionsReady);
            var actions = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
            generation = (await actions.SettingsAsync(Ct))!.ActionsGeneration;
            Assert.Contains("unavailable", await actions.HandleCallbackAsync(generation, oldCallback, Ct));
        });
        await Dispatch(provider);
        var newMessage = bot.ButtonMessages.Last();
        Assert.Equal("456", newMessage.ChatId);
        var callback = Callback(newMessage, "new-target", true);
        callback = callback with { SenderId = 456, Message = callback.Message! with { ChatId = 456 } };
        await InScope(provider, async scope => Assert.Contains("Approved", await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>()
            .HandleCallbackAsync(generation, callback, Ct)));
    }

    [Theory]
    [InlineData(true, "123", "123")]
    [InlineData(false, "123", "")]
    [InlineData(true, null, "")]
    public async Task UpgradeCopiesOnlyPreviouslyVerifiedEnabledActionDestination(bool enabled, string? verified, string expected)
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        await using var db = Database(connection);
        db.TelegramNotificationSettings.Add(new() { EncryptedBotToken = "not-used", ChatId = "-100123", OrderActionsEnabled = enabled,
            VerifiedPrivateChatId = verified, Enabled = enabled, VerifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE TelegramNotificationSettings DROP COLUMN OrderActionsChatId", Ct);
        db.ChangeTracker.Clear();
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        var settings = await db.TelegramNotificationSettings.SingleAsync(Ct);
        Assert.Equal(expected, settings.OrderActionsChatId);
        settings.OrderActionsChatId = "789";
        await db.SaveChangesAsync(Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        db.ChangeTracker.Clear();
        Assert.Equal("789", (await db.TelegramNotificationSettings.SingleAsync(Ct)).OrderActionsChatId);
    }

    [Fact]
    public async Task FailedDestinationBackfillRollsBackColumnAndRetriesOnNextStartup()
    {
        await using var connection = await OpenDatabaseAsync(Ct);
        await using var db = Database(connection);
        db.TelegramNotificationSettings.Add(new() { EncryptedBotToken = "not-used", ChatId = "123",
            OrderActionsEnabled = true, VerifiedPrivateChatId = "123", Enabled = true, VerifiedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE TelegramNotificationSettings DROP COLUMN OrderActionsChatId", Ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailDestinationBackfill BEFORE UPDATE ON TelegramNotificationSettings
            BEGIN SELECT RAISE(ABORT, 'Simulated upgrade interruption'); END;
            """, Ct);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => DatabaseCompatibility.EnsureExecutionSchemaAsync(db));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailDestinationBackfill", Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        Assert.Equal("123", (await db.TelegramNotificationSettings.SingleAsync(Ct)).OrderActionsChatId);
    }

    private static async Task<string> EnableActions(ServiceProvider provider, string notificationChatId = "123", string actionsChatId = "123")
    {
        string generation = "";
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new(Token, notificationChatId, true, actionsChatId), Ct);
            await settings.TestAndEnableAsync(Ct);
            generation = (await scope.ServiceProvider.GetRequiredService<TradingDbContext>().TelegramNotificationSettings.SingleAsync(Ct)).ActionsGeneration;
        });
        return generation;
    }

    private static Task SeedReview(ServiceProvider provider, string id, string kind = "ENTRY", string action = "PLACE", decimal threshold = 0m) =>
        InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var cycle = new CycleEntity { Id = "cycle-" + id, StrategyId = "strategy-" + id, State = kind == "FLATTEN" ? "CLOSING" : "RUNNING",
                FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "" };
            var order = new OrderEntity { Id = id, ClientOrderId = id, CycleId = cycle.Id, ExchangeOrderId = action == "AMEND" ? "1234" : "pending",
                Symbol = "SOLUSDT", Side = "BUY", Kind = kind, Status = action == "AMEND" ? "NEW" : "PENDING_EXCHANGE", Price = 150.25m, Quantity = 2m };
            db.AddRange(cycle, order, new StrategyEntity { Id = cycle.StrategyId, Name = "Telegram grid", Symbol = "SOLUSDT", ConfigurationJson = "{}" });
            await db.SaveChangesAsync(Ct);
            await new TradingControlSettingsService(db).SaveAsync(new(true, threshold), Ct);
            await OrderApprovalService.AuthorizeAsync(db, new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId), order,
                action, order.Price, order.Quantity, action == "FLATTEN" ? "Ioc" : "Gtc", kind != "ENTRY", Ct);
        });

    private static Task Dispatch(ServiceProvider provider) => InScope(provider, async scope =>
        await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>().DispatchNextAsync(Ct));
    private static TelegramOrderActionWorker Worker(ServiceProvider provider, FakeBot bot) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<CredentialProtector>(), bot,
        NullLogger<TelegramOrderActionWorker>.Instance);
    private static TelegramCallback Callback(ButtonMessage message, string id, bool approve) =>
        new(id, message.Buttons.SelectMany(x => x).Single(x => x.Data.StartsWith(approve ? "oa:a:" : "oa:r:")).Data,
            123, new(123, "private", 777, message.Id, null));

    private sealed record ButtonMessage(long Id, string ChatId, string Text, TelegramButton[][] Buttons);
    private sealed partial class FakeBot
    {
        public bool HasWebhook { get; set; }
        public Func<Task>? BeforeGetChat { get; set; }
        public TelegramBotApiException? ButtonFailure { get; set; }
        public TelegramBotApiException? EditFailure { get; set; }
        public List<ButtonMessage> ButtonMessages { get; } = [];
        public List<ButtonMessage> Edits { get; } = [];
        public List<(string Id, string Text)> Answers { get; } = [];
        public async Task<TelegramChat> GetChatAsync(string botToken, string chatId, CancellationToken ct)
        {
            if (BeforeGetChat is not null) await BeforeGetChat();
            return new TelegramChat(long.Parse(chatId), chatId.StartsWith('-') ? "group" : "private");
        }
        public Task<bool> HasWebhookAsync(string botToken, CancellationToken ct) => Task.FromResult(HasWebhook);
        public Task<long> SendButtonsAsync(string botToken, string chatId, string text, TelegramButton[][] buttons, CancellationToken ct)
        {
            if (ButtonFailure is not null) throw ButtonFailure;
            var id = ButtonMessages.Count + 1;
            ButtonMessages.Add(new(id, chatId, text, buttons));
            return Task.FromResult((long)id);
        }
        public Task EditButtonsAsync(string botToken, string chatId, long messageId, string text, TelegramButton[][] buttons, CancellationToken ct)
        {
            if (EditFailure is not null) throw EditFailure;
            Edits.Add(new(messageId, chatId, text, buttons));
            return Task.CompletedTask;
        }
        public Task AnswerCallbackAsync(string botToken, string callbackId, string text, CancellationToken ct)
        {
            Answers.Add((callbackId, text));
            return Task.CompletedTask;
        }
        public Task<TelegramUpdate[]> GetUpdatesAsync(string botToken, long offset, CancellationToken ct) => Task.FromResult<TelegramUpdate[]>([]);
    }
}
