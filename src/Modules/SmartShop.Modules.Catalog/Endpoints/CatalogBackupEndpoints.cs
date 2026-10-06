using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.Modules.Catalog.Services;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Catalog.Endpoints;

/// <summary>Menu section of a shop backup file. Categories are matched by name, items by id, then by name.</summary>
public sealed record CatalogBackup(int Version, List<CategoryBackup> Categories, List<ItemBackup> Items);
public sealed record CategoryBackup(string Name, int SortOrder);
/// <summary><paramref name="OnHand"/> is only used as the starting stock when the item has to be created again.</summary>
public sealed record ItemBackup(Guid Id, string? Category, int SortOrder, bool IsAvailable, int? OnHand, ItemRequest Item);
public sealed record CatalogRestoreResult(int CategoriesCreated, int ItemsCreated, int ItemsUpdated, int ItemsWithoutImages);

internal static class CatalogBackupEndpoints
{
    public const int CurrentVersion = 1;

    public static void Map(IEndpointRouteBuilder api)
    {
        var m = api.MapGroup("/merchant/shops/{shopId:guid}/catalog/backup").WithTags("Merchant catalog").RequirePlantMember();

        m.MapGet("/", async (Guid shopId, MerchantCatalogEndpoints.CatalogCtx ctx, CancellationToken ct) =>
        {
            await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            var categories = await ctx.Db.Categories.AsNoTracking().Where(c => c.ShopId == shopId).OrderBy(c => c.SortOrder).ToListAsync(ct);
            var names = categories.ToDictionary(c => c.Id, c => c.Name);
            var items = await ctx.Db.Items.AsNoTracking().Where(i => i.ShopId == shopId && i.DeletedAt == null).OrderBy(i => i.SortOrder).ToListAsync(ct);
            var ids = items.Select(i => i.Id).ToList();
            var stock = await ctx.Db.Inventories.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);

            return new CatalogBackup(CurrentVersion,
                categories.Select(c => new CategoryBackup(c.Name, c.SortOrder)).ToList(),
                items.Select(i =>
                {
                    var s = stock.GetValueOrDefault(i.Id);
                    var request = new ItemRequest(i.Kind, i.StockMode, i.Name, i.Description, i.Price, null, [.. i.ImageIds],
                        i.AvailabilityWindows.Select(w => new WindowDto([.. w.Days], w.From, w.To)).ToList(),
                        i.SaleFrom, i.SaleTo, i.MaxPerOrder, i.DurationMinutes, i.SlotCapacity, i.AllowedFulfillment?.ToList(),
                        MenuService.ToGroups(i.ModifierGroups), null, i.IsRecommended,
                        null, s?.DailyQuota, s?.DailyResetTime, s?.LowStockThreshold);
                    return new ItemBackup(i.Id, i.CategoryId is { } c ? names.GetValueOrDefault(c) : null, i.SortOrder, i.IsAvailable, s?.OnHand, request);
                }).ToList());
        });

        // Merges the backup into the shop: creates what is missing and updates what exists. Never deletes anything.
        m.MapPost("/restore", async (Guid shopId, CatalogBackup backup, MerchantCatalogEndpoints.CatalogCtx ctx, CancellationToken ct) =>
        {
            var shop = await ctx.RequireAsync(shopId, ShopRole.Manager, ct);
            if (backup.Version is < 1 or > CurrentVersion) throw new DomainException("backup_version", "This backup file is from an unsupported version.");
            if (backup.Items.Count > 300 || backup.Categories.Count > 30) throw new DomainException("validation", "The backup has too many items or categories.");

            var categories = await ctx.Db.Categories.Where(c => c.ShopId == shopId).ToListAsync(ct);
            var categoriesCreated = 0;
            foreach (var b in backup.Categories.OrderBy(c => c.SortOrder))
            {
                var category = categories.FirstOrDefault(c => string.Equals(c.Name, b.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (category is null)
                {
                    if (categories.Count >= 30) throw new DomainException("validation", "At most 30 categories.");
                    categories.Add(category = new ItemCategory(shopId, b.Name, categories.Count));
                    ctx.Db.Categories.Add(category);
                    categoriesCreated++;
                }
                category.SetOrder(b.SortOrder);
            }
            Guid? CategoryId(string? name) => name is null ? null
                : categories.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

            var items = await ctx.Db.Items.Where(i => i.ShopId == shopId && i.DeletedAt == null).ToListAsync(ct);
            var itemIds = items.Select(i => i.Id).ToList();
            var inventories = await ctx.Db.Inventories.Where(s => itemIds.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
            int created = 0, updated = 0, withoutImages = 0;
            var events = new List<Contracts.IntegrationEvent>();

            foreach (var b in backup.Items)
            {
                var item = items.FirstOrDefault(i => i.Id == b.Id)
                           ?? items.FirstOrDefault(i => string.Equals(i.Name, b.Item.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                var request = b.Item with { CategoryId = CategoryId(b.Category), PreOrderRoundId = null };
                try
                {
                    await ctx.ValidateReferencesAsync(shopId, request, item?.ImageIds ?? [], ct);
                }
                catch (DomainException)
                {
                    // Pictures uploaded by someone else or deleted since: keep the item, drop the pictures that cannot be reused.
                    request = request with { ImageIds = [.. (request.ImageIds ?? []).Where(id => item?.ImageIds.Contains(id) == true)] };
                    withoutImages++;
                }

                if (item is null)
                {
                    if (items.Count >= 300) throw new DomainException("validation", "At most 300 items per shop.");
                    item = Item.Create(shopId, shop.PlantId, shop.Schedule.TimeZoneId, MerchantCatalogEndpoints.Details(request), items.Count, ctx.Now);
                    ctx.Db.Items.Add(item);
                    items.Add(item);
                    created++;
                }
                else
                {
                    item.Update(MerchantCatalogEndpoints.Details(request));
                    updated++;
                }
                item.SetAvailable(b.IsAvailable);
                item.SetOrder(b.SortOrder);

                var inventory = inventories.GetValueOrDefault(item.Id);
                if (item.IsCounted && inventory is null)
                {
                    inventory = new Inventory(item.Id, shopId, 0);
                    var initial = b.OnHand ?? (item.StockMode == StockMode.Daily ? request.DailyQuota : null) ?? 0;
                    if (initial > 0) ctx.Db.StockMovements.Add(inventory.Set(initial, ctx.UserId, "Restored from backup", ctx.Now));
                    ctx.Db.Inventories.Add(inventory);
                    inventories[item.Id] = inventory;
                }
                inventory?.ConfigureDaily(request.DailyQuota, request.DailyResetTime);
                inventory?.ConfigureLowStock(request.LowStockThreshold);
                events.Add(new ItemChanged(shop.PlantId, shopId, item.Id, false));
            }

            await ctx.SaveAsync(shopId, ct, [.. events]);
            return new CatalogRestoreResult(categoriesCreated, created, updated, withoutImages);
        });
    }
}
