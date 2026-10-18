using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;

namespace SmartShop.Modules.Shops.Services;

internal sealed class ShopDirectory(ShopsDbContext db, HybridCache cache, ICurrentPlant plant) : IShopDirectory
{
    /// <summary>Cache lookup only: never runs the factory and never writes.</summary>
    private static readonly HybridCacheEntryOptions ReadOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData | HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    public async Task<ShopRole?> GetRoleAsync(Guid shopId, Guid userId, CancellationToken ct = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.ShopId == shopId && m.UserId == userId)
            .Select(m => (ShopRole?)m.Role)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Read on every cart change, checkout and order screen, so it is cached. The schedule is cached as data and open/closed
    /// is still evaluated against the current time by the caller. Every shop save drops the entry
    /// (<see cref="MerchantContext.SaveAsync"/>, admin suspension endpoints, status re-evaluation).
    /// <para>
    /// Only shops that were found are cached: under row-level security a village-scoped request cannot see other villages'
    /// shops, and caching that "not found" would hide the shop from its own village. For the same reason a cached entry is
    /// only returned inside its own village, exactly as the database policy would.
    /// </para>
    /// </summary>
    public async Task<ShopOrderingInfo?> GetOrderingInfoAsync(Guid shopId, CancellationToken ct = default)
    {
        var key = CacheKeys.ShopProfile(shopId);
        var info = await cache.GetOrCreateAsync<ShopOrderingInfo?>(key, _ => ValueTask.FromResult<ShopOrderingInfo?>(null),
            ReadOnly, cancellationToken: ct);
        if (info is null)
        {
            info = await LoadOrderingInfoAsync(shopId, ct);
            if (info is not null) await cache.SetAsync(key, info, CacheKeys.Short, cancellationToken: ct);
        }
        return info is not null && plant.ResolvedPlantId is { } scoped && info.PlantId != scoped ? null : info;
    }

    private async Task<ShopOrderingInfo?> LoadOrderingInfoAsync(Guid shopId, CancellationToken ct)
    {
        var s = await db.Shops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == shopId, ct);
        if (s is null) return null;
        return new ShopOrderingInfo(
            s.Id, s.PlantId, s.Name, s.Code, s.Status == ShopLifecycle.Active, s.ToSchedule(),
            s.AcceptMode, s.AcceptTimeoutMinutes, s.ReminderAfterMinutes, s.AutoCompleteHours,
            s.AllowPreorderWhenClosed, s.PrepTimeMinutes, s.SlotIntervalMinutes,
            s.PickupEnabled, s.DeliveryEnabled, s.DeliveryMinOrder, s.RequirePaymentBeforePreparing,
            s.PaymentMethods.Where(p => p.Enabled).OrderBy(p => p.SortOrder).Select(p => p.ToInfo()).ToList(),
            s.AcceptsCustomOrders);
    }

    /// <summary>Cached: read by every order detail (staff labels on the timeline); dropped whenever a member changes.</summary>
    public async Task<IReadOnlyList<ShopMemberInfo>> GetMembersAsync(Guid shopId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(CacheKeys.ShopMembers(shopId), async token => await db.Members.AsNoTracking()
                .Where(m => m.ShopId == shopId)
                .OrderByDescending(m => m.Role)
                .Select(m => new ShopMemberInfo(m.UserId, m.Role, m.ReceiveOrderNotifications, m.DisplayLabel))
                .ToListAsync(token),
            CacheKeys.Short, cancellationToken: ct);

    public async Task<IReadOnlyList<Guid>> GetOrderNotificationRecipientsAsync(Guid shopId, CancellationToken ct = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.ShopId == shopId && m.ReceiveOrderNotifications)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public async Task<ShopListingSnapshot?> GetListingSnapshotAsync(Guid shopId, CancellationToken ct = default)
    {
        var s = await db.Shops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == shopId, ct);
        return s is null
            ? null
            : new ShopListingSnapshot(s.Id, s.PlantId, s.Name, s.Description, s.Category, s.LogoId, s.CoverId, s.HouseNo,
                s.PickupEnabled, s.DeliveryEnabled, s.AllowPreorderWhenClosed, s.PrepTimeMinutes,
                s.RatingAverage, s.RatingCount, s.IsListed, s.ToSchedule(), s.CreatedAt);
    }

    public async Task<IReadOnlyList<Guid>> GetFavoritersAsync(Guid shopId, CancellationToken ct = default) =>
        await db.Favorites.AsNoTracking().Where(f => f.ShopId == shopId).Select(f => f.UserId).ToListAsync(ct);
}
