using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Promotions;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Ordering.Services;

public sealed record CheckoutRequest(
    FulfillmentType FulfillmentType, DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo,
    DeliveryAddress? DeliveryAddress, Guid PaymentMethodId, string? Note, string? CouponCode);

/// <summary>
/// Turns the cart into an order. Validation happens first; then, in one database transaction, the order number is
/// allocated, stock is reserved atomically (Catalog), a coupon is redeemed (Promotions), the order is stored and
/// OrderPlaced is written to the outbox. Either everything commits or nothing does.
/// </summary>
internal sealed class CheckoutService(
    ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<OrderingDbContext> outbox,
    IShopDirectory shops, ICatalogService catalog, IServiceProvider services, TimeProvider clock)
{
    public async Task<(Order Order, bool Created)> CheckoutAsync(CheckoutRequest req, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not { Length: >= 8 and <= 64 })
            throw new DomainException("idempotency_key_required", "Header Idempotency-Key (8-64 characters) is required.");

        var db = outbox.DbContext;
        var existing = await db.Orders.FirstOrDefaultAsync(o => o.CustomerId == user.Id && o.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return (existing, false);

        var cart = await db.Carts.FirstOrDefaultAsync(c => c.UserId == user.Id && c.PlantId == plant.PlantId, ct);
        if (cart is null || cart.Lines.Count == 0) throw new DomainException("cart_empty", "Your cart is empty.");

        var shop = await shops.GetOrderingInfoAsync(cart.ShopId, ct);
        if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", cart.ShopId);
        if (!shop.IsActive) throw new DomainException("shop_unavailable", "This shop is not available.");
        if (req.FulfillmentType == FulfillmentType.Pickup && !shop.PickupEnabled
            || req.FulfillmentType == FulfillmentType.Delivery && !shop.DeliveryEnabled)
            throw new DomainException("fulfillment_unavailable", "The shop does not offer this option.");

        var now = clock.GetUtcNow();
        var items = await catalog.GetCheckoutItemsAsync(shop.ShopId, cart.Lines.Select(l => l.ItemId).Distinct().ToList(), req.ScheduledFrom ?? now, ct);
        var (from, to, round) = ResolveTime(req, shop, items, now);

        var byId = items.ToDictionary(i => i.ItemId);
        foreach (var group in cart.Lines.GroupBy(l => l.ItemId))
        {
            var item = byId.GetValueOrDefault(group.Key) ?? throw new DomainException("item_unavailable", "An item in your cart is no longer available.");
            LinePricer.ThrowIfUnavailable(item, group.Sum(l => l.Quantity));
        }

        var lines = new List<OrderLine>();
        var reservations = new List<ReserveLine>();
        foreach (var cl in cart.Lines)
        {
            var item = byId[cl.ItemId];
            if (item.AllowedFulfillment is { } allowed && !allowed.Contains(req.FulfillmentType))
                throw new DomainException("fulfillment_item", $"\"{item.Name}\" is only available for {string.Join("/", allowed)}.");
            if (item.StockMode == StockMode.Slot && cl.SlotStart is null)
                throw new DomainException("slot_required", $"Please choose a time slot for \"{item.Name}\".");
            var priced = LinePricer.Price(item, cl.OptionIds);
            if (priced.Error is { } error) throw new DomainException("option_invalid", LinePricer.ErrorMessage(item.Name, error));
            lines.Add(new OrderLine
            {
                Id = Ids.New(), ItemId = item.ItemId, Name = item.Name, Quantity = cl.Quantity, UnitPrice = priced.UnitPrice,
                Options = priced.Options, Note = cl.Note, SlotStart = cl.SlotStart,
            });
            reservations.Add(new ReserveLine(item.ItemId, cl.Quantity, cl.SlotStart));
        }

        var subtotal = lines.Sum(l => l.LineTotal);
        if (req.FulfillmentType == FulfillmentType.Delivery && shop.DeliveryMinOrder is { } min && subtotal < min)
            throw new DomainException("min_order", $"Delivery requires a minimum order of {min:N0} baht.");

        var method = shop.PaymentMethods.FirstOrDefault(p => p.Id == req.PaymentMethodId)
                     ?? throw new DomainException("payment_method_invalid", "Please choose one of the shop's payment methods.");
        var address = ResolveAddress(req);

        var promotions = services.GetService<IPromotionService>();
        DiscountResult? discount = null;
        if (promotions is not null)
        {
            discount = await promotions.EvaluateAsync(shop.ShopId, user.Id, req.CouponCode, subtotal, now, ct);
            if (!string.IsNullOrWhiteSpace(req.CouponCode) && discount is null)
                throw new DomainException("coupon_invalid", "This coupon cannot be used for this order.");
        }

        var orderId = Ids.New();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var dbTx = tx.GetDbTransaction();
            var orderNo = await NextOrderNoAsync(dbTx, shop, now, ct);
            await catalog.ReserveAsync(dbTx, orderId, reservations, user.Id, ct);
            if (discount is not null && promotions is not null)
                await promotions.RedeemAsync(dbTx, discount.PromotionId, orderId, user.Id, discount.Discount, ct);

            var order = Order.Place(new PlaceOrder(orderId, plant.PlantId, shop.ShopId, shop.Name, user.Id, orderNo,
                req.FulfillmentType, from, to, address, req.Note, lines, Math.Min(discount?.Discount ?? 0, subtotal),
                discount?.PromotionId, discount?.Code, method, round?.Id, idempotencyKey, shop.AcceptTimeoutMinutes), now);
            db.Orders.Add(order);
            db.Carts.Remove(cart);

            await outbox.PublishAsync(new OrderPlaced(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId,
                order.Subtotal, order.Discount, order.Total, order.FulfillmentType, method.Id, method.Type, from, to, order.PromotionId,
                order.LineInfos()));

            if (shop.AcceptMode == AcceptMode.Auto)
            {
                order.Accept(null, now);
                await outbox.PublishAsync(new OrderAccepted(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, Guid.Empty));
            }
            else
            {
                await outbox.ScheduleAsync(new ExpireOrderIfNotAccepted(order.Id), order.ExpiresAt!.Value);
                await outbox.ScheduleAsync(new RemindShopPendingOrder(order.Id, 1), now.AddMinutes(shop.ReminderAfterMinutes));
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await outbox.FlushOutgoingMessagesAsync();
            return (order, true);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request with the same Idempotency-Key won the race.
            db.ChangeTracker.Clear();
            var winner = await db.Orders.FirstOrDefaultAsync(o => o.CustomerId == user.Id && o.IdempotencyKey == idempotencyKey, ct);
            return winner is not null ? (winner, false) : throw new ConflictException("checkout_conflict", "Please try again.");
        }
    }

    private static (DateTimeOffset? From, DateTimeOffset? To, PreOrderRoundInfo? Round) ResolveTime(
        CheckoutRequest req, ShopOrderingInfo shop, IReadOnlyList<CheckoutItem> items, DateTimeOffset now)
    {
        var rounds = items.Where(i => i.Round is not null).Select(i => i.Round!).DistinctBy(r => r.Id).ToList();
        if (rounds.Count > 1 || rounds.Count == 1 && items.Any(i => i.Round is null))
            throw new DomainException("mixed_rounds", "Pre-order items must be ordered separately from other items.");
        if (rounds.Count == 1) return (rounds[0].FulfillFrom, rounds[0].FulfillTo, rounds[0]);

        var status = ShopScheduleEvaluator.Evaluate(shop.Schedule, now);
        if (status.State == ShopOpenState.Busy)
            throw new DomainException("shop_busy", "The shop is busy and not taking new orders right now. Please try again later.");

        if (req.ScheduledFrom is not { } from)
        {
            if (!status.AcceptingOrders)
                throw shop.AllowPreorderWhenClosed && status.Reason is not (ShopClosedReason.Suspended or ShopClosedReason.Vacation)
                    ? new DomainException("schedule_required", "The shop is closed now. Please choose a time to pick up / receive your order.")
                    : new DomainException("shop_closed", "The shop is closed.");
            return (null, null, null);
        }

        if (req.ScheduledTo is not { } to || to <= from) throw new DomainException("validation", "Please choose a valid time window.");
        if (!status.AcceptingOrders && !shop.AllowPreorderWhenClosed) throw new DomainException("shop_closed", "The shop is closed.");
        var windows = ShopScheduleEvaluator.SelectableWindows(shop.Schedule, now, 7, shop.SlotIntervalMinutes, shop.PrepTimeMinutes);
        if (!windows.Any(w => w.Start == from && w.End == to))
            throw new DomainException("window_invalid", "The selected time is no longer available. Please choose another time.");
        return (from, to, null);
    }

    private DeliveryAddress? ResolveAddress(CheckoutRequest req)
    {
        if (req.FulfillmentType != FulfillmentType.Delivery) return null;
        var membership = plant.Membership;
        var address = new DeliveryAddress
        {
            HouseNo = Guard.Optional(req.DeliveryAddress?.HouseNo, "House number", 30) ?? membership.HouseNo,
            Soi = Guard.Optional(req.DeliveryAddress?.Soi, "Soi", 60) ?? membership.Soi,
            Note = Guard.Optional(req.DeliveryAddress?.Note, "Delivery note", 200),
        };
        if (string.IsNullOrWhiteSpace(address.HouseNo)) throw new DomainException("address_required", "Please enter your house number for delivery.");
        return address;
    }

    private static async Task<string> NextOrderNoAsync(DbTransaction tx, ShopOrderingInfo shop, DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ShopScheduleEvaluator.ResolveTimeZone(shop.Schedule.TimeZoneId)).DateTime);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ordering.order_counters (shop_id, day, last) VALUES (@shop, @day, 1)
            ON CONFLICT (shop_id, day) DO UPDATE SET last = ordering.order_counters.last + 1
            RETURNING last
            """, (NpgsqlConnection)tx.Connection!, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("shop", shop.ShopId);
        cmd.Parameters.AddWithValue("day", day);
        var number = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return $"{shop.Code}-{number:000}";
    }
}
