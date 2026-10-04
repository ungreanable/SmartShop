using System.Net;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartShop.Contracts.Identity;
using SmartShop.Modules.Notifications.Data;
using SmartShop.Modules.Notifications.Domain;
using SmartShop.Modules.Notifications.Services;
using WebPush;
using Notification = SmartShop.Modules.Notifications.Domain.Notification;
using NotificationPriority = SmartShop.Modules.Notifications.Domain.NotificationPriority;

namespace SmartShop.Modules.Notifications.Handlers;

public static class SendLinePushHandler
{
    public static async Task Handle(SendLinePush command, NotificationsDbContext db, IUserDirectory users, ILineMessagingClient line,
        NotificationDispatcher dispatcher, IOptions<NotificationOptions> options, TimeProvider clock, ILogger<SendLinePush> logger, CancellationToken ct)
    {
        var (notification, delivery) = await Load(db, command.NotificationId, Channel.Line, ct);
        if (notification is null || delivery is not { Status: DeliveryStatus.Pending }) return;
        var now = clock.GetUtcNow();

        var lineUserId = (await users.GetLineUserIdsAsync([notification.UserId], ct)).GetValueOrDefault(notification.UserId);
        if (lineUserId is null)
        {
            delivery.Skip("no_line_account", now);
            await dispatcher.FallbackToPushAsync(notification, ct);
            return;
        }

        // Quota guard: keep the last messages of the month for high-priority notifications.
        var month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var usage = await db.LineQuota.FirstOrDefaultAsync(q => q.Month == month, ct);
        var o = options.Value;
        var remaining = o.LineMonthlyQuota - (usage?.Sent ?? 0);
        if (usage?.Exhausted == true || remaining <= 0
            || remaining <= o.LineReserveForHighPriority && notification.Priority != NotificationPriority.High)
        {
            delivery.Skip("line_quota", now);
            await dispatcher.FallbackToPushAsync(notification, ct);
            return;
        }

        var message = FlexMessages.Bubble(notification.Title, notification.Body, o.AbsoluteLink(notification.Link),
            notification.Priority == NotificationPriority.High);
        var result = await line.PushAsync(lineUserId, message, delivery.Id, ct);
        switch (result)
        {
            case LinePushResult.Sent:
                delivery.Succeeded(now);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO notifications.line_quota (month, sent, exhausted) VALUES ({month}, 1, false)
                    ON CONFLICT (month) DO UPDATE SET sent = notifications.line_quota.sent + 1
                    """, ct);
                break;
            case LinePushResult.NotFriend:
                delivery.Failed("not_friend", now);
                var friendship = await db.LineFriendships.FirstOrDefaultAsync(f => f.UserId == notification.UserId, ct);
                friendship?.Set(false, now);
                await dispatcher.FallbackToPushAsync(notification, ct);
                break;
            case LinePushResult.QuotaExceeded:
                delivery.Failed("line_quota_exceeded", now);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO notifications.line_quota (month, sent, exhausted) VALUES ({month}, 0, true)
                    ON CONFLICT (month) DO UPDATE SET exhausted = true
                    """, ct);
                logger.LogWarning("LINE monthly message quota is exhausted; falling back to device push.");
                await dispatcher.FallbackToPushAsync(notification, ct);
                break;
            default:
                delivery.Failed("line_invalid_request", now);
                await dispatcher.FallbackToPushAsync(notification, ct);
                break;
        }
    }

    internal static async Task<(Notification? Notification, NotificationDelivery? Delivery)> Load(
        NotificationsDbContext db, Guid notificationId, Channel channel, CancellationToken ct)
    {
        var notification = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notificationId, ct);
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.NotificationId == notificationId && d.Channel == channel, ct);
        return (notification, delivery);
    }
}

public static class SendWebPushHandler
{
    private static readonly WebPushClient Client = new();

