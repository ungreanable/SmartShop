using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Media;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;

namespace SmartShop.Modules.Catalog.Services;

public sealed record WindowDto(List<DayOfWeek> Days, TimeOnly From, TimeOnly To);
public sealed record ModifierOptionDto(Guid Id, string Name, decimal PriceDelta, bool IsAvailable);
public sealed record ModifierGroupDto(Guid Id, string Name, int MinSelect, int MaxSelect, List<ModifierOptionDto> Options);
public sealed record RoundDto(Guid Id, string Title, string? Description, DateTimeOffset OrderCutoff, DateTimeOffset FulfillFrom, DateTimeOffset FulfillTo,
    int? MinTotalQuantity, int OrderedQuantity, RoundStatus Status);

public sealed record MenuItemDto(
    Guid Id, Guid? CategoryId, ItemKind Kind, StockMode StockMode, string Name, string? Description, decimal Price,
    List<string?> ImageUrls, string? ThumbnailUrl, bool IsOrderable, string? UnavailableReason, int? Remaining,
    int? MaxPerOrder, int? DurationMinutes, List<WindowDto> Windows, DateOnly? SaleFrom, DateOnly? SaleTo,
    List<FulfillmentType>? AllowedFulfillment, List<ModifierGroupDto> ModifierGroups, RoundDto? Round, bool IsRecommended);

public sealed record MenuCategoryDto(Guid? Id, string Name, List<MenuItemDto> Items);
public sealed record MenuDto(Guid ShopId, List<MenuCategoryDto> Categories);

/// <summary>Serializable snapshot of an item used for the menu cache (stock is overlaid live).</summary>
public sealed record CachedItem(
    Guid Id, Guid? CategoryId, ItemKind Kind, StockMode StockMode, string Name, string? Description, decimal Price, List<Guid> ImageIds,
    bool IsAvailable, bool IsSoldOut, string TimeZoneId, List<WindowDto> Windows, DateOnly? SaleFrom, DateOnly? SaleTo, int? MaxPerOrder,
    int? DurationMinutes, List<FulfillmentType>? AllowedFulfillment, List<ModifierGroupDto> ModifierGroups, Guid? PreOrderRoundId,
    bool IsRecommended, int SortOrder);

public sealed record CachedCategory(Guid Id, string Name, int SortOrder);

public sealed record CachedMenu(List<CachedCategory> Categories, List<CachedItem> Items, List<RoundDto> Rounds);

