using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts;
using SmartShop.Contracts.Ordering;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.Modules.Ordering.Services;

namespace SmartShop.Modules.Ordering.Endpoints;

public sealed record ReasonRequest(string? Reason);

internal static class CustomerOrderEndpoints
{
    private static readonly OrderStatus[] Active =
        [OrderStatus.PendingAcceptance, OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.Ready, OrderStatus.OutForDelivery, OrderStatus.Delivered];

    public static void Map(IEndpointRouteBuilder api)
    {
        var cart = api.MapGroup("/cart").WithTags("Cart").RequirePlantMember();
        cart.MapGet("/", (CartService carts, CancellationToken ct) => carts.GetAsync(ct));
        cart.MapPost("/items", (AddToCartRequest req, CartService carts, CancellationToken ct) => carts.AddAsync(req, ct));
        cart.MapPut("/items/{lineId:guid}", (Guid lineId, UpdateCartLineRequest req, CartService carts, CancellationToken ct) => carts.UpdateAsync(lineId, req, ct));
        cart.MapDelete("/items/{lineId:guid}", (Guid lineId, CartService carts, CancellationToken ct) => carts.RemoveAsync(lineId, ct));
        cart.MapDelete("/", async (CartService carts, CancellationToken ct) =>
        {
            await carts.ClearAsync(ct);
            return Results.NoContent();
        });

        var orders = api.MapGroup("/orders").WithTags("Orders").RequirePlantMember();

        orders.MapPost("/", async (CheckoutRequest req, HttpRequest http, CheckoutService checkout, OrderActions actions, CancellationToken ct) =>
        {
            var (order, created) = await checkout.CheckoutAsync(req, http.Headers["Idempotency-Key"].ToString(), ct);
            var dto = await actions.ToDtoAsync(order, "customer", ct);
            return created ? Results.Created($"/api/orders/{order.Id}", dto) : Results.Ok(dto);
        });

        orders.MapGet("/", async (string? scope, int? page, ICurrentPlant plant, ICurrentUser user, OrderingDbContext db, OrderActions actions, CancellationToken ct) =>
        {
            var size = 20;
            var number = Math.Max(1, page ?? 1);
            var query = db.Orders.AsNoTracking().Where(o => o.PlantId == plant.PlantId && o.CustomerId == user.Id);
            query = scope == "history" ? query.Where(o => !Active.Contains(o.Status)) : query.Where(o => Active.Contains(o.Status));
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(o => o.PlacedAt).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return new PagedResult<OrderSummaryDto>(await actions.SummariesAsync(rows, ct), total, number, size);
        });

        orders.MapGet("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "as")] string? viewAs, OrderActions actions, CancellationToken ct) =>
        {
            var (order, role) = await actions.ForViewerAsync(id, ct, viewAs == "shop");
            return await actions.ToDtoAsync(order, role, ct);
        });

        orders.MapPost("/{id:guid}/cancel", async (Guid id, ReasonRequest req, OrderActions a, CancellationToken ct) =>
        {
            var o = await a.ForCustomerAsync(id, ct);
            o.CancelByCustomer(a.UserId, req.Reason, a.Now);
            await a.SaveAsync(ct, new OrderCancelled(o.PlantId, o.Id, o.OrderNo, o.ShopId, o.ShopName, o.CustomerId, a.UserId, true, req.Reason, o.PromotionId, o.LineInfos()));
            return await a.ToDtoAsync(o, "customer", ct);
        });

        orders.MapPost("/{id:guid}/request-cancel", async (Guid id, ReasonRequest req, OrderActions a, CancellationToken ct) =>
        {
            var o = await a.ForCustomerAsync(id, ct);
            o.RequestCancel(a.UserId, req.Reason, a.Now);
            await a.SaveAsync(ct, new OrderCancellationRequested(o.PlantId, o.Id, o.OrderNo, o.ShopId, o.CustomerId, req.Reason));
            return await a.ToDtoAsync(o, "customer", ct);
        });

        // Customer confirms receipt -> Completed -> stock is deducted.
        orders.MapPost("/{id:guid}/confirm-received", async (Guid id, OrderActions a, CancellationToken ct) =>
        {
            var o = await a.ForCustomerAsync(id, ct);
            o.ConfirmReceived(a.UserId, a.Now);
            await a.SaveAsync(ct, new OrderCompleted(o.PlantId, o.Id, o.OrderNo, o.ShopId, o.ShopName, o.CustomerId, false, o.Total, o.LineInfos()));
            return await a.ToDtoAsync(o, "customer", ct);
        });

        // Put the same items back in the cart.
        orders.MapPost("/{id:guid}/reorder", async (Guid id, OrderActions a, CartService carts, CancellationToken ct) =>
        {
            var o = await a.ForCustomerAsync(id, ct);
            CartDto? cartDto = null;
            var first = true;
            foreach (var line in o.Lines.Where(l => l.SlotStart is null))
            {
                try
                {
                    cartDto = await carts.AddAsync(new AddToCartRequest(o.ShopId, line.ItemId, line.Quantity, null, line.Note, null, first), ct);
                    first = false;
                }
                catch (SharedKernel.DomainException) { /* item gone or options changed: skip */ }
                catch (SharedKernel.NotFoundException) { }
            }
            return cartDto ?? await carts.GetAsync(ct);
        });
    }
}
