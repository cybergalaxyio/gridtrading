using System.Globalization;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class TelegramOrderActionService(
    TradingDbContext db, ExecutionAccountOperationGate gate, OrderApprovalService approvals,
    CredentialProtector protector, ITelegramBotClient bot)
{
    public Task<TelegramNotificationSettingsEntity?> SettingsAsync(CancellationToken ct) =>
        db.TelegramNotificationSettings.AsNoTracking().SingleOrDefaultAsync(ct);

    public static bool Authorized(TelegramNotificationSettingsEntity? settings, string generation,
        TelegramIncomingMessage? message, long senderId) =>
        TelegramNotificationSettingsService.ActionsReady(settings) && settings!.ActionsGeneration == generation &&
        message is { ChatType: "private" } &&
        message.ChatId.ToString(CultureInfo.InvariantCulture) == settings.VerifiedPrivateChatId &&
        senderId.ToString(CultureInfo.InvariantCulture) == settings.VerifiedPrivateChatId;

    public async Task<string> HandleCallbackAsync(string generation, TelegramCallback callback, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (!Authorized(settings, generation, callback.Message, callback.SenderId)) return "Order actions are unavailable in this chat.";
        var parts = callback.Data?.Split(':');
        if (parts is not ["oa", "a" or "r", _]) return "Unknown order action.";
        var delivery = await db.TelegramApprovalMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parts[2], ct);
        if (delivery is null || delivery.Generation != generation || delivery.ChatId != settings!.VerifiedPrivateChatId ||
            delivery.MessageId is null || delivery.MessageId != callback.Message!.MessageId)
            return "This order button is no longer valid. Use /pending.";
        var accountId = await db.OrderApprovals.Where(x => x.Id == delivery.ApprovalId)
            .Select(x => x.ExecutionAccountId).SingleOrDefaultAsync(ct);
        if (accountId is null) return "This order is no longer available.";
        return await gate.RunAsync(accountId, async () =>
        {
            // The configuration check, decision, audit and replay receipt commit together.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            settings = await SettingsAsync(ct);
            if (!Authorized(settings, generation, callback.Message, callback.SenderId)) return "Order actions have been disabled or changed.";
            var receipt = await db.TelegramCallbackReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == callback.Id, ct);
            if (receipt is not null) return receipt.Result;
            string result;
            try
            {
                await approvals.DecideCoreAsync(delivery.ApprovalId, parts[1] == "a", $"telegram:{callback.SenderId}", ct);
                var state = await db.OrderApprovals.Where(x => x.Id == delivery.ApprovalId).Select(x => x.Status).SingleAsync(ct);
                result = StatusText(state);
            }
            catch (TradingProblemException ex) when (ex.Status is 404 or 409)
            {
                result = "This order changed or was already sent. Use /pending.";
            }
            db.TelegramCallbackReceipts.Add(new() { Id = callback.Id, Generation = generation, Result = result, ProcessedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, ct);
    }

    public async Task<bool> DispatchNextAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (!TelegramNotificationSettingsService.ActionsReady(settings)) return false;
        var token = protector.UnprotectTelegramBotToken(settings!.EncryptedBotToken);
        // Synchronize already delivered messages before sending the next new approval.
        var messages = await db.TelegramApprovalMessages.Where(x => x.Generation == settings.ActionsGeneration &&
            x.MessageId != null && !x.Finished).ToListAsync(ct);
        foreach (var message in messages)
        {
            if (message.RetryAt > DateTimeOffset.UtcNow) continue;
            var view = await ViewAsync(message.ApprovalId, ct);
            if (message.RenderedStatus == view.Status) continue;
            if (!await StillEnabledAsync(settings.ActionsGeneration, ct)) return false;
            try
            {
                await bot.EditButtonsAsync(token, message.ChatId, message.MessageId!.Value, view.Text,
                    Buttons(message.Id, view.Status), ct);
                MarkRendered(message, view.Status);
            }
            catch (TelegramBotApiException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                MarkRendered(message, view.Status);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                message.Error = SafeError(ex);
                message.RetryAt = DateTimeOffset.UtcNow.AddSeconds(ex is TelegramBotApiException { RetryAfterSeconds: { } delay } ? delay : 30);
                // Deleted or otherwise uneditable messages cannot be repaired automatically.
                if (ex is TelegramBotApiException { ServiceUnavailable: false }) message.Finished = true;
                await db.SaveChangesAsync(ct);
                throw;
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        var ids = await CurrentIdsAsync(ct);
        var attempted = await db.TelegramApprovalMessages.Where(x => x.Generation == settings.ActionsGeneration)
            .Select(x => x.ApprovalId).ToListAsync(ct);
        foreach (var id in ids.Except(attempted))
        {
            var view = await ViewAsync(id, ct);
            if (view.Status != "PENDING") continue;
            await SendReviewAsync(settings, id, view, token, ct);
            return true;
        }
        return false;
    }

    public async Task SendPendingPageAsync(string generation, TelegramIncomingMessage message, long senderId, int page, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (!Authorized(settings, generation, message, senderId)) return;
        var ids = await CurrentIdsAsync(ct);
        var pages = Math.Max(1, (ids.Length + 9) / 10);
        page = Math.Clamp(page, 0, pages - 1);
        var token = protector.UnprotectTelegramBotToken(settings!.EncryptedBotToken);
        foreach (var id in ids.Skip(page * 10).Take(10))
        {
            if (!await StillEnabledAsync(generation, ct)) return;
            var view = await ViewAsync(id, ct);
            if (view.Status is not ("PENDING" or "APPROVED" or "REJECTED")) continue;
            await SendReviewAsync(settings, id, view, token, ct);
        }
        var navigation = new List<TelegramButton>();
        if (page > 0) navigation.Add(new("← Previous", $"op:{generation}:{page - 1}"));
        if (page + 1 < pages) navigation.Add(new("Next →", $"op:{generation}:{page + 1}"));
        if (await StillEnabledAsync(generation, ct))
            await bot.SendButtonsAsync(token, settings.VerifiedPrivateChatId!,
                ids.Length == 0 ? "No orders awaiting review." : $"Order reviews: {ids.Length} · Page {page + 1}/{pages}\nUse /pending to refresh.",
                navigation.Count == 0 ? [] : [navigation.ToArray()], ct);
    }

    private async Task SendReviewAsync(TelegramNotificationSettingsEntity settings, string id, Review view, string token, CancellationToken ct)
    {
        if (!await StillEnabledAsync(settings.ActionsGeneration, ct)) return;
        var delivery = new TelegramApprovalMessageEntity { ApprovalId = id, Generation = settings.ActionsGeneration,
            ChatId = settings.VerifiedPrivateChatId!, AttemptedAt = DateTimeOffset.UtcNow };
        db.TelegramApprovalMessages.Add(delivery);
        await db.SaveChangesAsync(ct); // Claim before I/O: an uncertain send is not blindly repeated.
        try
        {
            delivery.MessageId = await bot.SendButtonsAsync(token, delivery.ChatId, view.Text, Buttons(delivery.Id, view.Status), ct);
            MarkRendered(delivery, view.Status);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            delivery.Error = SafeError(ex);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private Task<bool> StillEnabledAsync(string generation, CancellationToken ct) => db.TelegramNotificationSettings.AsNoTracking()
        .AnyAsync(x => x.Enabled && x.OrderActionsEnabled && x.VerifiedAt != null && x.VerifiedPrivateChatId != null && x.ActionsGeneration == generation, ct);

    private async Task<string[]> CurrentIdsAsync(CancellationToken ct)
    {
        var rows = await (from approval in db.OrderApprovals.AsNoTracking()
            join order in db.Orders.AsNoTracking() on approval.OrderId equals order.Id
            join cycle in db.Cycles.AsNoTracking() on approval.CycleId equals cycle.Id
            where approval.Status == "PENDING" || approval.Status == "APPROVED" || approval.Status == "REJECTED"
            select new { approval, order, cycle }).ToListAsync(ct);
        return rows.Where(x => OrderApprovalService.Current(x.approval, x.order, x.cycle))
            .OrderBy(x => x.approval.CreatedAt).ThenBy(x => x.approval.Id).Select(x => x.approval.Id).ToArray();
    }

    private sealed record Review(string Status, string Text);
    private async Task<Review> ViewAsync(string id, CancellationToken ct)
    {
        var approval = await db.OrderApprovals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (approval is null) return new("STALE", "Order no longer available. Use /pending.");
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == approval.OrderId, ct);
        var cycle = await db.Cycles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == approval.CycleId, ct);
        var status = approval.Status;
        if (status is "PENDING" or "APPROVED" or "REJECTED" &&
            (order is null || cycle is null || !OrderApprovalService.Current(approval, order, cycle)))
            // Pauses and recovery states may end without replacing the approval. Keep syncing its message.
            status = order is not null && cycle is { IsTerminal: false } ? "UNAVAILABLE" : "STALE";
        var strategy = cycle is null ? null : await db.Strategies.Where(x => x.Id == cycle.StrategyId).Select(x => x.Name).SingleOrDefaultAsync(ct);
        var account = await db.HyperliquidAccounts.Where(x => x.Id == approval.ExecutionAccountId).Select(x => x.Name).SingleOrDefaultAsync(ct);
        var text = $"Order confirmation / 订单确认\n{StatusText(status)}\n" +
            $"Account: {Label(account ?? approval.ExecutionAccountId)}\nEnvironment: {Label(approval.ExecutionEnvironmentId)}\n" +
            $"Strategy: {Label(strategy ?? cycle?.StrategyId ?? "—")}\nSymbol: {Label(approval.Symbol)}\nSide: {approval.Side}\n" +
            $"Action: {approval.Action} / {approval.Kind}\nPrice: {Number(approval.Price)}\nQuantity: {Number(approval.Quantity)}\n" +
            $"Order value: {Number(approval.Price * approval.Quantity)} (quote currency)\n" +
            $"Time in force: {approval.TimeInForce}\nReduce only: {(approval.ReduceOnly ? "YES" : "NO")}\nOrder: {approval.OrderId}";
        return new(status, text);
    }

    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private static string Label(string value) => new(value.Where(x => !char.IsControl(x)).Take(120).ToArray());
    internal static string SafeError(Exception ex) => ex is TelegramBotApiException ? ex.Message : $"Telegram order actions failed ({ex.GetType().Name}).";
    private static string StatusText(string status) => status switch
    {
        "PENDING" => "Pending confirmation—NOT SENT",
        "APPROVED" => "Approved—waiting for submission",
        "REJECTED" => "Rejected—NOT SENT",
        "SUBMITTED" => "Approval consumed—check order status in the dashboard",
        "UNAVAILABLE" => "Order is not currently actionable—waiting for strategy state to update",
        _ => "Order changed or cancelled—buttons no longer active"
    };
    private static TelegramButton[][] Buttons(string deliveryId, string status) => status switch
    {
        "PENDING" => [[new("Confirm / 确认", $"oa:a:{deliveryId}"), new("Reject / 拒绝", $"oa:r:{deliveryId}")]],
        "REJECTED" => [[new("Confirm / 确认", $"oa:a:{deliveryId}")]],
        "APPROVED" => [[new("Reject / 拒绝", $"oa:r:{deliveryId}")]],
        _ => []
    };
    private static void MarkRendered(TelegramApprovalMessageEntity message, string status)
    {
        message.RenderedStatus = status;
        message.Finished = status is not ("PENDING" or "APPROVED" or "REJECTED" or "UNAVAILABLE");
        message.Error = null;
        message.RetryAt = null;
    }
}
