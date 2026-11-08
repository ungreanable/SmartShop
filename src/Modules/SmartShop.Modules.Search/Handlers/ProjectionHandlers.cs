using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;
using Wolverine;

namespace SmartShop.Modules.Search.Handlers;

/// <summary>
/// Keeps the search read model in sync. Events are notifications; the handler re-reads the current state
/// through the owning module's contract, so ordering and duplicate delivery do not matter.
/// </summary>
public static class ShopProjectionHandler
{
    public static Task Handle(ShopCreated e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(ShopProfileUpdated e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(OpeningHoursChanged e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(ShopStatusChanged e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(ShopSuspended e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(ShopReinstated e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(DeliveryOptionsUpdated e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);
    public static Task Handle(ShopRatingChanged e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) => Refresh(e.ShopId, db, shops, bus, clock, ct);

    internal static async Task Refresh(Guid shopId, SearchDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct)
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
        listing.DisplayOrder = snapshot.DisplayOrder;
        listing.Schedule = snapshot.Schedule;
        listing.UpdatedAt = clock.GetUtcNow();

        var itemNames = await db.Items.Where(i => i.ShopId == shopId && i.IsVisible).Select(i => i.Name).ToListAsync(ct);
        listing.SearchText = SearchText(listing, itemNames);
        await bus.PublishAsync(new PlantFeedChanged(snapshot.PlantId));
    }

    /// <summary>One event for the whole village: renumber every listing from one query instead of a snapshot per shop.</summary>
    public static async Task Handle(ShopsReordered e, SearchDbContext db, IShopDirectory shops, IMessageBus bus, CancellationToken ct)
    {
        var order = await shops.GetDisplayOrderAsync(e.PlantId, ct);
        foreach (var listing in await db.Shops.Where(s => s.PlantId == e.PlantId).ToListAsync(ct))
            listing.DisplayOrder = order.GetValueOrDefault(listing.ShopId);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new PlantFeedChanged(e.PlantId));
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
    public static Task Handle(ItemChanged e, SearchDbContext db, ICatalogService catalog, IMessageBus bus, CancellationToken ct) => Refresh(e.ItemId, db, catalog, bus, ct);
    public static Task Handle(ItemSoldOut e, SearchDbContext db, ICatalogService catalog, IMessageBus bus, CancellationToken ct) => Refresh(e.ItemId, db, catalog, bus, ct);
    public static Task Handle(ItemBackInStock e, SearchDbContext db, ICatalogService catalog, IMessageBus bus, CancellationToken ct) => Refresh(e.ItemId, db, catalog, bus, ct);

    private static async Task Refresh(Guid itemId, SearchDbContext db, ICatalogService catalog, IMessageBus bus, CancellationToken ct)
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
        await bus.PublishAsync(new PlantFeedChanged(snapshot.PlantId));
    }
}

/// <summary>
/// The village's cached home feed must be dropped only after the projection is committed: removing it inside the handler
/// (before the commit) let a home request re-cache the old rows. Messages from a handler are sent after its commit.
/// </summary>
public sealed record PlantFeedChanged(Guid PlantId) : SmartShop.Contracts.IAsyncMessage;

public static class PlantFeedChangedHandler
{
    public static ValueTask Handle(PlantFeedChanged message, HybridCache cache, CancellationToken ct) =>
        cache.RemoveAsync(CacheKeys.PlantFeed(message.PlantId), ct);
}
