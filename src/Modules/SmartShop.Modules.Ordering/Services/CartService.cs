using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Ordering.Services;

public sealed record AddToCartRequest(Guid ShopId, Guid ItemId, int Quantity, List<Guid>? OptionIds, string? Note, DateTimeOffset? SlotStart, bool ReplaceCart = false);
public sealed record UpdateCartLineRequest(int Quantity, List<Guid>? OptionIds, string? Note);

internal sealed class CartService(
    ICurrentPlant plant, ICurrentUser user, OrderingDbContext db, IShopDirectory shops, ICatalogService catalog,
    IMediaUrls media, TimeProvider clock)
{
    public async Task<CartDto> GetAsync(CancellationToken ct)
    {
        var cart = await db.Carts.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == user.Id && c.PlantId == plant.PlantId, ct);
        return await PriceAsync(cart, ct);
    }

    public async Task<CartDto> AddAsync(AddToCartRequest req, CancellationToken ct)
    {
        var shop = await shops.GetOrderingInfoAsync(req.ShopId, ct);
        if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", req.ShopId);
        var item = await catalog.GetCheckoutItemsAsync(req.ShopId, [req.ItemId], req.SlotStart ?? clock.GetUtcNow(), ct) is [var found, ..]
            ? found
            : throw new NotFoundException("Item", req.ItemId);
        var priced = LinePricer.Price(item, req.OptionIds ?? []);
        if (priced.Error is { } error) throw new DomainException("option_invalid", LinePricer.ErrorMessage(item.Name, error));
        if (item.StockMode == StockMode.Slot && req.SlotStart is null)
            throw new DomainException("slot_required", $"Please choose a time slot for \"{item.Name}\".");

        var now = clock.GetUtcNow();
        var cart = await db.Carts.FirstOrDefaultAsync(c => c.UserId == user.Id && c.PlantId == plant.PlantId, ct);
        if (cart is null)
        {
            cart = new Cart(user.Id, plant.PlantId, req.ShopId, now);
            db.Carts.Add(cart);
        }
        else if (cart.ShopId != req.ShopId)
        {
            if (cart.Lines.Count > 0 && !req.ReplaceCart)
                throw new ConflictException("cart_other_shop", "Your cart has items from another shop. Start a new cart?");
            cart.Reset(req.ShopId, now);
        }

        cart.Add(req.ItemId, req.Quantity, req.OptionIds ?? [], req.Note, req.SlotStart, now);
        await db.SaveChangesAsync(ct);
        return await PriceAsync(cart, ct);
    }

    public async Task<CartDto> UpdateAsync(Guid lineId, UpdateCartLineRequest req, CancellationToken ct)
    {
        var cart = await RequireCartAsync(ct);
        cart.Update(lineId, req.Quantity, req.OptionIds, req.Note, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await PriceAsync(cart, ct);
    }

    public async Task<CartDto> RemoveAsync(Guid lineId, CancellationToken ct)
    {
        var cart = await RequireCartAsync(ct);
        cart.Remove(lineId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await PriceAsync(cart, ct);
    }

    public async Task ClearAsync(CancellationToken ct) =>
        await db.Carts.Where(c => c.UserId == user.Id && c.PlantId == plant.PlantId).ExecuteDeleteAsync(ct);

    private async Task<Cart> RequireCartAsync(CancellationToken ct) =>
        await db.Carts.FirstOrDefaultAsync(c => c.UserId == user.Id && c.PlantId == plant.PlantId, ct)
        ?? throw new NotFoundException("Cart", user.Id);

    /// <summary>Prices with current data and flags problems (sold out, price changes, removed options) before checkout.</summary>
    private async Task<CartDto> PriceAsync(Cart? cart, CancellationToken ct)
    {
        if (cart is null || cart.Lines.Count == 0) return new CartDto(cart?.ShopId, null, [], 0, 0, false);
        var shop = await shops.GetOrderingInfoAsync(cart.ShopId, ct);
        var items = (await catalog.GetCheckoutItemsAsync(cart.ShopId, cart.Lines.Select(l => l.ItemId).Distinct().ToList(), clock.GetUtcNow(), ct))
            .ToDictionary(i => i.ItemId);

        var lines = cart.Lines.Select(l =>
        {
            if (!items.TryGetValue(l.ItemId, out var item))
                return new CartLineDto(l.Id, l.ItemId, "?", null, l.Quantity, 0, l.OptionIds, [], 0, l.Note, l.SlotStart, false, "item_removed", null);
            var priced = LinePricer.Price(item, l.OptionIds);
            var lineTotal = (priced.UnitPrice + priced.Options.Sum(o => o.PriceDelta)) * l.Quantity;
            var problem = !item.IsOrderable ? item.UnavailableReason
                : priced.Error ?? (item.Available is { } a && l.Quantity > a ? "not_enough_stock" : null);
            return new CartLineDto(l.Id, l.ItemId, item.Name, media.For(item.ImageId, MediaVariant.Small), l.Quantity, priced.UnitPrice,
                l.OptionIds, priced.Options.Select(o => new OptionDto(o.Group, o.Name, o.PriceDelta)).ToList(), lineTotal, l.Note, l.SlotStart,
                problem is null, problem, item.Available);
        }).ToList();

        return new CartDto(cart.ShopId, shop?.Name, lines, lines.Sum(l => l.LineTotal), lines.Sum(l => l.Quantity),
            lines.All(l => l.IsOrderable) && shop?.IsActive == true);
    }
}
