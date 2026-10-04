using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;

namespace SmartShop.Infrastructure.Realtime;

/// <summary>
/// One hub for all real-time updates. Clients are placed in groups:
/// <c>user:{id}</c> (always), <c>shop:{id}</c> (shop members), <c>plant:{id}</c> (active plant members).
/// </summary>
[Authorize]
public sealed class RealtimeHub(IShopDirectory shops, IPlantDirectory plants) : Hub
{
    public const string Path = "/hubs/realtime";

    private Guid UserId => Guid.Parse(Context.User!.FindFirst(SmartShopClaims.UserId)!.Value);

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(UserId));
        await base.OnConnectedAsync();
    }

    public async Task JoinPlant(Guid plantId)
    {
        var membership = await plants.GetMembershipAsync(plantId, UserId);
        if (membership?.Status != MembershipStatus.Active) throw new HubException("Not a member of this village.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Plant(plantId));
    }

    public Task LeavePlant(Guid plantId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Plant(plantId));

    public async Task JoinShop(Guid shopId)
    {
        if (await shops.GetRoleAsync(shopId, UserId) is null) throw new HubException("Not a member of this shop.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Shop(shopId));
    }

    public Task LeaveShop(Guid shopId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Shop(shopId));
}

public static class RealtimeGroups
{
    public static string User(Guid id) => $"user:{id}";
    public static string Shop(Guid id) => $"shop:{id}";
    public static string Plant(Guid id) => $"plant:{id}";
}

/// <summary>Well-known client event names.</summary>
public static class RealtimeEvents
{
    public const string Notification = "notification";
    public const string OrderUpdated = "order.updated";
    public const string NewOrder = "order.new";
    public const string OrderClaimed = "order.claimed";
    public const string PaymentUpdated = "payment.updated";
    public const string ShopStatusChanged = "shop.status";
    public const string MenuChanged = "shop.menu";
    public const string OrderMessage = "order.message";
}

public interface IRealtimePublisher
{
    Task ToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default);
    Task ToUsersAsync(IEnumerable<Guid> userIds, string eventName, object payload, CancellationToken ct = default);
    Task ToShopAsync(Guid shopId, string eventName, object payload, CancellationToken ct = default);
    Task ToPlantAsync(Guid plantId, string eventName, object payload, CancellationToken ct = default);
}

/// <summary>Works from any process: with the Valkey backplane, the worker can push to clients connected to the API.</summary>
internal sealed class RealtimePublisher(IHubContext<RealtimeHub> hub) : IRealtimePublisher
{
    public Task ToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.User(userId)).SendAsync(eventName, payload, ct);

    public Task ToUsersAsync(IEnumerable<Guid> userIds, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Groups(userIds.Distinct().Select(RealtimeGroups.User).ToList()).SendAsync(eventName, payload, ct);

    public Task ToShopAsync(Guid shopId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.Shop(shopId)).SendAsync(eventName, payload, ct);

    public Task ToPlantAsync(Guid plantId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.Plant(plantId)).SendAsync(eventName, payload, ct);
}

public static class RealtimeSetup
{
    public static IServiceCollection AddSmartShopRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        var signalR = services.AddSignalR();
        var redis = configuration.GetConnectionString("cache");
        if (!string.IsNullOrWhiteSpace(redis))
            signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("smartshop"));

        services.AddSingleton<IRealtimePublisher, RealtimePublisher>();
        return services;
    }
}
