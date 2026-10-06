using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.SharedKernel;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Ordering.Services;

/// <summary>Loads orders with the right authorisation and persists state changes together with their events.</summary>
internal sealed class OrderActions(
    ICurrentPlant plant, ICurrentUser user, IShopAccess access, IDbContextOutbox<OrderingDbContext> outbox,
    IShopDirectory shops, IUserDirectory users, IMediaService media, IMediaUrls urls, TimeProvider clock)
{
    public OrderingDbContext Db => outbox.DbContext;
    public Guid UserId => user.Id;
    public DateTimeOffset Now => clock.GetUtcNow();
    public IMessageBus Bus => outbox;

    public async Task<Order> ForShopAsync(Guid orderId, ShopRole minimum, CancellationToken ct)
    {
        var order = await Db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.PlantId == plant.PlantId, ct)
                    ?? throw new NotFoundException("Order", orderId);
        await access.RequireAsync(order.ShopId, minimum, ct);
        return order;
    }

    public async Task<Order> ForCustomerAsync(Guid orderId, CancellationToken ct) =>
        await Db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.PlantId == plant.PlantId && o.CustomerId == user.Id, ct)
        ?? throw new NotFoundException("Order", orderId);

    /// <summary>
    /// Customer, any shop member, or a plant admin may view an order. A shop member who ordered from their own
    /// shop is the customer by default; <paramref name="asShop"/> (the merchant screens) gives them the shop view.
    /// </summary>
    public async Task<(Order Order, string Role)> ForViewerAsync(Guid orderId, CancellationToken ct, bool asShop = false)
    {
        var order = await Db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.PlantId == plant.PlantId, ct)
                    ?? throw new NotFoundException("Order", orderId);
        var isShopMember = await access.GetRoleAsync(order.ShopId, ct) is not null;
        if (asShop && isShopMember) return (order, "shop");
        if (order.CustomerId == user.Id) return (order, "customer");
        if (isShopMember) return (order, "shop");
        if (plant.Membership.Role == PlantRole.PlantAdmin) return (order, "admin");
        throw new NotFoundException("Order", orderId);
    }

    public async Task SaveAsync(CancellationToken ct, params IntegrationEvent[] events)
    {
        foreach (var e in events) await outbox.PublishAsync(e);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }

    public async Task<Guid?> ValidatePhotoAsync(Guid? photoId, CancellationToken ct)
    {
        if (photoId is { } id) await media.RequireOwnedAsync(id, user.Id, MediaPurpose.DeliveryPhoto, ct);
        return photoId;
    }

    public Task<ShopOrderingInfo?> ShopAsync(Guid shopId, CancellationToken ct) => shops.GetOrderingInfoAsync(shopId, ct);

    public async Task<OrderDto> ToDtoAsync(Order order, string role, CancellationToken ct)
    {
        var actorIds = order.Timeline.Where(t => t.ActorId is not null).Select(t => t.ActorId!.Value)
            .Append(order.CustomerId).Concat(order.AcceptedBy is { } a ? [a] : []).Distinct().ToList();
        var people = await users.GetProfilesAsync(actorIds, ct);
        var labels = (await shops.GetMembersAsync(order.ShopId, ct)).ToDictionary(m => m.UserId, m => m.DisplayLabel);
        return OrderMapper.ToDto(order, people, labels, urls, role);
    }

    public async Task<List<OrderSummaryDto>> SummariesAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var people = await users.GetProfilesAsync(orders.Select(o => o.CustomerId), ct);
        return orders.Select(o => OrderMapper.Summary(o, people)).ToList();
    }
}
