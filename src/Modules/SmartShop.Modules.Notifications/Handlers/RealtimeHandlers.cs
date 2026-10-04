using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Realtime;

namespace SmartShop.Modules.Notifications.Handlers;

/// <summary>
/// Live screen updates through SignalR (separate from the inbox): order boards, tracking pages, the
/// "new order" alarm on every device of the shop and stopping it once someone accepted.
/// </summary>
public static class RealtimeOrderHandler
{
    public static async Task Handle(OrderPlaced e, IRealtimePublisher rt, CancellationToken ct)
    {
        await rt.ToShopAsync(e.ShopId, RealtimeEvents.NewOrder, new { e.OrderId, e.OrderNo, e.Total, items = e.Lines.Sum(l => l.Quantity) }, ct);
        await rt.ToUserAsync(e.CustomerId, RealtimeEvents.OrderUpdated, new { e.OrderId, status = "PendingAcceptance" }, ct);
    }

    public static async Task Handle(OrderAccepted e, IRealtimePublisher rt, CancellationToken ct)
    {
        // Every other device of the shop stops ringing: "accepted by X".
        await rt.ToShopAsync(e.ShopId, RealtimeEvents.OrderClaimed, new { e.OrderId, e.OrderNo, by = e.ActorId }, ct);
        await Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.Accepted, ct);
    }

    public static Task Handle(OrderRejected e, IRealtimePublisher rt, CancellationToken ct) => Claimed(rt, e.ShopId, e.CustomerId, e.OrderId, e.OrderNo, OrderStatus.Rejected, ct);
    public static Task Handle(OrderExpired e, IRealtimePublisher rt, CancellationToken ct) => Claimed(rt, e.ShopId, e.CustomerId, e.OrderId, e.OrderNo, OrderStatus.Expired, ct);
    public static Task Handle(OrderCancelled e, IRealtimePublisher rt, CancellationToken ct) => Claimed(rt, e.ShopId, e.CustomerId, e.OrderId, e.OrderNo, OrderStatus.Cancelled, ct);
    public static Task Handle(OrderPreparing e, IRealtimePublisher rt, CancellationToken ct) => Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.Preparing, ct);
    public static Task Handle(OrderReady e, IRealtimePublisher rt, CancellationToken ct) => Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.Ready, ct);
    public static Task Handle(OrderOutForDelivery e, IRealtimePublisher rt, CancellationToken ct) => Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.OutForDelivery, ct);
    public static Task Handle(OrderDelivered e, IRealtimePublisher rt, CancellationToken ct) => Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.Delivered, ct);
    public static Task Handle(OrderCompleted e, IRealtimePublisher rt, CancellationToken ct) => Updated(rt, e.ShopId, e.CustomerId, e.OrderId, OrderStatus.Completed, ct);
    public static Task Handle(OrderCancellationRequested e, IRealtimePublisher rt, CancellationToken ct) => rt.ToShopAsync(e.ShopId, RealtimeEvents.OrderUpdated, new { e.OrderId, cancelRequested = true }, ct);

    public static async Task Handle(OrderMessagePosted e, IRealtimePublisher rt, CancellationToken ct)
    {
        await rt.ToShopAsync(e.ShopId, RealtimeEvents.OrderMessage, new { e.OrderId, e.Preview }, ct);
        await rt.ToUserAsync(e.CustomerId, RealtimeEvents.OrderMessage, new { e.OrderId, e.Preview }, ct);
    }

    private static async Task Claimed(IRealtimePublisher rt, Guid shopId, Guid customerId, Guid orderId, string orderNo, OrderStatus status, CancellationToken ct)
    {
        await rt.ToShopAsync(shopId, RealtimeEvents.OrderClaimed, new { orderId, orderNo, by = (Guid?)null }, ct);
        await Updated(rt, shopId, customerId, orderId, status, ct);
    }

    private static async Task Updated(IRealtimePublisher rt, Guid shopId, Guid customerId, Guid orderId, OrderStatus status, CancellationToken ct)
    {
        await rt.ToShopAsync(shopId, RealtimeEvents.OrderUpdated, new { orderId, status = status.ToString() }, ct);
        await rt.ToUserAsync(customerId, RealtimeEvents.OrderUpdated, new { orderId, status = status.ToString() }, ct);
    }
}

public static class RealtimePaymentHandler
{
    public static Task Handle(PaymentCreated e, IRealtimePublisher rt, CancellationToken ct) => Send(rt, e.ShopId, e.CustomerId, e.OrderId, "Unpaid", ct);
    public static Task Handle(PaymentProofUploaded e, IRealtimePublisher rt, CancellationToken ct) => Send(rt, e.ShopId, e.CustomerId, e.OrderId, "PendingVerification", ct);
    public static Task Handle(PaymentVerified e, IRealtimePublisher rt, CancellationToken ct) => Send(rt, e.ShopId, e.CustomerId, e.OrderId, "Paid", ct);
    public static Task Handle(PaymentRejected e, IRealtimePublisher rt, CancellationToken ct) => Send(rt, e.ShopId, e.CustomerId, e.OrderId, "Rejected", ct);

    private static async Task Send(IRealtimePublisher rt, Guid shopId, Guid customerId, Guid orderId, string status, CancellationToken ct)
    {
        await rt.ToShopAsync(shopId, RealtimeEvents.PaymentUpdated, new { orderId, status }, ct);
        await rt.ToUserAsync(customerId, RealtimeEvents.PaymentUpdated, new { orderId, status }, ct);
    }
}

public static class RealtimeShopHandler
{
    public static Task Handle(ShopStatusChanged e, IRealtimePublisher rt, CancellationToken ct) =>
        rt.ToPlantAsync(e.PlantId, RealtimeEvents.ShopStatusChanged, new { e.ShopId, state = e.State.ToString() }, ct);

    public static Task Handle(ItemSoldOut e, IRealtimePublisher rt, CancellationToken ct) =>
        rt.ToPlantAsync(e.PlantId, RealtimeEvents.MenuChanged, new { e.ShopId, e.ItemId, soldOut = true }, ct);

    public static Task Handle(ItemBackInStock e, IRealtimePublisher rt, CancellationToken ct) =>
        rt.ToPlantAsync(e.PlantId, RealtimeEvents.MenuChanged, new { e.ShopId, e.ItemId, soldOut = false }, ct);

    public static Task Handle(ItemChanged e, IRealtimePublisher rt, CancellationToken ct) =>
        rt.ToPlantAsync(e.PlantId, RealtimeEvents.MenuChanged, new { e.ShopId, e.ItemId }, ct);
}
