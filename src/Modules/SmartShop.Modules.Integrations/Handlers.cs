using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.SharedKernel;
using Wolverine;

namespace SmartShop.Modules.Integrations;

/// <summary>Sends (or retries) one webhook delivery; runs in the worker.</summary>
public sealed record DeliverWebhook(Guid DeliveryId) : IScheduledCommand;

public sealed record WebhookOrderData(OrderExport Order, string? CustomerName);

internal static class WebhookFanout
{
    /// <summary>Creates a delivery for every active endpoint of the shop subscribed to the event.</summary>
    public static async Task QueueAsync(IntegrationsDbContext db, IMessageBus bus, TimeProvider clock, Guid shopId, string eventType,
        Func<Task<object?>> data, CancellationToken ct)
    {
        var endpoints = await db.Webhooks.Where(w => w.ShopId == shopId && w.Active).ToListAsync(ct);
        endpoints = endpoints.Where(w => w.Events.Contains(eventType)).ToList();
        if (endpoints.Count == 0) return;

        var payloadData = await data();
        if (payloadData is null) return;
        var now = clock.GetUtcNow();
        foreach (var endpoint in endpoints)
        {
            var id = Ids.New();
            var payload = JsonSerializer.Serialize(new { id, type = eventType, createdAt = now, shopId, data = payloadData }, WebhookSender.Json);
            db.Deliveries.Add(WebhookDelivery.Create(id, endpoint, eventType, payload, now));
            await bus.PublishAsync(new DeliverWebhook(id));
        }
    }

    public static Func<Task<object?>> Order(IOrderDirectory orders, IUserDirectory users, Guid shopId, Guid orderId, CancellationToken ct) => async () =>
    {
        var order = await orders.GetForShopAsync(shopId, orderId, ct);
        if (order is null) return null;
        var customer = (await users.GetProfilesAsync([order.CustomerId], ct)).GetValueOrDefault(order.CustomerId);
        return new WebhookOrderData(order, customer?.DisplayName);
    };
}

/// <summary>Order and payment events that shops can subscribe to (one method per event: Wolverine dispatches on concrete types).</summary>
public static class WebhookEventHandler
{
    public static Task Handle(OrderPlaced e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.placed", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderAccepted e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.accepted", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderPreparing e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.preparing", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderReady e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.ready", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderDelivered e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.delivered", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderCompleted e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.completed", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderCancelled e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.cancelled", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderRejected e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.rejected", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(OrderExpired e, IntegrationsDbContext db, IMessageBus bus, IOrderDirectory orders, IUserDirectory users, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "order.expired", WebhookFanout.Order(orders, users, e.ShopId, e.OrderId, ct), ct);

    public static Task Handle(PaymentVerified e, IntegrationsDbContext db, IMessageBus bus, TimeProvider clock, CancellationToken ct) =>
        WebhookFanout.QueueAsync(db, bus, clock, e.ShopId, "payment.verified",
            () => Task.FromResult<object?>(new { e.OrderId, e.OrderNo, e.PaymentId, automatic = e.ActorId == Guid.Empty }), ct);
}

public static class DeliverWebhookHandler
{
    public static async Task Handle(DeliverWebhook command, IntegrationsDbContext db, WebhookSender sender, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == command.DeliveryId, ct);
        if (delivery is not { Status: DeliveryStatus.Pending }) return;
        var endpoint = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == delivery.EndpointId, ct);
        if (endpoint is not { Active: true })
        {
            delivery.Skip();
            return;
        }

        var (ok, status, error) = await sender.SendAsync(endpoint, delivery, ct);
        var now = clock.GetUtcNow();
        if (ok)
        {
            delivery.Succeeded(status!.Value, now);
            endpoint.RecordSuccess();
            return;
        }

        endpoint.RecordFailure();
        if (delivery.Failed(status, error ?? "error", now) is not { } retryIn) return;
        if (endpoint.Active) await bus.ScheduleAsync(new DeliverWebhook(delivery.Id), retryIn);
        else delivery.Skip(); // the endpoint was just disabled after too many failures
    }
}
