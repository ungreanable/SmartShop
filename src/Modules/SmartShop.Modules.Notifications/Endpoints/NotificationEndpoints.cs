using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Notifications;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Caching;
using SmartShop.Modules.Notifications.Data;
using SmartShop.Modules.Notifications.Domain;
using SmartShop.Modules.Notifications.Services;
using SmartShop.SharedKernel;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Notifications.Endpoints;

public sealed record NotificationDto(Guid Id, Guid? PlantId, string Type, NotificationPriority Priority, string Title, string Body, string? Link,
    DateTimeOffset CreatedAt, bool Read);
public sealed record DeviceRequest(DeviceKind Kind, string Endpoint, string? P256dh, string? Auth, string? Label);
/// <summary><paramref name="Key"/>: short hash of the push endpoint, so a browser can tell which listed device it is.</summary>
public sealed record DeviceDto(Guid Id, DeviceKind Kind, string? Label, DateTimeOffset LastSeenAt, string Key)
{
    public static string KeyOf(string endpoint) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint)))[..16];
}
public sealed record ChannelsDto(bool LineConfigured, bool LineFriend, string? LineAddFriendUrl, bool WebPushConfigured, string? VapidPublicKey,
    bool FcmConfigured, List<DeviceDto> Devices, bool HasPush);
public sealed record SettingsDto(PushMode PushMode, List<string> LineMuted, List<string> PushMuted, TimeOnly? QuietFrom, TimeOnly? QuietTo);
public sealed record QuotaDto(string Month, int Sent, int Quota, bool Exhausted);

/// <summary>Sends a test notification through every channel of the user (handled by the worker).</summary>
public sealed record SendTestNotification(Guid UserId) : IScheduledCommand;

