using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Promotions;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Promotions;

public sealed record PreviewRequest(string? Code, decimal Subtotal);
public sealed record PromotionDto(Guid Id, string? Code, string Title, PromotionType Type, decimal Value, decimal? MinOrder, decimal? MaxDiscount,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UsageLimit, int? PerUserLimit, int UsedCount, bool AutoApply, bool Active);
public sealed record PromotionBadgeDto(Guid Id, string Title, string Description, decimal? MinOrder, DateTimeOffset? EndsAt);

public sealed class PromotionsModule : IModule
{
    public string Name => "Promotions";

    public IReadOnlyList<Type> DbContexts => [typeof(PromotionsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<PromotionsDbContext>(PromotionsDbContext.SchemaName);
        builder.Services.AddScoped<IPromotionService, PromotionService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var customer = app.MapGroup("/shops/{shopId:guid}/promotions").WithTags("Promotions").RequirePlantMember();

        customer.MapPost("/preview", async (Guid shopId, PreviewRequest req, ICurrentPlant plant, ICurrentUser user, IShopDirectory shops,
            IPromotionService promotions, TimeProvider clock, CancellationToken ct) =>
        {
            await RequireShopInPlantAsync(shops, shopId, plant, ct);
            var result = await promotions.EvaluateAsync(shopId, user.Id, req.Code, req.Subtotal, clock.GetUtcNow(), ct);
            if (result is null && !string.IsNullOrWhiteSpace(req.Code))
                throw new DomainException("coupon_invalid", "This coupon cannot be used for this order.");
            return Results.Json(result);
        });

        // Automatic promotions shown on the shop page (coupon codes stay private to whoever the shop shares them with).
        customer.MapGet("", async (Guid shopId, ICurrentPlant plant, IShopDirectory shops, PromotionsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await RequireShopInPlantAsync(shops, shopId, plant, ct);
            var now = clock.GetUtcNow();
            var list = await db.Promotions.AsNoTracking().Where(p => p.ShopId == shopId && p.Active && p.AutoApply).ToListAsync(ct);
            return list.Where(p => p.IsLive(now)).Select(p => new PromotionBadgeDto(p.Id, p.Title, p.Describe(), p.MinOrder, p.EndsAt)).ToList();
        });

        var merchant = app.MapGroup("/merchant").WithTags("Merchant promotions").RequirePlantMember();

        merchant.MapGet("/shops/{shopId:guid}/promotions", async (Guid shopId, IShopAccess access, PromotionsDbContext db, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Manager, ct);
            var list = await db.Promotions.AsNoTracking().Where(p => p.ShopId == shopId).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
            return list.Select(ToDto).ToList();
        });

        merchant.MapPost("/shops/{shopId:guid}/promotions", async (Guid shopId, PromotionInput req, ICurrentPlant plant, IShopAccess access,
            IShopDirectory shops, PromotionsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Manager, ct);
            await RequireShopInPlantAsync(shops, shopId, plant, ct);
            var promotion = Promotion.Create(shopId, plant.PlantId, req, clock.GetUtcNow());
            await EnsureCodeFreeAsync(db, promotion, ct);
            db.Promotions.Add(promotion);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/merchant/promotions/{promotion.Id}", ToDto(promotion));
        });

        merchant.MapPut("/promotions/{id:guid}", async (Guid id, PromotionInput req, IShopAccess access, PromotionsDbContext db, ICurrentPlant plant, CancellationToken ct) =>
        {
            var promotion = await FindAsync(db, id, plant, ct);
            await access.RequireAsync(promotion.ShopId, ShopRole.Manager, ct);
            promotion.Update(req);
            await EnsureCodeFreeAsync(db, promotion, ct);
            await db.SaveChangesAsync(ct);
            return ToDto(promotion);
        });

        // Used promotions are kept (orders reference them) and only switched off.
        merchant.MapDelete("/promotions/{id:guid}", async (Guid id, IShopAccess access, PromotionsDbContext db, ICurrentPlant plant, CancellationToken ct) =>
        {
            var promotion = await FindAsync(db, id, plant, ct);
            await access.RequireAsync(promotion.ShopId, ShopRole.Manager, ct);
            if (await db.Redemptions.AnyAsync(r => r.PromotionId == id, ct))
                promotion.Update(Input(promotion) with { Active = false });
            else
                db.Promotions.Remove(promotion);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task RequireShopInPlantAsync(IShopDirectory shops, Guid shopId, ICurrentPlant plant, CancellationToken ct)
    {
        var shop = await shops.GetOrderingInfoAsync(shopId, ct);
        if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", shopId);
    }

    private static async Task<Promotion> FindAsync(PromotionsDbContext db, Guid id, ICurrentPlant plant, CancellationToken ct) =>
        await db.Promotions.FirstOrDefaultAsync(p => p.Id == id && p.PlantId == plant.PlantId, ct) ?? throw new NotFoundException("Promotion", id);

    private static async Task EnsureCodeFreeAsync(PromotionsDbContext db, Promotion promotion, CancellationToken ct)
    {
        if (promotion.Code is not null && await db.Promotions.AnyAsync(p => p.ShopId == promotion.ShopId && p.Code == promotion.Code && p.Id != promotion.Id, ct))
            throw new ConflictException("coupon_code_taken", "This shop already has a promotion with this code.");
    }

    private static PromotionInput Input(Promotion p) =>
        new(p.Code, p.Title, p.Type, p.Value, p.MinOrder, p.MaxDiscount, p.StartsAt, p.EndsAt, p.UsageLimit, p.PerUserLimit, p.AutoApply, p.Active);

    private static PromotionDto ToDto(Promotion p) =>
        new(p.Id, p.Code, p.Title, p.Type, p.Value, p.MinOrder, p.MaxDiscount, p.StartsAt, p.EndsAt, p.UsageLimit, p.PerUserLimit, p.UsedCount, p.AutoApply, p.Active);
}

[RequiresEagerTransaction]
public static class PromotionsOrderCancelledHandler
{
    public static Task Handle(OrderCancelled e, PromotionsDbContext db, CancellationToken ct) =>
        e.PromotionId is null ? Task.CompletedTask : PromotionService.ReleaseAsync(db, e.OrderId, ct);
}

[RequiresEagerTransaction]
public static class PromotionsOrderRejectedHandler
{
    public static Task Handle(OrderRejected e, PromotionsDbContext db, CancellationToken ct) =>
        e.PromotionId is null ? Task.CompletedTask : PromotionService.ReleaseAsync(db, e.OrderId, ct);
}

[RequiresEagerTransaction]
public static class PromotionsOrderExpiredHandler
{
    public static Task Handle(OrderExpired e, PromotionsDbContext db, CancellationToken ct) =>
        e.PromotionId is null ? Task.CompletedTask : PromotionService.ReleaseAsync(db, e.OrderId, ct);
}