    public static async Task Handle(SendWebPush command, NotificationsDbContext db, IOptions<NotificationOptions> options, TimeProvider clock,
        ILogger<SendWebPush> logger, CancellationToken ct)
    {
        var (notification, delivery) = await SendLinePushHandler.Load(db, command.NotificationId, Channel.WebPush, ct);
        if (notification is null || delivery is not { Status: DeliveryStatus.Pending }) return;
        var o = options.Value;
        var now = clock.GetUtcNow();
        var vapid = new VapidDetails(o.VapidSubject ?? "mailto:admin@example.com", o.VapidPublicKey, o.VapidPrivateKey);
        var payload = JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.Body,
            url = notification.Link ?? "/",
            tag = notification.Type,
            urgent = notification.Priority == NotificationPriority.High,
        });

        var devices = await db.Devices.Where(d => d.UserId == notification.UserId && d.Kind == DeviceKind.WebPush).ToListAsync(ct);
        var sent = 0;
        foreach (var device in devices)
        {
            try
            {
                await Client.SendNotificationAsync(new PushSubscription(device.Endpoint, device.P256dh, device.Auth), payload, new Dictionary<string, object>
                {
                    ["vapidDetails"] = vapid,
                    ["TTL"] = notification.Priority == NotificationPriority.High ? 3600 : 86400,
                    ["urgency"] = notification.Priority == NotificationPriority.High ? "high" : "normal",
                }, ct);
                sent++;
            }
            catch (WebPushException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                db.Devices.Remove(device); // subscription expired or revoked
            }
            catch (WebPushException e)
            {
                logger.LogWarning(e, "Web push to device {Device} failed with {Status}", device.Id, e.StatusCode);
            }
        }

        if (sent > 0) delivery.Succeeded(now);
        else delivery.Failed(devices.Count == 0 ? "no_devices" : "all_devices_failed", now);
    }
}

public static class SendMobilePushHandler
{
    private static readonly Lock Gate = new();
    private static FirebaseMessaging? _messaging;

    public static async Task Handle(SendMobilePush command, NotificationsDbContext db, IOptions<NotificationOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var (notification, delivery) = await SendLinePushHandler.Load(db, command.NotificationId, Channel.Fcm, ct);
        if (notification is null || delivery is not { Status: DeliveryStatus.Pending }) return;
        var now = clock.GetUtcNow();
        var messaging = Messaging(options.Value);
        if (messaging is null)
        {
            delivery.Skip("fcm_not_configured", now);
            return;
        }

        var devices = await db.Devices.Where(d => d.UserId == notification.UserId && d.Kind == DeviceKind.Fcm).ToListAsync(ct);
        if (devices.Count == 0)
        {
            delivery.Failed("no_devices", now);
            return;
        }

        var response = await messaging.SendEachForMulticastAsync(new MulticastMessage
        {
            Tokens = devices.Select(d => d.Endpoint).ToList(),
            Notification = new FirebaseAdmin.Messaging.Notification { Title = notification.Title, Body = notification.Body },
            Data = new Dictionary<string, string> { ["url"] = notification.Link ?? "/", ["type"] = notification.Type },
            Android = new AndroidConfig { Priority = notification.Priority == NotificationPriority.High ? Priority.High : Priority.Normal },
        }, ct);

        for (var i = 0; i < response.Responses.Count; i++)
        {
            if (response.Responses[i].Exception?.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                db.Devices.Remove(devices[i]);
        }
        if (response.SuccessCount > 0) delivery.Succeeded(now);
        else delivery.Failed("all_devices_failed", now);
    }

    private static FirebaseMessaging? Messaging(NotificationOptions options)
    {
        if (!options.FcmConfigured) return null;
        lock (Gate)
        {
            if (_messaging is not null) return _messaging;
            var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromJson<ServiceAccountCredential>(options.FirebaseCredentialsJson).ToGoogleCredential(),
            });
            return _messaging = FirebaseMessaging.GetMessaging(app);
        }
    }
}
