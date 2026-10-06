using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.Modules.Catalog.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Catalog.Endpoints;

public sealed record CategoryRequest(string Name);
public sealed record CategoryDto(Guid Id, string Name, int SortOrder);
public sealed record OrderIdsRequest(List<Guid> Ids);
public sealed record ItemRequest(
    ItemKind Kind, StockMode StockMode, string Name, string? Description, decimal Price, Guid? CategoryId,
    List<Guid>? ImageIds, List<WindowDto>? Windows, DateOnly? SaleFrom, DateOnly? SaleTo, int? MaxPerOrder,
    int? DurationMinutes, int? SlotCapacity, List<FulfillmentType>? AllowedFulfillment, List<ModifierGroupDto>? ModifierGroups,
    Guid? PreOrderRoundId, bool IsRecommended = false,
    int? InitialStock = null, int? DailyQuota = null, TimeOnly? DailyResetTime = null, int? LowStockThreshold = null);
public sealed record AvailabilityRequest(bool IsAvailable);
public sealed record SoldOutRequest(bool SoldOut);
/// <summary><c>set</c> = count after stock-taking, <c>add</c> = restock.</summary>
public sealed record StockRequest(string Action, int Quantity, string? Note);
public sealed record StockSettingsRequest(int? DailyQuota, TimeOnly? DailyResetTime, int? LowStockThreshold);
public sealed record MerchantItemDto(MenuItemDto Item, bool IsAvailable, bool IsSoldOut, List<Guid> ImageIds, int SortOrder,
    int? OnHand, int? Reserved, int? DailyQuota, TimeOnly? DailyResetTime, int? LowStockThreshold, int? SlotCapacity);
public sealed record MovementDto(Guid Id, StockMovementType Type, int Quantity, int OnHandAfter, int ReservedAfter, Guid? OrderId, string? Note, DateTimeOffset At);
public sealed record RoundRequest(string Title, string? Description, DateTimeOffset OrderCutoff, DateTimeOffset FulfillFrom, DateTimeOffset FulfillTo, int? MinTotalQuantity);

