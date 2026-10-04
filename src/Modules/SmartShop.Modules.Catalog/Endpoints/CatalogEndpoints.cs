using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.Modules.Catalog.Services;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Catalog.Endpoints;

public sealed record SlotDto(DateTimeOffset Start, DateTimeOffset End, int Remaining);

internal static class CatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var shops = api.MapGroup("/shops/{shopId:guid}").WithTags("Catalog").RequirePlantMember();

        shops.MapGet("/menu", async (Guid shopId, ICurrentPlant plant, IShopDirectory directory, MenuService menu, TimeProvider clock, CancellationToken ct) =>
        {
            var shop = await directory.GetOrderingInfoAsync(shopId, ct);
            if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", shopId);
            return await menu.GetAsync(shopId, clock.GetUtcNow(), ct);
        });

        // Bookable time slots of a service on a given (local) date with remaining capacity.
        api.MapGet("/items/{itemId:guid}/slots", async (Guid itemId, DateOnly? date, ICurrentPlant plant, CatalogDbContext db,
            IShopDirectory directory, TimeProvider clock, CancellationToken ct) =>
        {
            var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.PlantId == plant.PlantId && i.DeletedAt == null, ct)
                       ?? throw new NotFoundException("Item", itemId);
            if (item.StockMode != StockMode.Slot) throw new DomainException("not_bookable", "This item is not booked by time slot.");
            var shop = await directory.GetOrderingInfoAsync(item.ShopId, ct) ?? throw new NotFoundException("Shop", item.ShopId);

            var now = clock.GetUtcNow();
            var tz = ShopScheduleEvaluator.ResolveTimeZone(shop.Schedule.TimeZoneId);
            var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
            var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
            var reference = dayStart > now ? dayStart : now;
            var windows = ShopScheduleEvaluator
                .SelectableWindows(shop.Schedule, reference, 1, item.DurationMinutes ?? 60, shop.PrepTimeMinutes)
                .Where(w => w.Start >= dayStart && w.Start < dayStart.AddDays(1) && w.Start > now)
                .ToList();

            var starts = windows.Select(w => w.Start).ToList();
            var booked = await db.SlotBookings.AsNoTracking()
                .Where(b => b.ItemId == itemId && starts.Contains(b.SlotStart) && b.Status != SlotBookingStatus.Released)
                .GroupBy(b => b.SlotStart).Select(g => new { g.Key, Count = g.Sum(b => b.Quantity) })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            var capacity = item.SlotCapacity ?? 1;
            return windows.Select(w => new SlotDto(w.Start, w.End, Math.Max(0, capacity - booked.GetValueOrDefault(w.Start)))).ToList();
        }).WithTags("Catalog").RequirePlantMember();
    }
}
