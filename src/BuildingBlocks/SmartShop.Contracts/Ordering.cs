using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;

namespace SmartShop.Contracts.Ordering;

public enum OrderStatus
{
    PendingAcceptance, Accepted, Preparing, Ready, OutForDelivery, Delivered, Completed,
    Rejected, Cancelled, Expired,
}

public sealed record OrderLineInfo(Guid ItemId, string Name, int Quantity, decimal UnitPrice, DateTimeOffset? SlotStart);

public sealed record OrderSummaryInfo(
    Guid OrderId, Guid PlantId, Guid ShopId, Guid CustomerId, string OrderNo, OrderStatus Status,
    decimal Total, Guid PaymentMethodId, FulfillmentType FulfillmentType);

public interface IOrderDirectory
{
    Task<OrderSummaryInfo?> GetAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Orders of one shop for integrations (public API), newest first.</summary>
    Task<IReadOnlyList<OrderExport>> ListForShopAsync(Guid shopId, DateTimeOffset? placedSince, OrderStatus? status, int limit, CancellationToken ct = default);

    Task<OrderExport?> GetForShopAsync(Guid shopId, Guid orderId, CancellationToken ct = default);
}

public sealed record OrderExportLine(Guid ItemId, string Name, int Quantity, decimal UnitPrice, decimal LineTotal, IReadOnlyList<string> Options, string? Note, DateTimeOffset? SlotStart);

public sealed record OrderExport(
    Guid OrderId, string OrderNo, OrderStatus Status, Guid CustomerId, FulfillmentType FulfillmentType,
    string? HouseNo, string? Soi, string? AddressNote, string? Note,
    DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo,
    decimal Subtotal, decimal Discount, decimal Total, PaymentMethodType PaymentMethodType, PaymentStatus PaymentStatus,
    DateTimeOffset PlacedAt, DateTimeOffset? AcceptedAt, DateTimeOffset? CompletedAt, IReadOnlyList<OrderExportLine> Lines);

public sealed record OrderPlaced(
    Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId,
    decimal Subtotal, decimal Discount, decimal Total, FulfillmentType FulfillmentType,
    Guid PaymentMethodId, PaymentMethodType PaymentMethodType,
    DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo,
    Guid? PromotionId, IReadOnlyList<OrderLineInfo> Lines) : IntegrationEvent(PlantId);

public sealed record OrderAccepted(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record OrderPreparing(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId) : IntegrationEvent(PlantId);
public sealed record OrderReady(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, FulfillmentType FulfillmentType) : IntegrationEvent(PlantId);
public sealed record OrderOutForDelivery(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId) : IntegrationEvent(PlantId);
public sealed record OrderDelivered(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, Guid? PhotoId, int AutoCompleteHours) : IntegrationEvent(PlantId);
public sealed record OrderCompleted(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, bool Automatic, decimal Total, IReadOnlyList<OrderLineInfo> Lines) : IntegrationEvent(PlantId);

/// <summary>Terminal "did not happen" states share one shape: stock and promotions must be released.</summary>
public sealed record OrderRejected(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, Guid ActorId, string Reason, Guid? PromotionId, IReadOnlyList<OrderLineInfo> Lines) : IntegrationEvent(PlantId);
public sealed record OrderCancelled(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, Guid? ActorId, bool ByCustomer, string? Reason, Guid? PromotionId, IReadOnlyList<OrderLineInfo> Lines) : IntegrationEvent(PlantId);
public sealed record OrderExpired(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid CustomerId, Guid? PromotionId, IReadOnlyList<OrderLineInfo> Lines) : IntegrationEvent(PlantId);

public sealed record OrderCancellationRequested(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, Guid CustomerId, string? Reason) : IntegrationEvent(PlantId);
public sealed record OrderPendingReminder(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, int Attempt) : IntegrationEvent(PlantId);
public sealed record OrderMessagePosted(Guid PlantId, Guid OrderId, string OrderNo, Guid ShopId, Guid CustomerId, Guid SenderId, bool FromShop, string Preview) : IntegrationEvent(PlantId);
public sealed record OrderReported(Guid PlantId, Guid OrderId, string OrderNo, Guid ReportId, Guid ReporterId, string Reason) : IntegrationEvent(PlantId);
public sealed record OrderReportResolved(Guid PlantId, Guid OrderId, Guid ReportId, Guid ReporterId, string Resolution) : IntegrationEvent(PlantId);

public sealed record ExpireOrderIfNotAccepted(Guid OrderId) : IScheduledCommand;
public sealed record RemindShopPendingOrder(Guid OrderId, int Attempt) : IScheduledCommand;
public sealed record AutoCompleteOrder(Guid OrderId) : IScheduledCommand;
