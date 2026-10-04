using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Catalog.Domain;

public sealed class ItemCategory
{
    private ItemCategory() { }

    public ItemCategory(Guid shopId, string name, int sortOrder)
    {
        Id = Ids.New();
        ShopId = shopId;
        Rename(name);
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public string Name { get; private set; } = "";
    public int SortOrder { get; private set; }

    public void Rename(string name) => Name = Guard.NotEmpty(name, "Category name", 60);
    public void SetOrder(int order) => SortOrder = order;
}

/// <summary>"Sold only 06:00-09:00 on weekdays". Days empty = every day.</summary>
public sealed class AvailabilityWindow
{
    private AvailabilityWindow() { }

    public AvailabilityWindow(List<DayOfWeek> days, TimeOnly from, TimeOnly to)
    {
        if (from == to) throw new DomainException("validation", "Availability window start and end cannot be equal.");
        Days = days.Distinct().ToList();
        From = from;
        To = to;
    }

    public List<DayOfWeek> Days { get; private set; } = [];
    public TimeOnly From { get; private set; }
    public TimeOnly To { get; private set; }

    public bool Contains(DateTime local) => ItemRules.InWindow(Days, From, To, local);
}

public sealed class ModifierOption
{
    public Guid Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public decimal PriceDelta { get; set; }
    public bool IsAvailable { get; set; } = true;
}

/// <summary>Options such as size (choose exactly 1) or add-ons (choose up to 3), like delivery apps.</summary>
public sealed class ModifierGroup
{
    public Guid Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; } = 1;
    public List<ModifierOption> Options { get; set; } = [];
}

public sealed class Item
{
    private Item() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public string TimeZoneId { get; private set; } = "Asia/Bangkok";
    public Guid? CategoryId { get; private set; }
    public ItemKind Kind { get; private set; }
    public StockMode StockMode { get; private set; }
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public List<Guid> ImageIds { get; private set; } = [];

    /// <summary>Owner switched the item on/off (hidden from ordering when off).</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>Manual "sold out" for items without stock counting.</summary>
    public bool IsSoldOut { get; private set; }