internal static class NotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var n = api.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();

        n.MapGet("/", async (int? page, bool? unreadOnly, ICurrentUser user, NotificationsDbContext db, CancellationToken ct) =>
        {
            var size = 30;
            var number = Math.Max(1, page ?? 1);
            var query = db.Notifications.AsNoTracking().Where(x => x.UserId == user.Id);
            if (unreadOnly == true) query = query.Where(x => x.ReadAt == null);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAt).Skip((number - 1) * size).Take(size)
                .Select(x => new NotificationDto(x.Id, x.PlantId, x.Type, x.Priority, x.Title, x.Body, x.Link, x.CreatedAt, x.ReadAt != null))
                .ToListAsync(ct);
            return new PagedResult<NotificationDto>(items, total, number, size);
        });

        n.MapGet("/unread-count", async (ICurrentUser user, NotificationsDbContext db, CancellationToken ct) =>
            new { count = await db.Notifications.CountAsync(x => x.UserId == user.Id && x.ReadAt == null, ct) });

        n.MapPost("/{id:guid}/read", async (Guid id, ICurrentUser user, NotificationsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            await db.Notifications.Where(x => x.Id == id && x.UserId == user.Id && x.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
            return Results.NoContent();
        });

        n.MapPost("/read-all", async (ICurrentUser user, NotificationsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            await db.Notifications.Where(x => x.UserId == user.Id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
            return Results.NoContent();
        });

        n.MapGet("/settings", async (ICurrentUser user, NotificationsDbContext db, CancellationToken ct) =>
        {
            var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Id, ct) ?? new NotificationSettings(user.Id);
            return new SettingsDto(s.PushMode, s.LineMuted, s.PushMuted, s.QuietFrom, s.QuietTo);
        });

        n.MapPut("/settings", async (SettingsDto req, ICurrentUser user, NotificationsDbContext db, CancellationToken ct) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync(x => x.UserId == user.Id, ct);
            if (s is null) db.Settings.Add(s = new NotificationSettings(user.Id));
            s.Update(req.PushMode, req.LineMuted, req.PushMuted, req.QuietFrom, req.QuietTo);
            await db.SaveChangesAsync(ct);
            return req;
        });

        n.MapGet("/channels", async (ICurrentUser user, NotificationsDbContext db, IOptions<NotificationOptions> options, CancellationToken ct) =>
            await ChannelsAsync(user.Id, db, options.Value, ct));

        // Re-check friendship with LINE directly (e.g. the user added the OA before the webhook was configured).
        n.MapPost("/channels/line/refresh", async (ICurrentUser user, IDbContextOutbox<NotificationsDbContext> outbox, IUserDirectory users,
            ILineMessagingClient line, IOptions<NotificationOptions> options, HybridCache cache, TimeProvider clock, CancellationToken ct) =>
        {
            if (!options.Value.LineConfigured) throw new DomainException("line_not_configured", "LINE Official Account is not configured on this server.");
            var lineUserId = (await users.GetLineUserIdsAsync([user.Id], ct)).GetValueOrDefault(user.Id)
                             ?? throw new DomainException("no_line_account", "Your account is not signed in with LINE.");
            if (await line.IsFriendAsync(lineUserId, ct) is { } friend)
                await LineFriendshipUpdater.SetAsync(outbox, user.Id, friend, clock.GetUtcNow(), ct);
            await cache.RemoveAsync(CacheKeys.UserChannels(user.Id), ct);
            return await ChannelsAsync(user.Id, outbox.DbContext, options.Value, ct);
        });

        n.MapPost("/devices", async (DeviceRequest req, ICurrentUser user, NotificationsDbContext db, HybridCache cache, TimeProvider clock, CancellationToken ct) =>
        {
            var endpoint = Guard.NotEmpty(req.Endpoint, "Endpoint", 1000);
            if (req.Kind == DeviceKind.WebPush && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || string.IsNullOrEmpty(req.P256dh) || string.IsNullOrEmpty(req.Auth)))
                throw new DomainException("validation", "Invalid Web Push subscription.");

            var now = clock.GetUtcNow();
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Endpoint == endpoint, ct);
            if (device is null)
            {
                if (await db.Devices.CountAsync(d => d.UserId == user.Id, ct) >= 10)
                    throw new DomainException("too_many_devices", "At most 10 devices per account. Remove an old one first.");
                db.Devices.Add(device = new UserDevice(user.Id, req.Kind, endpoint, req.P256dh, req.Auth, Guard.Optional(req.Label, "Label", 100), now));
            }
            else device.Refresh(user.Id, req.P256dh, req.Auth, Guard.Optional(req.Label, "Label", 100), now);
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(CacheKeys.UserChannels(user.Id), ct);
            return new DeviceDto(device.Id, device.Kind, device.Label, device.LastSeenAt, DeviceDto.KeyOf(device.Endpoint));
        });

        n.MapDelete("/devices/{id:guid}", async (Guid id, ICurrentUser user, NotificationsDbContext db, HybridCache cache, CancellationToken ct) =>
        {
            await db.Devices.Where(d => d.Id == id && d.UserId == user.Id).ExecuteDeleteAsync(ct);
            await cache.RemoveAsync(CacheKeys.UserChannels(user.Id), ct);
            return Results.NoContent();
        });

        n.MapPost("/test", async (ICurrentUser user, IMessageBus bus) =>
        {
            await bus.PublishAsync(new SendTestNotification(user.Id));
            return Results.Accepted();
        });

        api.MapGet("/admin/notifications/line-quota", async (NotificationsDbContext db, IOptions<NotificationOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var month = clock.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var usage = await db.LineQuota.AsNoTracking().FirstOrDefaultAsync(q => q.Month == month, ct);
            return new QuotaDto(month, usage?.Sent ?? 0, options.Value.LineMonthlyQuota, usage?.Exhausted ?? false);
        }).WithTags("System admin").RequireAuthorization(Policies.SystemAdmin);

        // LINE Messaging API webhook: follow / unfollow tell us whether LINE push can reach the user.
        api.MapPost("/webhooks/line", async (HttpRequest http, ILineMessagingClient line, IUserDirectory users,
            IDbContextOutbox<NotificationsDbContext> outbox, HybridCache cache, TimeProvider clock, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Body);
            var body = await reader.ReadToEndAsync(ct);
            if (!line.VerifySignature(body, http.Headers["X-Line-Signature"])) return Results.Unauthorized();

            using var json = JsonDocument.Parse(body);
            foreach (var evt in json.RootElement.GetProperty("events").EnumerateArray())
            {
                var type = evt.GetProperty("type").GetString();
                if (type is not ("follow" or "unfollow")) continue;
                if (!evt.TryGetProperty("source", out var source) || !source.TryGetProperty("userId", out var lineUserIdElement)) continue;
                var userId = await users.FindByLineUserIdAsync(lineUserIdElement.GetString()!, ct);
                if (userId is null) continue; // not registered yet; checked again on demand
                await LineFriendshipUpdater.SetAsync(outbox, userId.Value, type == "follow", clock.GetUtcNow(), ct);
                await cache.RemoveAsync(CacheKeys.UserChannels(userId.Value), ct);
            }
            return Results.Ok();
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static async Task<ChannelsDto> ChannelsAsync(Guid userId, NotificationsDbContext db, NotificationOptions o, CancellationToken ct)
    {
        var friend = await db.LineFriendships.AsNoTracking().AnyAsync(f => f.UserId == userId && f.IsFriend, ct);
        var devices = (await db.Devices.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.LastSeenAt).ToListAsync(ct))
            .Select(d => new DeviceDto(d.Id, d.Kind, d.Label, d.LastSeenAt, DeviceDto.KeyOf(d.Endpoint))).ToList();
        var hasPush = (o.LineConfigured && friend) || (o.WebPushConfigured && devices.Any(d => d.Kind == DeviceKind.WebPush))
                      || (o.FcmConfigured && devices.Any(d => d.Kind == DeviceKind.Fcm));
        return new ChannelsDto(o.LineConfigured, friend, o.LineAddFriendUrl, o.WebPushConfigured, o.VapidPublicKey, o.FcmConfigured, devices, hasPush);
    }
}

internal static class LineFriendshipUpdater
{
    public static async Task SetAsync(IDbContextOutbox<NotificationsDbContext> outbox, Guid userId, bool isFriend, DateTimeOffset now, CancellationToken ct)
    {
        var db = outbox.DbContext;
        var friendship = await db.LineFriendships.FirstOrDefaultAsync(f => f.UserId == userId, ct);
        var changed = friendship is null ? true : friendship.Set(isFriend, now);
        if (friendship is null) db.LineFriendships.Add(new LineFriendship(userId, isFriend, now));
        if (changed) await outbox.PublishAsync(new LineFriendshipChanged(userId, isFriend));
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }
}
