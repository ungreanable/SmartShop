using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Notifications;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Notifications.Data;
using SmartShop.Modules.Notifications.Domain;
using SmartShop.Modules.Notifications.Endpoints;
using SmartShop.Modules.Notifications.Services;

namespace SmartShop.Modules.Notifications;

public sealed class NotificationsModule : IModule
{
    public string Name => "Notifications";

    public IReadOnlyList<Type> DbContexts => [typeof(NotificationsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        var c = builder.Configuration;
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.SchemaName);
        services.Configure<NotificationOptions>(o =>
        {
            o.LineAccessToken = c["Line:MessagingChannelAccessToken"];
            o.LineChannelSecret = c["Line:MessagingChannelSecret"];
            o.LineAddFriendUrl = c["Line:AddFriendUrl"];
            o.LineMonthlyQuota = c.GetValue("Line:MonthlyQuota", 300);
            o.LineReserveForHighPriority = c.GetValue("Line:QuotaReserveForHighPriority", 50);
            o.VapidSubject = c["WebPush:Subject"];
            o.VapidPublicKey = c["WebPush:PublicKey"];
            o.VapidPrivateKey = c["WebPush:PrivateKey"];
            o.FirebaseCredentialsJson = c["Firebase:CredentialsJson"];
            o.PublicUrl = c["App:PublicUrl"] ?? o.PublicUrl;
            o.LiffId = c["Auth:Line:LiffId"];
            o.TimeZoneId = c["App:TimeZoneId"] ?? o.TimeZoneId;
        });
        services.AddHttpClient<ILineMessagingClient, LineMessagingClient>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<INotificationChannelDirectory, ChannelDirectory>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => NotificationEndpoints.Map(app);

    /// <summary>Generates VAPID keys for Web Push: <c>dotnet SmartShop.Api.dll vapid</c>.</summary>
    public static (string PublicKey, string PrivateKey) GenerateVapidKeys()
    {
        var keys = WebPush.VapidHelper.GenerateVapidKeys();
        return (keys.PublicKey, keys.PrivateKey);
    }
}

/// <summary>Which users can be reached by push (used by the shop's notification health check and open-shop guard).</summary>
internal sealed class ChannelDirectory(NotificationsDbContext db, IOptions<NotificationOptions> options, HybridCache cache) : INotificationChannelDirectory
{
    public async Task<IReadOnlyList<ChannelStatus>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var o = options.Value;
        var result = new List<ChannelStatus>();
        foreach (var userId in userIds.Distinct())
        {
            result.Add(await cache.GetOrCreateAsync(CacheKeys.UserChannels(userId), async token =>
            {
                var friend = o.LineConfigured && await db.LineFriendships.AsNoTracking().AnyAsync(f => f.UserId == userId && f.IsFriend, token);
                var web = o.WebPushConfigured ? await db.Devices.CountAsync(d => d.UserId == userId && d.Kind == DeviceKind.WebPush, token) : 0;
                var mobile = o.FcmConfigured ? await db.Devices.CountAsync(d => d.UserId == userId && d.Kind == DeviceKind.Fcm, token) : 0;
                return new ChannelStatus(userId, friend, web, mobile);
            }, CacheKeys.Short, cancellationToken: ct));
        }
        return result;
    }
}

public static class SendTestNotificationHandler
{
    public static Task Handle(SendTestNotification command, NotificationDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.NotifyAsync([command.UserId], null, SmartShop.SharedKernel.Ids.New(),
            new NotificationTemplate("test", NotificationPriority.High, "🔔 ทดสอบการแจ้งเตือน", "ถ้าเห็นข้อความนี้ แปลว่าได้รับแจ้งเตือนจาก SmartShop แล้ว", "/notifications", true), ct);
}

public static class UserErasedNotificationHandler
{
    public static async Task Handle(UserErased e, NotificationsDbContext db, CancellationToken ct)
    {
        await db.Devices.Where(d => d.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => n.UserId == e.UserId).ExecuteDeleteAsync(ct);
        await db.LineFriendships.Where(f => f.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}
