using SmartShop.SharedKernel;

namespace SmartShop.Modules.Catalog.Domain;

public enum SlotBookingStatus { Reserved, Committed, Released }

/// <summary>A booked time slot of a service (e.g. a 60-minute massage). Capacity is per item and slot start.</summary>
public sealed class SlotBooking
{
    private SlotBooking() { }

    public SlotBooking(Guid itemId, Guid shopId, Guid orderId, DateTimeOffset slotStart, DateTimeOffset slotEnd, int quantity, DateTimeOffset now)
    {
        Id = Ids.New();
        ItemId = itemId;
        ShopId = shopId;
        OrderId = orderId;
        SlotStart = slotStart;
        SlotEnd = slotEnd;
        Quantity = quantity;
        Status = SlotBookingStatus.Reserved;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid OrderId { get; private set; }
    public DateTimeOffset SlotStart { get; private set; }
    public DateTimeOffset SlotEnd { get; private set; }
    public int Quantity { get; private set; }
    public SlotBookingStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Commit() { if (Status == SlotBookingStatus.Reserved) Status = SlotBookingStatus.Committed; }
    public void Release() { if (Status == SlotBookingStatus.Reserved) Status = SlotBookingStatus.Released; }
}

public enum RoundStatus { Open, Confirmed, Cancelled }

/// <summary>
/// Pre-order round: "order by Friday 20:00, pick up Saturday 08:00-10:00". With a minimum total quantity it
/// becomes a group buy: if the target is not reached by the cut-off, the round and its orders are cancelled.
/// </summary>
public sealed class PreOrderRound
{
    private PreOrderRound() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public string Title { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset OrderCutoff { get; private set; }
    public DateTimeOffset FulfillFrom { get; private set; }
    public DateTimeOffset FulfillTo { get; private set; }
    public int? MinTotalQuantity { get; private set; }
    public int OrderedQuantity { get; private set; }
    public RoundStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public uint Version { get; private set; }

    public static PreOrderRound Create(Guid shopId, Guid plantId, string title, string? description, DateTimeOffset cutoff,
        DateTimeOffset from, DateTimeOffset to, int? minTotal, DateTimeOffset now)
    {
        var round = new PreOrderRound { Id = Ids.New(), ShopId = shopId, PlantId = plantId, Status = RoundStatus.Open, CreatedAt = now };
        round.Update(title, description, cutoff, from, to, minTotal, now);
        return round;
    }

    public void Update(string title, string? description, DateTimeOffset cutoff, DateTimeOffset from, DateTimeOffset to, int? minTotal, DateTimeOffset now)
    {
        if (Status != RoundStatus.Open) throw new ConflictException("round_closed", "This round is already closed.");
        Title = Guard.NotEmpty(title, "Title", 100);
        Description = Guard.Optional(description, "Description", 1000);
        if (cutoff <= now) throw new DomainException("validation", "The order cut-off must be in the future.");
        if (from < cutoff) throw new DomainException("validation", "Pick-up must start after the order cut-off.");
        if (to <= from) throw new DomainException("validation", "Pick-up end must be after its start.");
        OrderCutoff = cutoff;
        FulfillFrom = from;
        FulfillTo = to;
        MinTotalQuantity = minTotal is { } m ? Guard.Range(m, "Minimum quantity", 1, 100_000) : null;
    }

    public bool IsOpenAt(DateTimeOffset now) => Status == RoundStatus.Open && now < OrderCutoff;

    public void AddOrdered(int quantity) => OrderedQuantity = Math.Max(0, OrderedQuantity + quantity);

    public bool Close(DateTimeOffset now)
    {
        var reached = MinTotalQuantity is null || OrderedQuantity >= MinTotalQuantity;
        Status = reached ? RoundStatus.Confirmed : RoundStatus.Cancelled;
        ClosedAt = now;
        return reached;
    }
}
