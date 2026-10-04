using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Payments.Data;
using SmartShop.Modules.Payments.Domain;
using Wolverine;

namespace SmartShop.Modules.Payments.Handlers;

/// <summary>Every order gets a payment with a snapshot of the chosen method (so later edits don't change it).</summary>
public static class CreatePaymentHandler
{
    public static async Task Handle(OrderPlaced e, PaymentsDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Payments.AnyAsync(p => p.OrderId == e.OrderId, ct)) return; // redelivery

        var shop = await shops.GetOrderingInfoAsync(e.ShopId, ct);
        var method = shop?.PaymentMethods.FirstOrDefault(m => m.Id == e.PaymentMethodId)
                     ?? new PaymentMethodInfo(e.PaymentMethodId, e.PaymentMethodType, e.PaymentMethodType.ToString(), null, null, null, null, null, null, null, false);
        var payment = Payment.Create(e.OrderId, e.OrderNo, e.PlantId, e.ShopId, e.CustomerId, e.Total, method, clock.GetUtcNow());
        db.Payments.Add(payment);
        await bus.PublishAsync(new PaymentCreated(e.PlantId, payment.Id, e.OrderId, e.ShopId, e.CustomerId));
    }
}

public static class VoidPaymentHandler
{
    public static Task Handle(OrderCancelled e, PaymentsDbContext db, CancellationToken ct) => Void(db, e.OrderId, ct);
    public static Task Handle(OrderRejected e, PaymentsDbContext db, CancellationToken ct) => Void(db, e.OrderId, ct);
    public static Task Handle(OrderExpired e, PaymentsDbContext db, CancellationToken ct) => Void(db, e.OrderId, ct);

    private static async Task Void(PaymentsDbContext db, Guid orderId, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);
        payment?.Void();
    }
}
