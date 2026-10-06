using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Ordering.Domain;

public sealed class DeliveryAddress
{
    public string? HouseNo { get; set; }
    public string? Soi { get; set; }
    public string? Note { get; set; }
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public List<OrderLineOption> Options { get; set; } = [];
    public string? Note { get; set; }
    public DateTimeOffset? SlotStart { get; set; }
    public decimal LineTotal => (UnitPrice + Options.Sum(o => o.PriceDelta)) * Quantity;
}

public sealed class OrderLineOption
{
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal PriceDelta { get; set; }
}

public sealed class OrderEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public Guid? ActorId { get; set; }
    public string? Note { get; set; }
    public Guid? PhotoId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// An order to one shop. State machine:
/// PendingAcceptance -> Accepted -> Preparing -> Ready -> (OutForDelivery) -> Delivered -> Completed,
/// with Rejected / Cancelled / Expired as alternative terminal states. Shops may skip intermediate steps.
/// Lines are snapshots: later price or name changes never affect existing orders.
/// </summary>
public sealed class Order
{
    private Order() { }

    public Guid Id { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid ShopId { get; private set; }
    public string ShopName { get; private set; } = "";
    public Guid CustomerId { get; private set; }
    public string OrderNo { get; private set; } = "";
    public OrderStatus Status { get; private set; }
    public FulfillmentType FulfillmentType { get; private set; }
    public DateTimeOffset? ScheduledFrom { get; private set; }
    public DateTimeOffset? ScheduledTo { get; private set; }
    public DeliveryAddress? DeliveryAddress { get; private set; }
    public string? Note { get; private set; }
    public List<OrderLine> Lines { get; private set; } = [];
    public decimal Subtotal { get; private set; }
    public decimal Discount { get; private set; }
    public decimal Total { get; private set; }
    public Guid? PromotionId { get; private set; }
    public string? PromotionCode { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public PaymentMethodType PaymentMethodType { get; private set; }
    public string PaymentMethodName { get; private set; } = "";
    public PaymentStatus PaymentStatus { get; private set; }
    public Guid? PreOrderRoundId { get; private set; }
    public Guid? AcceptedBy { get; private set; }
    public string? CancelReason { get; private set; }
    public string? CancelRequestReason { get; private set; }
    public DateTimeOffset? CancelRequestedAt { get; private set; }
    public Guid? DeliveryPhotoId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? AutoCompleteAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public List<OrderEvent> Timeline { get; private set; } = [];
    public uint Version { get; private set; }

    public static Order Place(PlaceOrder p, DateTimeOffset now)
    {
        var order = new Order
        {
            Id = p.OrderId,
            PlantId = p.PlantId,
            ShopId = p.ShopId,
            ShopName = p.ShopName,
            CustomerId = p.CustomerId,
            OrderNo = p.OrderNo,
            Status = OrderStatus.PendingAcceptance,
            FulfillmentType = p.FulfillmentType,
            ScheduledFrom = p.ScheduledFrom,
            ScheduledTo = p.ScheduledTo,
            DeliveryAddress = p.FulfillmentType == FulfillmentType.Delivery ? p.Address : null,
            Note = Guard.Optional(p.Note, "Note", 500),
            Lines = [.. p.Lines],
            Discount = p.Discount,
            PromotionId = p.PromotionId,
            PromotionCode = p.PromotionCode,
            PaymentMethodId = p.PaymentMethod.Id,
            PaymentMethodType = p.PaymentMethod.Type,
            PaymentMethodName = p.PaymentMethod.DisplayName,
            PaymentStatus = PaymentStatus.Unpaid,
            PreOrderRoundId = p.PreOrderRoundId,
            IdempotencyKey = p.IdempotencyKey,
            PlacedAt = now,
            ExpiresAt = now.AddMinutes(p.AcceptTimeoutMinutes),
        };
        order.Subtotal = order.Lines.Sum(l => l.LineTotal);
        order.Total = Math.Max(0, order.Subtotal - order.Discount);
        order.Log("Placed", p.CustomerId, null, null, now);
        return order;
    }

    public bool IsTerminal => Status is OrderStatus.Completed or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Expired;

    public void Accept(Guid? actorId, DateTimeOffset now)
    {
        Require("accept", OrderStatus.PendingAcceptance);
        Status = OrderStatus.Accepted;
        AcceptedBy = actorId;
        AcceptedAt = now;
        ExpiresAt = null;
        Log(actorId is null ? "AutoAccepted" : "Accepted", actorId, null, null, now);
    }

    public void Reject(Guid actorId, string reason, DateTimeOffset now)
    {
        Require("reject", OrderStatus.PendingAcceptance);
        Close(OrderStatus.Rejected, Guard.NotEmpty(reason, "Reason", 300), now);
        Log("Rejected", actorId, CancelReason, null, now);
    }

    /// <summary>Customers may cancel freely only before the shop accepted.</summary>
    public void CancelByCustomer(Guid customerId, string? reason, DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingAcceptance)
            throw new ConflictException("cannot_cancel", "The shop already accepted this order. Send a cancellation request instead.");
        Close(OrderStatus.Cancelled, Guard.Optional(reason, "Reason", 300), now);
        Log("CancelledByCustomer", customerId, CancelReason, null, now);
    }

    public void RequestCancel(Guid customerId, string? reason, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Accepted or OrderStatus.Preparing or OrderStatus.Ready))
            throw new ConflictException("cannot_request_cancel", "This order can no longer be cancelled.");
        if (CancelRequestedAt is not null) throw new ConflictException("cancel_requested", "A cancellation request is already pending.");
        CancelRequestReason = Guard.Optional(reason, "Reason", 300);
        CancelRequestedAt = now;
        Log("CancelRequested", customerId, CancelRequestReason, null, now);
    }

    public void DeclineCancelRequest(Guid actorId, string? note, DateTimeOffset now)
    {
        if (CancelRequestedAt is null) throw new ConflictException("no_cancel_request", "There is no cancellation request.");
        CancelRequestedAt = null;
        CancelRequestReason = null;
        Log("CancelRequestDeclined", actorId, Guard.Optional(note, "Note", 300), null, now);
    }

    /// <summary>Shop cancels (or approves the customer's request) any time before delivery.</summary>
    public void CancelByShop(Guid actorId, string reason, DateTimeOffset now)
    {
        if (IsTerminal || Status is OrderStatus.Delivered or OrderStatus.PendingAcceptance)
            throw new ConflictException("cannot_cancel", Status == OrderStatus.PendingAcceptance ? "Reject the order instead." : "This order can no longer be cancelled.");
        Close(OrderStatus.Cancelled, Guard.NotEmpty(reason, "Reason", 300), now);
        Log("CancelledByShop", actorId, CancelReason, null, now);
    }

    /// <summary>System cancellation, e.g. a group-buy round did not reach its target.</summary>
    public void CancelBySystem(string reason, DateTimeOffset now)
    {
        if (IsTerminal || Status == OrderStatus.Delivered) return;
        Close(OrderStatus.Cancelled, reason, now);
        Log("CancelledBySystem", null, reason, null, now);
    }

    public void StartPreparing(Guid actorId, DateTimeOffset now)
    {
        Require("start preparing", OrderStatus.Accepted);
        Status = OrderStatus.Preparing;
        Log("Preparing", actorId, null, null, now);
    }

    public void MarkReady(Guid actorId, DateTimeOffset now)
    {
        Require("mark ready", OrderStatus.Accepted, OrderStatus.Preparing);
        Status = OrderStatus.Ready;
        Log("Ready", actorId, null, null, now);
    }

    public void Dispatch(Guid actorId, DateTimeOffset now)
    {
        if (FulfillmentType != FulfillmentType.Delivery)
            throw new DomainException("not_delivery", "Only delivery orders go out for delivery.");
        Require("dispatch", OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.Ready);
        Status = OrderStatus.OutForDelivery;
        Log("OutForDelivery", actorId, null, null, now);
    }

    /// <summary>The shop confirms hand-over, optionally with a photo (e.g. bag at the customer's door).</summary>
    public void MarkDelivered(Guid actorId, Guid? photoId, int autoCompleteHours, DateTimeOffset now)
    {
        Require("mark delivered", OrderStatus.Accepted, OrderStatus.Preparing, OrderStatus.Ready, OrderStatus.OutForDelivery);
        Status = OrderStatus.Delivered;
        DeliveryPhotoId = photoId;
        DeliveredAt = now;
        AutoCompleteAt = now.AddHours(autoCompleteHours);
        CancelRequestedAt = null;
        Log("Delivered", actorId, null, photoId, now);
    }

    /// <summary>The customer confirms receipt. This is when stock is finally deducted.</summary>
    public void ConfirmReceived(Guid customerId, DateTimeOffset now)
    {
        Require("confirm", OrderStatus.Delivered);
        Complete(now);
        Log("Received", customerId, null, null, now);
    }

    /// <summary>
    /// Owner/manager escape hatch for an order stuck in the flow (e.g. the customer never confirms): the goods were
    /// handed over, so it counts as a sale. Recorded in the timeline with the reason.
    /// </summary>
    public void ForceComplete(Guid actorId, string reason, DateTimeOffset now)
    {
        if (IsTerminal || Status == OrderStatus.PendingAcceptance)
            throw new ConflictException("cannot_force_complete", Status == OrderStatus.PendingAcceptance
                ? "Accept or reject the order instead." : "This order is already closed.");
        var note = Guard.NotEmpty(reason, "Reason", 300);
        Complete(now);
        Log("ForceCompleted", actorId, note, null, now);
    }

    /// <summary>Owner/manager escape hatch: closes a broken order as cancelled from any open state except delivered.</summary>
    public void ForceCancel(Guid actorId, string reason, DateTimeOffset now)
    {
        if (IsTerminal || Status == OrderStatus.Delivered)
            throw new ConflictException("cannot_force_cancel", Status == OrderStatus.Delivered
                ? "The order was delivered; close it as completed instead." : "This order is already closed.");
        Close(OrderStatus.Cancelled, Guard.NotEmpty(reason, "Reason", 300), now);
        Log("ForceCancelled", actorId, CancelReason, null, now);
    }

    public bool AutoComplete(DateTimeOffset now)
    {
        if (Status != OrderStatus.Delivered) return false;
        Complete(now);
        Log("AutoCompleted", null, null, null, now);
        return true;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingAcceptance) return false;
        Close(OrderStatus.Expired, "The shop did not respond in time.", now);
        Log("Expired", null, null, null, now);
        return true;
    }

    public void SetPaymentStatus(PaymentStatus status, DateTimeOffset now, Guid? actorId = null, string? note = null)
    {
        if (PaymentStatus == status) return;
        PaymentStatus = status;
        Log($"Payment{status}", actorId, note, null, now);
    }

    public void Log(string type, Guid? actorId, string? note, Guid? photoId, DateTimeOffset at) =>
        Timeline.Add(new OrderEvent { Id = Ids.New(), Type = type, ActorId = actorId, Note = note, PhotoId = photoId, At = at });

    public void Anonymise()
    {
        DeliveryAddress = null;
        Note = null;
    }

    private void Complete(DateTimeOffset now)
    {
        Status = OrderStatus.Completed;
        CompletedAt = now;
        ClosedAt = now;
        AutoCompleteAt = null;
    }

    private void Close(OrderStatus status, string? reason, DateTimeOffset now)
    {
        Status = status;
        CancelReason = reason;
        ClosedAt = now;
        ExpiresAt = null;
        CancelRequestedAt = null;
    }

    private void Require(string action, params OrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
            throw new ConflictException("invalid_order_state", $"Cannot {action} an order that is {Status}.");
    }

    public OrderLineInfo[] LineInfos() =>
        Lines.Select(l => new OrderLineInfo(l.ItemId, l.Name, l.Quantity, l.UnitPrice, l.SlotStart)).ToArray();
}

public sealed record PlaceOrder(
    Guid OrderId, Guid PlantId, Guid ShopId, string ShopName, Guid CustomerId, string OrderNo,
    FulfillmentType FulfillmentType, DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo, DeliveryAddress? Address,
    string? Note, IReadOnlyList<OrderLine> Lines, decimal Discount, Guid? PromotionId, string? PromotionCode,
    PaymentMethodInfo PaymentMethod, Guid? PreOrderRoundId, string IdempotencyKey, int AcceptTimeoutMinutes);