internal static class MerchantCatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var m = api.MapGroup("/merchant/shops/{shopId:guid}/catalog").WithTags("Merchant catalog").RequirePlantMember();

        // ---- categories ----
        m.MapGet("/categories", async (Guid shopId, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            return await ctx.Db.Categories.AsNoTracking().Where(c => c.ShopId == shopId).OrderBy(c => c.SortOrder)
                .Select(c => new CategoryDto(c.Id, c.Name, c.SortOrder)).ToListAsync(ct);
        });

        m.MapPost("/categories", async (Guid shopId, CategoryRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var count = await ctx.Db.Categories.CountAsync(c => c.ShopId == shopId, ct);
            if (count >= 30) throw new DomainException("validation", "At most 30 categories.");
            var category = new ItemCategory(shopId, req.Name, count);
            ctx.Db.Categories.Add(category);
            await ctx.SaveAsync(shopId, ct);
            return new CategoryDto(category.Id, category.Name, category.SortOrder);
        });

        m.MapPut("/categories/{id:guid}", async (Guid shopId, Guid id, CategoryRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var category = await ctx.Db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.ShopId == shopId, ct) ?? throw new NotFoundException("Category", id);
            category.Rename(req.Name);
            await ctx.SaveAsync(shopId, ct);
            return Results.NoContent();
        });

        m.MapDelete("/categories/{id:guid}", async (Guid shopId, Guid id, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var category = await ctx.Db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.ShopId == shopId, ct) ?? throw new NotFoundException("Category", id);
            ctx.Db.Categories.Remove(category);
            await ctx.SaveAsync(shopId, ct);
            return Results.NoContent();
        });

        m.MapPut("/categories/order", async (Guid shopId, OrderIdsRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var categories = await ctx.Db.Categories.Where(c => c.ShopId == shopId).ToListAsync(ct);
            for (var i = 0; i < req.Ids.Count; i++) categories.FirstOrDefault(c => c.Id == req.Ids[i])?.SetOrder(i);
            await ctx.SaveAsync(shopId, ct);
            return Results.NoContent();
        });

        // ---- items ----
        m.MapGet("/items", async (Guid shopId, CatalogCtx ctx, IMediaUrls media, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            var items = await ctx.Db.Items.AsNoTracking().Where(i => i.ShopId == shopId && i.DeletedAt == null).OrderBy(i => i.SortOrder).ToListAsync(ct);
            var ids = items.Select(i => i.Id).ToList();
            var stock = await ctx.Db.Inventories.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
            var roundIds = items.Where(i => i.PreOrderRoundId != null).Select(i => i.PreOrderRoundId!.Value).ToList();
            var rounds = await ctx.Db.PreOrderRounds.AsNoTracking().Where(r => roundIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            return items.Select(i => ToMerchant(i, stock.GetValueOrDefault(i.Id), i.PreOrderRoundId is { } r ? rounds.GetValueOrDefault(r) : null, media, ctx.Now)).ToList();
        });

        m.MapPost("/items", async (Guid shopId, ItemRequest req, CatalogCtx ctx, IMediaUrls media, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            await ctx.ValidateReferencesAsync(shopId, req, [], ct);
            var count = await ctx.Db.Items.CountAsync(i => i.ShopId == shopId && i.DeletedAt == null, ct);
            if (count >= 300) throw new DomainException("validation", "At most 300 items per shop.");

            var item = Item.Create(shopId, shop.PlantId, shop.Schedule.TimeZoneId, Details(req), count, ctx.Now);
            ctx.Db.Items.Add(item);
            Inventory? inventory = null;
            if (item.IsCounted)
            {
                inventory = new Inventory(item.Id, shopId, 0);
                inventory.ConfigureDaily(req.DailyQuota, req.DailyResetTime);
                inventory.ConfigureLowStock(req.LowStockThreshold);
                var initial = req.InitialStock ?? (item.StockMode == StockMode.Daily ? req.DailyQuota : null) ?? 0;
                if (initial > 0) ctx.Db.StockMovements.Add(inventory.Set(initial, ctx.UserId, "Initial stock", ctx.Now));
                ctx.Db.Inventories.Add(inventory);
            }
            await ctx.SaveAsync(shopId, ct, [.. await ctx.Announcer.CheckAsync([item.Id], ct), new ItemChanged(shop.PlantId, shopId, item.Id, false)]);
            return Results.Created($"/api/merchant/shops/{shopId}/catalog/items/{item.Id}", ToMerchant(item, inventory, null, media, ctx.Now));
        });

        m.MapPut("/items/{id:guid}", async (Guid shopId, Guid id, ItemRequest req, CatalogCtx ctx, IMediaUrls media, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var item = await ctx.LoadItemAsync(shopId, id, ct);
            await ctx.ValidateReferencesAsync(shopId, req, item.ImageIds, ct);
            item.Update(Details(req));

            var inventory = await ctx.Db.Inventories.FirstOrDefaultAsync(s => s.ItemId == id, ct);
            if (item.IsCounted && inventory is null)
            {
                inventory = new Inventory(item.Id, shopId, 0);
                if (req.InitialStock is > 0) ctx.Db.StockMovements.Add(inventory.Set(req.InitialStock.Value, ctx.UserId, "Initial stock", ctx.Now));
                ctx.Db.Inventories.Add(inventory);
            }
            if (inventory is not null)
            {
                inventory.ConfigureDaily(req.DailyQuota, req.DailyResetTime);
                inventory.ConfigureLowStock(req.LowStockThreshold);
            }
            await ctx.SaveAsync(shopId, ct, [.. await ctx.Announcer.CheckAsync([item.Id], ct), new ItemChanged(shop.PlantId, shopId, item.Id, false)]);
            return ToMerchant(item, inventory, null, media, ctx.Now);
        });

        m.MapDelete("/items/{id:guid}", async (Guid shopId, Guid id, CatalogCtx ctx, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var item = await ctx.LoadItemAsync(shopId, id, ct);
            item.Delete(ctx.Now);
            await ctx.SaveAsync(shopId, ct, new ItemChanged(shop.PlantId, shopId, item.Id, true));
            return Results.NoContent();
        });

        m.MapPut("/items/order", async (Guid shopId, OrderIdsRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var items = await ctx.Db.Items.Where(i => i.ShopId == shopId && i.DeletedAt == null).ToListAsync(ct);
            for (var i = 0; i < req.Ids.Count; i++) items.FirstOrDefault(x => x.Id == req.Ids[i])?.SetOrder(i);
            await ctx.SaveAsync(shopId, ct);
            return Results.NoContent();
        });

        // Quick toggles used many times a day (staff allowed).
        m.MapPut("/items/{id:guid}/availability", async (Guid shopId, Guid id, AvailabilityRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            var item = await ctx.LoadItemAsync(shopId, id, ct);
            item.SetAvailable(req.IsAvailable);
            await ctx.SaveAsync(shopId, ct, [.. await ctx.Announcer.CheckAsync([id], ct), new ItemChanged(shop.PlantId, shopId, id, false)]);
            return Results.NoContent();
        });

        m.MapPut("/items/{id:guid}/sold-out", async (Guid shopId, Guid id, SoldOutRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            var item = await ctx.LoadItemAsync(shopId, id, ct);
            if (item.StockMode != StockMode.Untracked)
                throw new DomainException("use_stock", "This item counts stock; set the stock to 0 instead.");
            item.SetSoldOut(req.SoldOut);
            await ctx.SaveAsync(shopId, ct, [.. await ctx.Announcer.CheckAsync([id], ct)]);
            return Results.NoContent();
        });

        m.MapPost("/items/{id:guid}/stock", async (Guid shopId, Guid id, StockRequest req, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            var item = await ctx.LoadItemAsync(shopId, id, ct);
            if (!item.IsCounted) throw new DomainException("not_counted", "This item does not count stock.");
            var inventory = await ctx.Db.Inventories.FirstAsync(s => s.ItemId == id, ct);
            var movement = req.Action.ToLowerInvariant() switch
            {
                "set" => inventory.Set(req.Quantity, ctx.UserId, req.Note, ctx.Now),
                "add" => inventory.Add(req.Quantity, ctx.UserId, req.Note, ctx.Now),
                _ => throw new DomainException("validation", "Action must be 'set' or 'add'."),
            };
            ctx.Db.StockMovements.Add(movement);
            await ctx.SaveAsync(shopId, ct, [.. await ctx.Announcer.CheckAsync([id], ct)]);
            return new { inventory.OnHand, inventory.Reserved, inventory.Available };
        });

        m.MapGet("/items/{id:guid}/movements", async (Guid shopId, Guid id, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            return await ctx.Db.StockMovements.AsNoTracking().Where(x => x.ItemId == id && x.ShopId == shopId)
                .OrderByDescending(x => x.At).Take(200)
                .Select(x => new MovementDto(x.Id, x.Type, x.Quantity, x.OnHandAfter, x.ReservedAfter, x.OrderId, x.Note, x.At))
                .ToListAsync(ct);
        });

        // ---- pre-order rounds / group buys ----
        m.MapGet("/rounds", async (Guid shopId, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Staff, ct);
            var rounds = await ctx.Db.PreOrderRounds.AsNoTracking().Where(r => r.ShopId == shopId)
                .OrderByDescending(r => r.OrderCutoff).Take(50).ToListAsync(ct);
            return rounds.Select(MenuService.ToRound).ToList();
        });

        m.MapPost("/rounds", async (Guid shopId, RoundRequest r, CatalogCtx ctx, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var round = PreOrderRound.Create(shopId, shop.PlantId, r.Title, r.Description, r.OrderCutoff, r.FulfillFrom, r.FulfillTo, r.MinTotalQuantity, ctx.Now);
            ctx.Db.PreOrderRounds.Add(round);
            await ctx.SaveAsync(shopId, ct);
            return MenuService.ToRound(round);
        });

        m.MapPut("/rounds/{id:guid}", async (Guid shopId, Guid id, RoundRequest r, CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var round = await ctx.Db.PreOrderRounds.FirstOrDefaultAsync(x => x.Id == id && x.ShopId == shopId, ct) ?? throw new NotFoundException("Round", id);
            round.Update(r.Title, r.Description, r.OrderCutoff, r.FulfillFrom, r.FulfillTo, r.MinTotalQuantity, ctx.Now);
            await ctx.SaveAsync(shopId, ct);
            return MenuService.ToRound(round);
        });
    }

    internal static ItemDetails Details(ItemRequest r) => new(
        r.Kind, r.StockMode, r.Name, r.Description, r.Price, r.CategoryId, r.ImageIds ?? [],
        (r.Windows ?? []).Select(w => new AvailabilityWindow(w.Days, w.From, w.To)).ToList(),
        r.SaleFrom, r.SaleTo, r.MaxPerOrder, r.DurationMinutes, r.SlotCapacity, r.AllowedFulfillment,
        (r.ModifierGroups ?? []).Select(g => new ModifierGroup
        {
            Id = g.Id == Guid.Empty ? Ids.New() : g.Id,
            Name = g.Name,
            MinSelect = g.MinSelect,
            MaxSelect = g.MaxSelect,
            Options = g.Options.Select(o => new ModifierOption
            {
                Id = o.Id == Guid.Empty ? Ids.New() : o.Id,
                Name = o.Name,
                PriceDelta = o.PriceDelta,
                IsAvailable = o.IsAvailable,
            }).ToList(),
        }).ToList(),
        r.PreOrderRoundId, r.IsRecommended);

    private static MerchantItemDto ToMerchant(Item i, Inventory? s, PreOrderRound? round, IMediaUrls media, DateTimeOffset now)
    {
        var cached = MenuService.ToCached(i);
        int? available = i.IsCounted ? s?.Available ?? 0 : null;
        var reason = i.UnavailableReason(now, available);
        var item = new MenuItemDto(i.Id, i.CategoryId, i.Kind, i.StockMode, i.Name, i.Description, i.Price,
            i.ImageIds.Select(id => media.For(id)).ToList(),
            media.For(i.ImageIds.FirstOrDefault() is var f && f != Guid.Empty ? f : null, MediaVariant.Small),
            reason is null, reason, available, i.MaxPerOrder, i.DurationMinutes, cached.Windows, i.SaleFrom, i.SaleTo,
            i.AllowedFulfillment, cached.ModifierGroups, round is null ? null : MenuService.ToRound(round), i.IsRecommended);
        return new MerchantItemDto(item, i.IsAvailable, i.IsSoldOut, i.ImageIds, i.SortOrder, s?.OnHand, s?.Reserved,
            s?.DailyQuota, s?.DailyResetTime, s?.LowStockThreshold, i.SlotCapacity);
    }

    /// <summary>Authorises the shop role, wraps the outbox and invalidates the menu cache on save.</summary>
    internal sealed class CatalogCtx(
        ICurrentPlant plant, ICurrentUser user, IShopAccess access, IShopDirectory shops,
        IDbContextOutbox<CatalogDbContext> outbox, MenuService menu, StockAnnouncer announcer, IMediaService media, TimeProvider clock)
    {
        public CatalogDbContext Db => outbox.DbContext;
        public Guid UserId => user.Id;
        public DateTimeOffset Now => clock.GetUtcNow();
        public StockAnnouncer Announcer => announcer;

        public async Task<ShopOrderingInfo> RequireAsync(Guid shopId, ShopRole minimum, CancellationToken ct)
        {
            await access.RequireAsync(shopId, minimum, ct);
            var shop = await shops.GetOrderingInfoAsync(shopId, ct);
            if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", shopId);
            return shop;
        }

        public async Task<Item> LoadItemAsync(Guid shopId, Guid itemId, CancellationToken ct) =>
            await Db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.ShopId == shopId && i.DeletedAt == null, ct)
            ?? throw new NotFoundException("Item", itemId);

        public async Task ValidateReferencesAsync(Guid shopId, ItemRequest req, IReadOnlyCollection<Guid> existingImages, CancellationToken ct)
        {
            foreach (var image in (req.ImageIds ?? []).Where(i => !existingImages.Contains(i)))
                await media.RequireOwnedAsync(image, UserId, MediaPurpose.ItemImage, ct);
            if (req.CategoryId is { } c && !await Db.Categories.AnyAsync(x => x.Id == c && x.ShopId == shopId, ct))
                throw new DomainException("validation", "Unknown category.");
            if (req.PreOrderRoundId is { } r && !await Db.PreOrderRounds.AnyAsync(x => x.Id == r && x.ShopId == shopId && x.Status == RoundStatus.Open, ct))
                throw new DomainException("validation", "Unknown or closed pre-order round.");
        }

        public async Task SaveAsync(Guid shopId, CancellationToken ct, params Contracts.IntegrationEvent[] events)
        {
            foreach (var e in events) await outbox.PublishAsync(e);
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await menu.InvalidateAsync(shopId, ct);
        }
    }
}
