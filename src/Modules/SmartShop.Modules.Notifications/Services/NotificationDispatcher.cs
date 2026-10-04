using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartShop.Infrastructure.Realtime;
using SmartShop.Modules.Notifications.Data;
using SmartShop.Modules.Notifications.Domain;
using SmartShop.SharedKernel.Scheduling;
using Wolverine;

namespace SmartShop.Modules.Notifications.Services;

public sealed record NotificationTemplate(
    string Type, NotificationPriority Priority, string Title, string Body, string? Link, bool ExemptFromQuietHours = false);

// Internal, process-local commands (not IAsyncMessage): one per channel so each retries independently.
public sealed record SendLinePush(Guid NotificationId);
public sealed record SendWebPush(Guid NotificationId);
public sealed record SendMobilePush(Guid NotificationId);

/// <summary>
/// Fan-out: one inbox entry per recipient (always), a real-time push to every open session, then LINE and/or
/// device push depending on the user's channels and preferences. LINE failures fall back to Web Push / FCM.
/// Must run inside a Wolverine handler so the channel commands are part of the same transaction (outbox).
/// </summary>
public sealed class NotificationDispatcher(
    NotificationsDbContext db, IMessageBus bus, IRealtimePublisher realtime, IOptions<NotificationOptions> options, TimeProvider clock)
{
    private readonly NotificationOptions _options = options.Value;

    public async Task NotifyAsync(IEnumerable<Guid> recipients, Guid? plantId, Guid sourceEventId, NotificationTemplate template, CancellationToken ct)
    {
        var ids = recipients.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;

        var already = await db.Notifications.Where(n => n.SourceEventId == sourceEventId && ids.Contains(n.UserId))
            .Select(n => n.UserId).ToListAsync(ct);
        ids = ids.Except(already).ToList();
        if (ids.Count == 0) return;

        var settings = await db.Settings.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);
        var friends = await db.LineFriendships.AsNoTracking().Where(f => ids.Contains(f.UserId) && f.IsFriend).Select(f => f.UserId).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.UserId))
            .GroupBy(d => new { d.UserId, d.Kind }).Select(g => new { g.Key.UserId, g.Key.Kind }).ToListAsync(ct);

        var now = clock.GetUtcNow();
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ShopScheduleEvaluator.ResolveTimeZone(_options.TimeZoneId)).DateTime);

        foreach (var userId in ids)
        {
            var notification = new Notification(userId, plantId, template.Type, template.Priority, template.Title, template.Body,
                template.Link, sourceEventId, now);
            db.Notifications.Add(notification);

            var s = settings.GetValueOrDefault(userId) ?? new NotificationSettings(userId);
            var quiet = !template.ExemptFromQuietHours && s.InQuietHours(localTime);
            var line = !quiet && _options.LineConfigured && friends.Contains(userId)
                       && template.Priority != NotificationPriority.Low && !s.LineMuted.Contains(template.Type);
            var push = !quiet && !s.PushMuted.Contains(template.Type) && (!line || s.PushMode == PushMode.Always);

            if (line) await QueueAsync(notification, Channel.Line, new SendLinePush(notification.Id), now);
            if (push && _options.WebPushConfigured && devices.Any(d => d.UserId == userId && d.Kind == DeviceKind.WebPush))
                await QueueAsync(notification, Channel.WebPush, new SendWebPush(notification.Id), now);
            if (push && _options.FcmConfigured && devices.Any(d => d.UserId == userId && d.Kind == DeviceKind.Fcm))
                await QueueAsync(notification, Channel.Fcm, new SendMobilePush(notification.Id), now);
        }

        await realtime.ToUsersAsync(ids, RealtimeEvents.Notification, new
        {
            type = template.Type,
            title = template.Title,
            body = template.Body,
            link = template.Link,
            priority = template.Priority.ToString(),
            urgent = template.Priority == NotificationPriority.High,
        }, ct);
    }

    /// <summary>Used by the LINE sender when LINE could not deliver: try device push instead.</summary>
    public async Task FallbackToPushAsync(Notification notification, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var kinds = await db.Devices.AsNoTracking().Where(d => d.UserId == notification.UserId).Select(d => d.Kind).Distinct().ToListAsync(ct);
        var existing = await db.Deliveries.AsNoTracking().Where(d => d.NotificationId == notification.Id).Select(d => d.Channel).ToListAsync(ct);
        if (_options.WebPushConfigured && kinds.Contains(DeviceKind.WebPush) && !existing.Contains(Channel.WebPush))
            await QueueAsync(notification, Channel.WebPush, new SendWebPush(notification.Id), now);
        if (_options.FcmConfigured && kinds.Contains(DeviceKind.Fcm) && !existing.Contains(Channel.Fcm))
            await QueueAsync(notification, Channel.Fcm, new SendMobilePush(notification.Id), now);
    }

    private async Task QueueAsync(Notification notification, Channel channel, object command, DateTimeOffset now)
    {
        db.Deliveries.Add(new NotificationDelivery(notification.Id, notification.UserId, channel, now));
        await bus.PublishAsync(command);
    }
}
