using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Ordering.Data;
using Wolverine;

namespace SmartShop.Modules.Ordering.Handlers;

/// <summary>The shop did not accept in time: expire the order and release its stock.</summary>
public static class ExpireOrderHandler
{
    public static async Task Handle(ExpireOrderIfNotAccepted command, OrderingDbContext db, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null || !order.Expire(clock.GetUtcNow())) return;
        await bus.PublishAsync(new OrderExpired(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId,
            order.PromotionId, order.LineInfos()));
    }
}

/// <summary>Nobody accepted yet: remind every shop member again, until the order expires.</summary>
public static class RemindPendingOrderHandler
{
    public static async Task Handle(RemindShopPendingOrder command, OrderingDbContext db, IShopDirectory shops, IMessageBus bus,
        TimeProvider clock, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is not { Status: OrderStatus.PendingAcceptance } || order.ExpiresAt is not { } expires) return;

        await bus.PublishAsync(new OrderPendingReminder(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, command.Attempt));

        var interval = TimeSpan.FromMinutes((await shops.GetOrderingInfoAsync(order.ShopId, ct))?.ReminderAfterMinutes ?? 3);
        var next = clock.GetUtcNow() + interval;
        if (next < expires) await bus.ScheduleAsync(new RemindShopPendingOrder(order.Id, command.Attempt + 1), next);
    }
}

/// <summary>The customer did not confirm receipt: close the order automatically so it never hangs forever.</summary>
public static class AutoCompleteOrderHandler
{
    public static async Task Handle(AutoCompleteOrder command, OrderingDbContext db, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null) return;
        var now = clock.GetUtcNow();
        if (order.AutoCompleteAt is { } due && due > now)
        {
            await bus.ScheduleAsync(command, due); // delivered again later than planned
            return;
        }
        if (!order.AutoComplete(now)) return;
        await bus.PublishAsync(new OrderCompleted(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId,
            true, order.Total, order.LineInfos()));
    }
}

/// <summary>Group buy did not reach its minimum: cancel every order of the round.</summary>
public static class PreOrderRoundClosedHandler
{
    public static async Task Handle(PreOrderRoundClosed e, OrderingDbContext db, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        if (e.TargetReached) return;
        var orders = await db.Orders.Where(o => o.PreOrderRoundId == e.RoundId && o.ClosedAt == null).ToListAsync(ct);
        var reason = $"รอบสั่งล่วงหน้า \"{e.Title}\" ไม่ถึงยอดขั้นต่ำ";
        foreach (var order in orders)
        {
            order.CancelBySystem(reason, clock.GetUtcNow());
            await bus.PublishAsync(new OrderCancelled(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId,
                null, false, reason, order.PromotionId, order.LineInfos()));
        }
    }
}

/// <summary>Keeps the payment status on the order for list views and the "pay before preparing" rule.</summary>
public static class PaymentStatusHandler
{
    public static Task Handle(PaymentProofUploaded e, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        Set(db, e.OrderId, PaymentStatus.PendingVerification, clock, e.CustomerId, null, ct);

    public static Task Handle(PaymentVerified e, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        Set(db, e.OrderId, PaymentStatus.Paid, clock, e.ActorId, null, ct);

    public static Task Handle(PaymentRejected e, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        Set(db, e.OrderId, PaymentStatus.Rejected, clock, e.ActorId, e.Reason, ct);

    private static async Task Set(OrderingDbContext db, Guid orderId, PaymentStatus status, TimeProvider clock, Guid? actor, string? note, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        order?.SetPaymentStatus(status, clock.GetUtcNow(), actor, note);
    }
}

public static class OrderingUserErasedHandler
{
    public static async Task Handle(UserErased e, OrderingDbContext db, CancellationToken ct)
    {
        var orders = await db.Orders.Where(o => o.CustomerId == e.UserId).ToListAsync(ct);
        foreach (var order in orders) order.Anonymise();
        await db.Carts.Where(c => c.UserId == e.UserId).ExecuteDeleteAsync(ct);
    }
}
