using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Media;
using SmartShop.Modules.Ordering.Domain;

namespace SmartShop.Modules.Ordering.Services;

public sealed record OptionDto(string Group, string Name, decimal PriceDelta);

public sealed record CartLineDto(Guid Id, Guid ItemId, string Name, string? ThumbnailUrl, int Quantity, decimal UnitPrice,
    List<Guid> OptionIds, List<OptionDto> Options, decimal LineTotal, string? Note, DateTimeOffset? SlotStart,
    bool IsOrderable, string? Problem, int? Remaining);

public sealed record CartDto(Guid? ShopId, string? ShopName, List<CartLineDto> Lines, decimal Subtotal, int ItemCount, bool CanCheckout);

public sealed record OrderLineDto(Guid ItemId, string Name, int Quantity, decimal UnitPrice, List<OptionDto> Options, decimal LineTotal,
    string? Note, DateTimeOffset? SlotStart);

public sealed record PersonDto(Guid Id, string Name, string? PictureUrl, string? Phone);

public sealed record TimelineDto(string Type, string? ActorName, string? Note, string? PhotoUrl, DateTimeOffset At);

public sealed record OrderDto(
    Guid Id, string OrderNo, OrderStatus Status, Guid ShopId, string ShopName, PersonDto Customer,
    FulfillmentType FulfillmentType, DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo, DeliveryAddress? DeliveryAddress,
    string? Note, List<OrderLineDto> Lines, decimal Subtotal, decimal Discount, decimal Total, string? PromotionCode,
    Guid PaymentMethodId, PaymentMethodType PaymentMethodType, string PaymentMethodName, PaymentStatus PaymentStatus,
    string? AcceptedByName, string? CancelReason, string? CancelRequestReason, DateTimeOffset? CancelRequestedAt,
    string? DeliveryPhotoUrl, DateTimeOffset PlacedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? AutoCompleteAt,
    DateTimeOffset? CompletedAt, List<TimelineDto> Timeline, Guid? PreOrderRoundId, string ViewerRole);

public sealed record OrderSummaryDto(
    Guid Id, string OrderNo, OrderStatus Status, Guid ShopId, string ShopName, string CustomerName, string? CustomerHouseNo,
    FulfillmentType FulfillmentType, DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo, int ItemCount, string ItemsPreview,
    decimal Total, PaymentMethodType PaymentMethodType, PaymentStatus PaymentStatus, DateTimeOffset PlacedAt, DateTimeOffset? ExpiresAt,
    bool CancelRequested);

internal static class OrderMapper
{
    public static OrderLineDto Line(OrderLine l) => new(l.ItemId, l.Name, l.Quantity, l.UnitPrice,
        l.Options.Select(o => new OptionDto(o.Group, o.Name, o.PriceDelta)).ToList(), l.LineTotal, l.Note, l.SlotStart);

    public static OrderDto ToDto(Order o, IReadOnlyDictionary<Guid, UserProfile> people, IReadOnlyDictionary<Guid, string?> labels,
        IMediaUrls media, string viewerRole)
    {
        string? Name(Guid? id) => id is { } v
            ? labels.GetValueOrDefault(v) is { } label ? $"{people.GetValueOrDefault(v)?.DisplayName} ({label})" : people.GetValueOrDefault(v)?.DisplayName
            : null;

        var customer = people.GetValueOrDefault(o.CustomerId);
        // Customer phone is only revealed to the shop handling the order (PDPA: data minimisation).
        var phone = viewerRole is "shop" or "admin" ? customer?.Phone : null;
        return new OrderDto(
            o.Id, o.OrderNo, o.Status, o.ShopId, o.ShopName,
            new PersonDto(o.CustomerId, customer?.DisplayName ?? "?", customer?.PictureUrl, phone),
            o.FulfillmentType, o.ScheduledFrom, o.ScheduledTo, o.DeliveryAddress, o.Note,
            o.Lines.Select(Line).ToList(), o.Subtotal, o.Discount, o.Total, o.PromotionCode,
            o.PaymentMethodId, o.PaymentMethodType, o.PaymentMethodName, o.PaymentStatus,
            Name(o.AcceptedBy), o.CancelReason, o.CancelRequestReason, o.CancelRequestedAt,
            media.For(o.DeliveryPhotoId), o.PlacedAt, o.ExpiresAt, o.AutoCompleteAt, o.CompletedAt,
            o.Timeline.OrderBy(t => t.At).Select(t => new TimelineDto(t.Type, Name(t.ActorId), t.Note, media.For(t.PhotoId), t.At)).ToList(),
            o.PreOrderRoundId, viewerRole);
    }

    public static OrderSummaryDto Summary(Order o, IReadOnlyDictionary<Guid, UserProfile> people) => new(
        o.Id, o.OrderNo, o.Status, o.ShopId, o.ShopName, people.GetValueOrDefault(o.CustomerId)?.DisplayName ?? "?",
        o.DeliveryAddress?.HouseNo, o.FulfillmentType, o.ScheduledFrom, o.ScheduledTo,
        o.Lines.Sum(l => l.Quantity), string.Join(", ", o.Lines.Select(l => $"{l.Name} x{l.Quantity}")),
        o.Total, o.PaymentMethodType, o.PaymentStatus, o.PlacedAt, o.ExpiresAt, o.CancelRequestedAt is not null);
}
