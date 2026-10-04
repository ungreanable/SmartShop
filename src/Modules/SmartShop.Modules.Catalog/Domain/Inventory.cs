using SmartShop.SharedKernel;

namespace SmartShop.Modules.Catalog.Domain;

/// <summary>
/// Stock counter of a counted item. Available = OnHand - Reserved.
/// Reserve on order placement, Commit (really deduct) on completion, Release on cancel/reject/expire.
/// Reservation itself is done with an atomic SQL update (see <c>InventorySql</c>) to prevent overselling.
/// </summary>
public sealed class Inventory
{
    private Inventory() { }

    public Inventory(Guid itemId, Guid shopId, int onHand)
    {
        ItemId = itemId;
        ShopId = shopId;
        OnHand = Math.Max(0, onHand);
    }

    public Guid ItemId { get; private set; }
    public Guid ShopId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public int? DailyQuota { get; private set; }
    public TimeOnly? DailyResetTime { get; private set; }
    public DateOnly? LastResetDate { get; private set; }
    public int? LowStockThreshold { get; private set; }
    public bool LowStockNotified { get; private set; }

    public int Available => OnHand - Reserved;

    public StockMovement Set(int onHand, Guid actorId, string? note, DateTimeOffset now)
    {
        Guard.Range(onHand, "Stock", 0, 1_000_000);
        if (onHand < Reserved)
            throw new DomainException("stock_below_reserved", $"{Reserved} are reserved by pending orders; stock cannot be lower than that.");
        var delta = onHand - OnHand;
        OnHand = onHand;
        ResetLowStockFlag();
        return Movement(StockMovementType.Adjust, delta, null, actorId, note, now);
    }

    public StockMovement Add(int quantity, Guid actorId, string? note, DateTimeOffset now)
    {
        Guard.Range(quantity, "Quantity", 1, 1_000_000);
        OnHand += quantity;
        ResetLowStockFlag();
        return Movement(StockMovementType.Restock, quantity, null, actorId, note, now);
    }

    public void ConfigureDaily(int? quota, TimeOnly? resetTime)
    {
        DailyQuota = quota is { } q ? Guard.Range(q, "Daily quota", 0, 100_000) : null;
        DailyResetTime = resetTime;
    }

    public void ConfigureLowStock(int? threshold)
    {
        LowStockThreshold = threshold is { } t ? Guard.Range(t, "Low-stock threshold", 0, 100_000) : null;
        ResetLowStockFlag();
    }

    public StockMovement? DailyReset(DateOnly today, DateTimeOffset now)
    {
        if (DailyQuota is not { } quota || LastResetDate == today) return null;
        var target = Reserved + quota;
        var delta = target - OnHand;
        OnHand = target;
        LastResetDate = today;
        LowStockNotified = false;
        return Movement(StockMovementType.DailyReset, delta, null, null, $"Daily quota {quota}", now);
    }

    /// <summary>True exactly once when stock drops to the threshold, until restocked.</summary>
    public bool ShouldWarnLowStock()
    {
        if (LowStockThreshold is not { } t || LowStockNotified || Available > t) return false;
        LowStockNotified = true;
        return true;
    }

    private void ResetLowStockFlag()
    {
        if (LowStockThreshold is null || Available > LowStockThreshold) LowStockNotified = false;
    }

    private StockMovement Movement(StockMovementType type, int quantity, Guid? orderId, Guid? actorId, string? note, DateTimeOffset now) =>
        new(ItemId, ShopId, type, quantity, OnHand, Reserved, orderId, actorId, note, now);
}

public enum StockMovementType { Restock, Adjust, Reserve, Commit, Release, DailyReset }

/// <summary>Append-only ledger of every stock change. (order, item, type) is unique so event redelivery is harmless.</summary>
public sealed class StockMovement
{
    private StockMovement() { }

    public StockMovement(Guid itemId, Guid shopId, StockMovementType type, int quantity, int onHandAfter, int reservedAfter,
        Guid? orderId, Guid? actorId, string? note, DateTimeOffset at)
    {
        Id = Ids.New();
        ItemId = itemId;
        ShopId = shopId;
        Type = type;
        Quantity = quantity;
        OnHandAfter = onHandAfter;
        ReservedAfter = reservedAfter;
        OrderId = orderId;
        ActorId = actorId;
        Note = note;
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid ShopId { get; private set; }
    public StockMovementType Type { get; private set; }
    public int Quantity { get; private set; }
    public int OnHandAfter { get; private set; }
    public int ReservedAfter { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? ActorId { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset At { get; private set; }
}
