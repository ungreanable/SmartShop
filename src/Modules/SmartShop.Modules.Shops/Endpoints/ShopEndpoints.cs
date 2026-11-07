using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;
using SmartShop.Modules.Shops.Services;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Shops.Endpoints;

public sealed record WindowDto(DateTimeOffset Start, DateTimeOffset End);

public sealed record AdminShopRow(Guid Id, string Name, string Code, string? LogoUrl, ShopLifecycle Lifecycle, string? SuspendReason,
    bool VisibleWhileSuspended, ShopStatusDto Status, Guid OwnerId, int MemberCount, decimal RatingAverage, int RatingCount, DateTimeOffset CreatedAt,
    int? DisplayOrder);

/// <summary>The village's shops from the top of the list down; shops left out go to the end.</summary>
public sealed record ShopOrderRequest(List<Guid> ShopIds);

public sealed record SuspendRequest(string? Reason, bool Visible = false);
public sealed record SuspensionVisibilityRequest(bool Visible);

internal static class ShopEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var shops = api.MapGroup("/shops").WithTags("Shops").RequirePlantMember();

        shops.MapGet("/{id:guid}", async (Guid id, ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, IMediaUrls media,
            TimeProvider clock, CancellationToken ct) =>
        {
            var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)
                       ?? throw new NotFoundException("Shop", id);
            var isMember = shop.Member(user.Id) is not null;
            if (!shop.IsListed && !isMember && plant.Membership.Role != Contracts.Plants.PlantRole.PlantAdmin)
                throw new NotFoundException("Shop", id);
            var favorite = await db.Favorites.AnyAsync(f => f.UserId == user.Id && f.ShopId == id, ct);
            return ShopMapper.Details(shop, media, clock.GetUtcNow(), user.Id, favorite);
        });

        // Time windows the customer can choose at checkout ("pick up between 16:00 and 16:30").
        shops.MapGet("/{id:guid}/windows", async (Guid id, int? days, ICurrentPlant plant, ShopsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)
                       ?? throw new NotFoundException("Shop", id);
            return ShopScheduleEvaluator
                .SelectableWindows(shop.ToSchedule(), clock.GetUtcNow(), Math.Clamp(days ?? 2, 1, 7), shop.SlotIntervalMinutes, shop.PrepTimeMinutes)
                .Select(w => new WindowDto(w.Start, w.End)).ToList();
        });

        shops.MapPut("/{id:guid}/favorite", async (Guid id, ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Shops.AnyAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)) throw new NotFoundException("Shop", id);
            if (!await db.Favorites.AnyAsync(f => f.UserId == user.Id && f.ShopId == id, ct))
            {
                db.Favorites.Add(new ShopFavorite(user.Id, id, clock.GetUtcNow()));
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        shops.MapDelete("/{id:guid}/favorite", async (Guid id, ICurrentUser user, ShopsDbContext db, CancellationToken ct) =>
        {
            await db.Favorites.Where(f => f.UserId == user.Id && f.ShopId == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        shops.MapGet("/favorites", async (ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, CancellationToken ct) =>
            await (from f in db.Favorites
                   join s in db.Shops on f.ShopId equals s.Id
                   where f.UserId == user.Id && s.PlantId == plant.PlantId
                   select f.ShopId).ToListAsync(ct));

        // Shops in the current plant where the caller is owner/manager/staff (for the merchant mode switcher).
        api.MapGet("/me/shops", async (ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var mine = await db.Shops.AsNoTracking()
                .Where(s => s.PlantId == plant.PlantId && s.Members.Any(m => m.UserId == user.Id))
                .OrderBy(s => s.Name).ToListAsync(ct);
            return mine.Select(s =>
            {
                var me = s.Member(user.Id)!;
                return new MyShopDto(s.Id, s.Name, s.Code, media.For(s.LogoId, MediaVariant.Small), me.Role, ShopMapper.Status(s, now), s.Status, me.ReceiveOrderNotifications);
            }).ToList();
        }).WithTags("Shops").RequirePlantMember();

        // ---- plant admin ----
        var admin = api.MapGroup("/plant/admin/shops").WithTags("Village admin").RequirePlantAdmin();

        admin.MapGet("/", async (ICurrentPlant plant, ShopsDbContext db, IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            // In the order customers see them (arranged shops first), so the same list serves as the arrange screen.
            var all = await db.Shops.AsNoTracking().Where(s => s.PlantId == plant.PlantId)
                .OrderBy(s => s.DisplayOrder == null).ThenBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync(ct);
            return all.Select(s => new AdminShopRow(s.Id, s.Name, s.Code, media.For(s.LogoId, MediaVariant.Small), s.Status, s.SuspendReason,
                s.VisibleWhileSuspended, ShopMapper.Status(s, now), s.OwnerId, s.Members.Count, s.RatingAverage, s.RatingCount, s.CreatedAt,
                s.DisplayOrder)).ToList();
        });

        // The position lives on the shop row rather than in a plant-level list: Search already re-reads a shop's snapshot on
        // every shop event, so the position travels with the rest of the listing, and a new shop simply has none (= last).
        admin.MapPut("/order", async (ShopOrderRequest req, ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<ShopsDbContext> outbox,
            CancellationToken ct) =>
        {
            if (req.ShopIds.Count > 1000) throw new DomainException("too_many_shops", "Too many shops in one request.");
            var position = req.ShopIds.Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            // Scalars only: members, payment methods and closures are not needed to renumber a shop.
            var shops = await outbox.DbContext.Shops.IgnoreAutoIncludes().Where(s => s.PlantId == plant.PlantId).ToListAsync(ct);
            foreach (var shop in shops) shop.SetDisplayOrder(position.TryGetValue(shop.Id, out var i) ? i : null);
            await outbox.PublishAsync(new ShopsReordered(plant.PlantId, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/suspend", async (Guid id, SuspendRequest req, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ShopsDbContext> outbox, HybridCache cache, CancellationToken ct) =>
        {
            var shop = await outbox.DbContext.Shops.FirstOrDefaultAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)
                       ?? throw new NotFoundException("Shop", id);
            shop.Suspend(req.Reason, req.Visible);
            await outbox.PublishAsync(new ShopSuspended(shop.PlantId, shop.Id, user.Id, req.Reason, req.Visible));
            await outbox.PublishAsync(new EvaluateShopStatus(shop.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await MerchantContext.InvalidateShopAsync(cache, shop.Id, ct);
            return Results.NoContent();
        });

        // Show or hide a suspended shop from customers without lifting the suspension.
        admin.MapPut("/{id:guid}/suspension-visibility", async (Guid id, SuspensionVisibilityRequest req, ICurrentPlant plant,
            IDbContextOutbox<ShopsDbContext> outbox, HybridCache cache, CancellationToken ct) =>
        {
            var shop = await outbox.DbContext.Shops.FirstOrDefaultAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)
                       ?? throw new NotFoundException("Shop", id);
            shop.SetVisibleWhileSuspended(req.Visible);
            await outbox.PublishAsync(new ShopProfileUpdated(shop.PlantId, shop.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await MerchantContext.InvalidateShopAsync(cache, shop.Id, ct);
            return Results.NoContent();
        });

        admin.MapPost("/{id:guid}/reinstate", async (Guid id, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ShopsDbContext> outbox, HybridCache cache, CancellationToken ct) =>
        {
            var shop = await outbox.DbContext.Shops.FirstOrDefaultAsync(s => s.Id == id && s.PlantId == plant.PlantId, ct)
                       ?? throw new NotFoundException("Shop", id);
            shop.Reinstate();
            await outbox.PublishAsync(new ShopReinstated(shop.PlantId, shop.Id, user.Id));
            await outbox.PublishAsync(new EvaluateShopStatus(shop.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await MerchantContext.InvalidateShopAsync(cache, shop.Id, ct);
            return Results.NoContent();
        });
    }
}
