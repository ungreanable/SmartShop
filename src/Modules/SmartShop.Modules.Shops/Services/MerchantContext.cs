using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using SmartShop.Contracts;
using SmartShop.Contracts.Notifications;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Shops.Services;

/// <summary>
/// Request-scoped helper for merchant endpoints: authorises the caller's shop role, loads the shop inside the
/// current plant, and saves changes together with their integration events (outbox) and cache invalidation.
/// </summary>
internal sealed class MerchantContext(
    ICurrentPlant plant,
    ICurrentUser user,
    IShopAccess access,
    IDbContextOutbox<ShopsDbContext> outbox,
    HybridCache cache,
    IServiceProvider services,
    TimeProvider clock)
{
    public ShopsDbContext Db => outbox.DbContext;
    public Guid UserId => user.Id;
    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<Shop> LoadAsync(Guid shopId, ShopRole minimum, CancellationToken ct)
    {
        await plant.RequireMemberAsync(ct);
        await access.RequireAsync(shopId, minimum, ct);
        return await Db.Shops.FirstOrDefaultAsync(s => s.Id == shopId && s.PlantId == plant.PlantId, ct)
               ?? throw new NotFoundException("Shop", shopId);
    }

    /// <summary>Persists the shop with its events. <paramref name="reevaluate"/> re-computes open/closed status.</summary>
    public async Task SaveAsync(Shop shop, CancellationToken ct, bool reevaluate = false, params IntegrationEvent[] events)
    {
        foreach (var e in events) await outbox.PublishAsync(e);
        if (reevaluate) await outbox.PublishAsync(new EvaluateShopStatus(shop.Id));
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ShopProfile(shop.Id), ct);
    }

    public ValueTask InvalidateRoleAsync(Guid shopId, Guid userId, CancellationToken ct) =>
        cache.RemoveAsync(CacheKeys.ShopRole(shopId, userId), ct);

    public ValueTask InvalidateRecipientsAsync(Guid shopId, CancellationToken ct) =>
        cache.RemoveAsync(CacheKeys.ShopRecipients(shopId), ct);

    /// <summary>
    /// When the village requires it, a shop may only open if at least one member who receives order
    /// notifications can get a push (LINE, Web Push or the mobile app); otherwise orders could go unnoticed.
    /// </summary>
    public async Task EnsureCanOpenAsync(Shop shop, CancellationToken ct)
    {
        var plantInfo = await services.GetRequiredService<IPlantDirectory>().GetPlantAsync(shop.PlantId, ct);
        if (plantInfo?.PushRequirement != PushRequirement.Block) return;

        var recipients = shop.Members.Where(m => m.ReceiveOrderNotifications).Select(m => m.UserId).ToList();
        var channels = await services.GetRequiredService<INotificationChannelDirectory>().GetAsync(recipients, ct);
        if (!channels.Any(c => c.HasPush))
            throw new DomainException("no_push_channel",
                "No shop member can receive push notifications. Add the LINE Official Account as a friend or enable Web Push before opening.");
    }
}
