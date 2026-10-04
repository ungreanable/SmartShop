using SmartShop.SharedKernel;

namespace SmartShop.Modules.Ordering.Domain;

/// <summary>Server-side cart: one per user and plant, holding items of a single shop (like delivery apps).</summary>
public sealed class Cart
{
    private Cart() { }

    public Cart(Guid userId, Guid plantId, Guid shopId, DateTimeOffset now)
    {
        Id = Ids.New();
        UserId = userId;
        PlantId = plantId;
        ShopId = shopId;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid ShopId { get; private set; }
    public List<CartLine> Lines { get; private set; } = [];
    public DateTimeOffset UpdatedAt { get; private set; }

    public CartLine Add(Guid itemId, int quantity, List<Guid> optionIds, string? note, DateTimeOffset? slotStart, DateTimeOffset now)
    {
        Guard.Range(quantity, "Quantity", 1, 999);
        if (Lines.Count >= 50) throw new DomainException("cart_full", "The cart can hold at most 50 lines.");
        note = Guard.Optional(note, "Note", 200);
        slotStart = slotStart?.ToUniversalTime();
        var same = Lines.FirstOrDefault(l => l.ItemId == itemId && l.Note == note && l.SlotStart == slotStart
                                             && l.OptionIds.Order().SequenceEqual(optionIds.Order()));
        if (same is not null)
        {
            same.Quantity = Math.Min(999, same.Quantity + quantity);
            UpdatedAt = now;
            return same;
        }
        var line = new CartLine { Id = Ids.New(), ItemId = itemId, Quantity = quantity, OptionIds = [.. optionIds.Distinct()], Note = note, SlotStart = slotStart };
        Lines.Add(line);
        UpdatedAt = now;
        return line;
    }

    public void Update(Guid lineId, int quantity, List<Guid>? optionIds, string? note, DateTimeOffset now)
    {
        var line = Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("Cart line", lineId);
        line.Quantity = Guard.Range(quantity, "Quantity", 1, 999);
        if (optionIds is not null) line.OptionIds = [.. optionIds.Distinct()];
        line.Note = Guard.Optional(note, "Note", 200);
        UpdatedAt = now;
    }

    public void Remove(Guid lineId, DateTimeOffset now)
    {
        Lines.RemoveAll(l => l.Id == lineId);
        UpdatedAt = now;
    }

    public void Reset(Guid shopId, DateTimeOffset now)
    {
        ShopId = shopId;
        Lines.Clear();
        UpdatedAt = now;
    }
}

public sealed class CartLine
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public int Quantity { get; set; }
    public List<Guid> OptionIds { get; set; } = [];
    public string? Note { get; set; }
    public DateTimeOffset? SlotStart { get; set; }
}
