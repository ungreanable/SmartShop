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
        // Custom dishes: the amount is only known once the customer agrees to the shop's prices (OrderQuoteAccepted).
        if (e.AwaitingQuote) return;
        await CreateAsync(db, shops, bus, clock, e.PlantId, e.OrderId, e.OrderNo, e.ShopId, e.CustomerId, e.Total, e.PaymentMethodId, e.PaymentMethodType, ct);
    }

    public static Task Handle(OrderQuoteAccepted e, PaymentsDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, CancellationToken ct) =>
        CreateAsync(db, shops, bus, clock, e.PlantId, e.OrderId, e.OrderNo, e.ShopId, e.CustomerId, e.Total, e.PaymentMethodId, e.PaymentMethodType, ct);

    private static async Task CreateAsync(PaymentsDbContext db, IShopDirectory shops, IMessageBus bus, TimeProvider clock, Guid plantId, Guid orderId,
        string orderNo, Guid shopId, Guid customerId, decimal total, Guid methodId, PaymentMethodType methodType, CancellationToken ct)
    {
        if (await db.Payments.AnyAsync(p => p.OrderId == orderId, ct)) return; // redelivery

        var shop = await shops.GetOrderingInfoAsync(shopId, ct);
        var method = shop?.PaymentMethods.FirstOrDefault(m => m.Id == methodId)
                     ?? new PaymentMethodInfo(methodId, methodType, methodType.ToString(), null, null, null, null, null, null, null, false);
        var payment = Payment.Create(orderId, orderNo, plantId, shopId, customerId, total, method, clock.GetUtcNow());
        db.Payments.Add(payment);
        await bus.PublishAsync(new PaymentCreated(plantId, payment.Id, orderId, shopId, customerId));
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
