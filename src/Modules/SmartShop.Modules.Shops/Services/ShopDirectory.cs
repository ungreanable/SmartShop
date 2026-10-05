using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;

namespace SmartShop.Modules.Shops.Services;

internal sealed class ShopDirectory(ShopsDbContext db) : IShopDirectory
{
    public async Task<ShopRole?> GetRoleAsync(Guid shopId, Guid userId, CancellationToken ct = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.ShopId == shopId && m.UserId == userId)
            .Select(m => (ShopRole?)m.Role)
            .FirstOrDefaultAsync(ct);

    public async Task<ShopOrderingInfo?> GetOrderingInfoAsync(Guid shopId, CancellationToken ct = default)
    {
        var s = await db.Shops.AsNoTracking().FirstOrDefaultAsync(x => x.Id == shopId, ct);
        if (s is null) return null;
        return new ShopOrderingInfo(
            s.Id, s.PlantId, s.Name, s.Code, s.Status == ShopLifecycle.Active, s.ToSchedule(),
            s.AcceptMode, s.AcceptTimeoutMinutes, s.ReminderAfterMinutes, s.AutoCompleteHours,
            s.AllowPreorderWhenClosed, s.PrepTimeMinutes, s.SlotIntervalMinutes,
            s.PickupEnabled, s.DeliveryEnabled, s.DeliveryMinOrder, s.RequirePaymentBeforePreparing,
            s.PaymentMethods.Where(p => p.Enabled).OrderBy(p => p.SortOrder).Select(p => p.ToInfo()).ToList());
    }

    public async Task<IReadOnlyList<ShopMemberInfo>> GetMembersAsync(Guid shopId, CancellationToken ct = default) =>
        await db.Members.AsNoTracking()
            .Where(m => m.ShopId == shopId)
            .OrderByDescending(m => m.Role)
            .Select(m => new ShopMemberInfo(m.UserId, m.Role, m.ReceiveOrderNotifications, m.DisplayLabel))
            .ToListAsync(ct);

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
