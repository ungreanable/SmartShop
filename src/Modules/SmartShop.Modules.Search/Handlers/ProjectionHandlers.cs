using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;

namespace SmartShop.Modules.Search.Handlers;

/// <summary>
/// Keeps the search read model in sync. Events are notifications; the handler re-reads the current state
/// through the owning module's contract, so ordering and duplicate delivery do not matter.
/// </summary>
public static class ShopProjectionHandler
{
    public static Task Handle(ShopCreated e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(ShopProfileUpdated e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(OpeningHoursChanged e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(ShopStatusChanged e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(ShopSuspended e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(ShopReinstated e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(DeliveryOptionsUpdated e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);
    public static Task Handle(ShopRatingChanged e, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, cache, clock, ct);

    internal static async Task Refresh(Guid shopId, SearchDbContext db, IShopDirectory shops, HybridCache cache, TimeProvider clock, CancellationToken ct)
    {
        var snapshot = await shops.GetListingSnapshotAsync(shopId, ct);
        if (snapshot is null) return;
        var listing = await db.Shops.FirstOrDefaultAsync(s => s.ShopId == shopId, ct);
        if (listing is null) db.Shops.Add(listing = new ShopListing { ShopId = shopId, CreatedAt = snapshot.CreatedAt });

        listing.PlantId = snapshot.PlantId;
        listing.Name = snapshot.Name;
        listing.Description = snapshot.Description;
        listing.Category = snapshot.Category;
        listing.LogoId = snapshot.LogoId;
        listing.CoverId = snapshot.CoverId;
        listing.HouseNo = snapshot.HouseNo;
        listing.PickupEnabled = snapshot.PickupEnabled;
        listing.DeliveryEnabled = snapshot.DeliveryEnabled;
        listing.AllowPreorder = snapshot.AllowPreorderWhenClosed;
        listing.PrepTimeMinutes = snapshot.PrepTimeMinutes;
        listing.RatingAverage = snapshot.RatingAverage;
        listing.RatingCount = snapshot.RatingCount;
        listing.IsActive = snapshot.IsActive;
        listing.Schedule = snapshot.Schedule;
        listing.UpdatedAt = clock.GetUtcNow();

        var itemNames = await db.Items.Where(i => i.ShopId == shopId && i.IsVisible).Select(i => i.Name).ToListAsync(ct);
        listing.SearchText = SearchText(listing, itemNames);
        await cache.RemoveAsync(CacheKeys.PlantFeed(snapshot.PlantId), ct);
    }

    internal static string SearchText(ShopListing l, IEnumerable<string> itemNames)
    {
        var text = string.Join(' ', new[] { l.Name, l.Category, l.Description }.Concat(itemNames)
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
        return text.Length > 4000 ? text[..4000] : text;
    }
}

public static class ItemProjectionHandler
{
    public static Task Handle(ItemChanged e, SearchDbContext db, ICatalogService catalog, HybridCache cache, CancellationToken ct) => Refresh(e.ItemId, db, catalog, cache, ct);
    public static Task Handle(ItemSoldOut e, SearchDbContext db, ICatalogService catalog, HybridCache cache, CancellationToken ct) => Refresh(e.ItemId, db, catalog, cache, ct);
    public static Task Handle(ItemBackInStock e, SearchDbContext db, ICatalogService catalog, HybridCache cache, CancellationToken ct) => Refresh(e.ItemId, db, catalog, cache, ct);

    private static async Task Refresh(Guid itemId, SearchDbContext db, ICatalogService catalog, HybridCache cache, CancellationToken ct)
    {
        var snapshot = await catalog.GetListingSnapshotAsync(itemId, ct);
        if (snapshot is null) return;
        var listing = await db.Items.FirstOrDefaultAsync(i => i.ItemId == itemId, ct);
        if (listing is null) db.Items.Add(listing = new ItemListing { ItemId = itemId });
        listing.ShopId = snapshot.ShopId;
        listing.PlantId = snapshot.PlantId;
        listing.Name = snapshot.Name;
        listing.Description = snapshot.Description;
        listing.Price = snapshot.Price;
        listing.ImageId = snapshot.ImageId;
        listing.IsVisible = snapshot.IsVisible;
        listing.IsSoldOut = snapshot.IsSoldOut;
        await db.SaveChangesAsync(ct);

        // Item names are part of the shop's search text.
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.ShopId == snapshot.ShopId, ct);
        if (shop is not null)
        {
            var names = await db.Items.Where(i => i.ShopId == snapshot.ShopId && i.IsVisible).Select(i => i.Name).ToListAsync(ct);
            shop.SearchText = ShopProjectionHandler.SearchText(shop, names);
        }
        await cache.RemoveAsync(CacheKeys.PlantFeed(snapshot.PlantId), ct);
    }
}