/// <summary>
/// Menu = cached item structure (10 min, invalidated on any catalog change) + live stock from the database.
/// Exact stock is never served from cache; checkout always re-checks it atomically.
/// </summary>
public sealed class MenuService(CatalogDbContext db, HybridCache cache, IMediaUrls media)
{
    public async Task<MenuDto> GetAsync(Guid shopId, DateTimeOffset now, CancellationToken ct)
    {
        var menu = await cache.GetOrCreateAsync(CacheKeys.ShopMenu(shopId), async token => await LoadAsync(shopId, token),
            CacheKeys.Medium, cancellationToken: ct);

        var countedIds = menu.Items.Where(i => i.StockMode is StockMode.Tracked or StockMode.Daily).Select(i => i.Id).ToList();
        var stock = await db.Inventories.AsNoTracking().Where(s => countedIds.Contains(s.ItemId))
            .ToDictionaryAsync(s => s.ItemId, s => s.OnHand - s.Reserved, ct);
        var rounds = menu.Rounds.ToDictionary(r => r.Id);

        MenuItemDto Map(CachedItem i)
        {
            int? available = i.StockMode is StockMode.Tracked or StockMode.Daily ? stock.GetValueOrDefault(i.Id) : null;
            var round = i.PreOrderRoundId is { } rid ? rounds.GetValueOrDefault(rid) : null;
            var reason = ItemRules.UnavailableReason(false, i.IsAvailable, i.TimeZoneId, i.SaleFrom, i.SaleTo,
                i.Windows.Select(w => (w.Days, w.From, w.To)).ToList(), i.StockMode, i.IsSoldOut, available, now);
            if (reason == "outside_hours" && round is not null) reason = null; // pre-orders are picked up later
            if (reason is null && round is not null && (round.Status != RoundStatus.Open || now >= round.OrderCutoff)) reason = "round_closed";
            return new MenuItemDto(i.Id, i.CategoryId, i.Kind, i.StockMode, i.Name, i.Description, i.Price,
                i.ImageIds.Select(id => media.For(id)).ToList(), media.For(i.ImageIds.FirstOrDefault() is var f && f != Guid.Empty ? f : null, MediaVariant.Small),
                reason is null, reason, available, i.MaxPerOrder, i.DurationMinutes, i.Windows, i.SaleFrom, i.SaleTo,
                i.AllowedFulfillment, i.ModifierGroups, round, i.IsRecommended);
        }

        var categories = new List<MenuCategoryDto>();
        var recommended = menu.Items.Where(i => i.IsRecommended).OrderBy(i => i.SortOrder).Select(Map).ToList();
        if (recommended.Count > 0) categories.Add(new MenuCategoryDto(null, "★", recommended));
        foreach (var c in menu.Categories.OrderBy(c => c.SortOrder))
        {
            var items = menu.Items.Where(i => i.CategoryId == c.Id).OrderBy(i => i.SortOrder).Select(Map).ToList();
            if (items.Count > 0) categories.Add(new MenuCategoryDto(c.Id, c.Name, items));
        }
        var known = menu.Categories.Select(c => c.Id).ToHashSet();
        var others = menu.Items.Where(i => i.CategoryId is null || !known.Contains(i.CategoryId.Value)).OrderBy(i => i.SortOrder).Select(Map).ToList();
        if (others.Count > 0) categories.Add(new MenuCategoryDto(null, "", others));
        return new MenuDto(shopId, categories);
    }

    public ValueTask InvalidateAsync(Guid shopId, CancellationToken ct) => cache.RemoveAsync(CacheKeys.ShopMenu(shopId), ct);

    private async Task<CachedMenu> LoadAsync(Guid shopId, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().Where(c => c.ShopId == shopId).ToListAsync(ct);
        var items = await db.Items.AsNoTracking().Where(i => i.ShopId == shopId && i.DeletedAt == null).ToListAsync(ct);
        var rounds = await db.PreOrderRounds.AsNoTracking().Where(r => r.ShopId == shopId && r.Status == RoundStatus.Open).ToListAsync(ct);
        return new CachedMenu(
            categories.Select(c => new CachedCategory(c.Id, c.Name, c.SortOrder)).ToList(),
            items.Select(ToCached).ToList(),
            rounds.Select(ToRound).ToList());
    }

    public static CachedItem ToCached(Item i) => new(
        i.Id, i.CategoryId, i.Kind, i.StockMode, i.Name, i.Description, i.Price, i.ImageIds, i.IsAvailable, i.IsSoldOut, i.TimeZoneId,
        i.AvailabilityWindows.Select(w => new WindowDto(w.Days, w.From, w.To)).ToList(), i.SaleFrom, i.SaleTo, i.MaxPerOrder,
        i.DurationMinutes, i.AllowedFulfillment, ToGroups(i.ModifierGroups), i.PreOrderRoundId, i.IsRecommended, i.SortOrder);

    public static List<ModifierGroupDto> ToGroups(IEnumerable<ModifierGroup> groups) =>
        groups.Select(g => new ModifierGroupDto(g.Id, g.Name, g.MinSelect, g.MaxSelect,
            g.Options.Select(o => new ModifierOptionDto(o.Id, o.Name, o.PriceDelta, o.IsAvailable)).ToList())).ToList();

    public static RoundDto ToRound(PreOrderRound r) =>
        new(r.Id, r.Title, r.Description, r.OrderCutoff, r.FulfillFrom, r.FulfillTo, r.MinTotalQuantity, r.OrderedQuantity, r.Status);
}