    public List<AvailabilityWindow> AvailabilityWindows { get; private set; } = [];
    public DateOnly? SaleFrom { get; private set; }
    public DateOnly? SaleTo { get; private set; }
    public int? MaxPerOrder { get; private set; }
    public int? DurationMinutes { get; private set; }
    public int? SlotCapacity { get; private set; }
    public List<FulfillmentType>? AllowedFulfillment { get; private set; }
    public List<ModifierGroup> ModifierGroups { get; private set; } = [];
    public Guid? PreOrderRoundId { get; private set; }
    public bool IsRecommended { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>Last sold-out state announced as an event, used to publish ItemSoldOut/ItemBackInStock once.</summary>
    public bool AnnouncedSoldOut { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public uint Version { get; private set; }

    public static Item Create(Guid shopId, Guid plantId, string timeZoneId, ItemDetails details, int sortOrder, DateTimeOffset now)
    {
        var item = new Item
        {
            Id = Ids.New(),
            ShopId = shopId,
            PlantId = plantId,
            TimeZoneId = timeZoneId,
            Kind = details.Kind,
            StockMode = details.StockMode,
            SortOrder = sortOrder,
            CreatedAt = now,
        };
        item.Update(details);
        return item;
    }

    public void Update(ItemDetails d)
    {
        if (d.Kind != Kind && Kind == ItemKind.Service && StockMode == StockMode.Slot)
            throw new DomainException("validation", "Change the stock mode before changing a slot service into a product.");
        Kind = d.Kind;
        if (d.StockMode == StockMode.Slot && d.Kind != ItemKind.Service)
            throw new DomainException("validation", "Time-slot booking is only available for services.");
        StockMode = d.StockMode;
        Name = Guard.NotEmpty(d.Name, "Name", 100);
        Description = Guard.Optional(d.Description, "Description", 1000);
        Price = Guard.Money(d.Price, "Price");
        CategoryId = d.CategoryId;
        if (d.ImageIds.Count > 5) throw new DomainException("validation", "At most 5 images per item.");
        ImageIds = [.. d.ImageIds.Distinct()];
        AvailabilityWindows = [.. d.AvailabilityWindows];
        if (d.SaleFrom is { } from && d.SaleTo is { } to && to < from)
            throw new DomainException("validation", "Sale end date must be after the start date.");
        SaleFrom = d.SaleFrom;
        SaleTo = d.SaleTo;
        MaxPerOrder = d.MaxPerOrder is { } max ? Guard.Range(max, "Max per order", 1, 999) : null;
        DurationMinutes = d.DurationMinutes is { } dur ? Guard.Range(dur, "Duration", 5, 24 * 60) : null;
        SlotCapacity = d.StockMode == StockMode.Slot ? Guard.Range(d.SlotCapacity ?? 1, "Slot capacity", 1, 100) : null;
        if (d.StockMode == StockMode.Slot && DurationMinutes is null)
            throw new DomainException("validation", "Slot services need a duration.");
        AllowedFulfillment = d.AllowedFulfillment is { Count: > 0 } f ? [.. f.Distinct()] : null;
        ModifierGroups = ValidateModifiers(d.ModifierGroups);
        PreOrderRoundId = d.PreOrderRoundId;
        IsRecommended = d.IsRecommended;
    }

    public void SetAvailable(bool available) => IsAvailable = available;
    public void SetSoldOut(bool soldOut) => IsSoldOut = soldOut;
    public void SetOrder(int order) => SortOrder = order;
    public void Delete(DateTimeOffset now) => DeletedAt = now;
    public void MarkAnnouncedSoldOut(bool soldOut) => AnnouncedSoldOut = soldOut;

    public bool IsCounted => StockMode is StockMode.Tracked or StockMode.Daily;

    /// <summary>Why the item cannot be ordered for the given moment, or null when it can.</summary>
    public string? UnavailableReason(DateTimeOffset at, int? available) =>
        ItemRules.UnavailableReason(DeletedAt is not null, IsAvailable, TimeZoneId, SaleFrom, SaleTo,
            AvailabilityWindows.Select(w => (w.Days, w.From, w.To)).ToList(), StockMode, IsSoldOut, available, at);

    private static List<ModifierGroup> ValidateModifiers(IReadOnlyList<ModifierGroup> groups)
    {
        if (groups.Count > 10) throw new DomainException("validation", "At most 10 option groups.");
        foreach (var g in groups)
        {
            g.Name = Guard.NotEmpty(g.Name, "Option group name", 60);
            if (g.Options.Count is 0 or > 30) throw new DomainException("validation", $"Option group '{g.Name}' needs 1-30 options.");
            if (g.MinSelect < 0 || g.MaxSelect < 1 || g.MinSelect > g.MaxSelect || g.MaxSelect > g.Options.Count)
                throw new DomainException("validation", $"Option group '{g.Name}' has invalid min/max selection.");
            foreach (var o in g.Options)
            {
                o.Name = Guard.NotEmpty(o.Name, "Option name", 60);
                if (o.PriceDelta is < -100_000 or > 100_000) throw new DomainException("validation", "Option price is out of range.");
                o.PriceDelta = decimal.Round(o.PriceDelta, 2);
            }
        }
        return [.. groups];
    }
}

public sealed record ItemDetails(
    ItemKind Kind, StockMode StockMode, string Name, string? Description, decimal Price, Guid? CategoryId,
    IReadOnlyList<Guid> ImageIds, IReadOnlyList<AvailabilityWindow> AvailabilityWindows,
    DateOnly? SaleFrom, DateOnly? SaleTo, int? MaxPerOrder, int? DurationMinutes, int? SlotCapacity,
    IReadOnlyList<FulfillmentType>? AllowedFulfillment, IReadOnlyList<ModifierGroup> ModifierGroups,
    Guid? PreOrderRoundId, bool IsRecommended);

/// <summary>Availability rules shared by the domain entity and the cached menu view.</summary>
public static class ItemRules
{
    public static bool InWindow(IReadOnlyCollection<DayOfWeek> days, TimeOnly from, TimeOnly to, DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);
        var day = local.DayOfWeek;
        if (from < to) return (days.Count == 0 || days.Contains(day)) && time >= from && time < to;
        // Overnight window (e.g. 22:00-02:00): after midnight belongs to the previous day's window.
        var previous = (DayOfWeek)(((int)day + 6) % 7);
        return (time >= from && (days.Count == 0 || days.Contains(day)))
               || (time < to && (days.Count == 0 || days.Contains(previous)));
    }

    public static string? UnavailableReason(bool deleted, bool isAvailable, string timeZoneId, DateOnly? saleFrom, DateOnly? saleTo,
        IReadOnlyList<(List<DayOfWeek> Days, TimeOnly From, TimeOnly To)> windows, StockMode mode, bool isSoldOut, int? available, DateTimeOffset at)
    {
        if (deleted) return "deleted";
        if (!isAvailable) return "disabled";
        var local = TimeZoneInfo.ConvertTime(at, ShopScheduleEvaluator.ResolveTimeZone(timeZoneId)).DateTime;
        var date = DateOnly.FromDateTime(local);
        if (saleFrom is { } f && date < f) return "not_yet_on_sale";
        if (saleTo is { } t && date > t) return "sale_ended";
        if (windows.Count > 0 && !windows.Any(w => InWindow(w.Days, w.From, w.To, local))) return "outside_hours";
        if (mode == StockMode.Untracked && isSoldOut) return "sold_out";
        if (mode is StockMode.Tracked or StockMode.Daily && available is <= 0) return "sold_out";
        return null;
    }
}
