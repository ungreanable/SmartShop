using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Modules.Analytics.Data;
using SmartShop.Modules.Analytics.Services;

namespace SmartShop.Modules.Analytics.Handlers;

/// <summary>
/// Orders become raw events from the integration events alone (never by reading Ordering's tables), so the rollup can
/// put browsing and buying side by side. The customer is stored as the day's visitor key, like browsing events.
/// </summary>
public static class AnalyticsOrderHandler
{
    public static async Task Handle(OrderPlaced e, AnalyticsDbContext db, VisitorKeys keys, CancellationToken ct)
    {
        if (await AlreadyRecordedAsync(db, e.OrderId, AnalyticsEventType.OrderPlaced, ct)) return;
        var visitor = keys.Visitor(e.CustomerId, VisitorKeys.BangkokDay(e.OccurredAt));

        // Repeat customer: the shop-scoped key remembers the first order, so a redelivered first order is still "new".
        var customerKey = keys.Customer(e.ShopId, e.CustomerId);
        var firstOrder = await db.ShopCustomers.Where(c => c.ShopId == e.ShopId && c.CustomerKey == customerKey)
            .Select(c => (Guid?)c.FirstOrderId).FirstOrDefaultAsync(ct);
        if (firstOrder is null)
            db.ShopCustomers.Add(new ShopCustomer { ShopId = e.ShopId, CustomerKey = customerKey, PlantId = e.PlantId, FirstOrderId = e.OrderId, FirstOrderAt = e.OccurredAt });

        // Total is an estimate for custom ("ตามสั่ง") orders: the shop prices those lines afterwards.
        db.Events.Add(Event(e.PlantId, e.ShopId, e.OrderId, visitor, AnalyticsEventType.OrderPlaced, e.Total, e.OccurredAt, repeat: firstOrder is { } f && f != e.OrderId));
        // Custom dishes have no catalog item (empty id): they only count in the shop totals.
        foreach (var line in e.Lines.Where(l => l.ItemId != Guid.Empty).GroupBy(l => l.ItemId))
        {
            var row = Event(e.PlantId, e.ShopId, e.OrderId, visitor, AnalyticsEventType.OrderLine, null, e.OccurredAt);
            row.ItemId = line.Key;
            row.Quantity = line.Sum(l => l.Quantity);
            db.Events.Add(row);
        }
    }

    public static Task Handle(OrderCompleted e, AnalyticsDbContext db, VisitorKeys keys, CancellationToken ct) =>
        RecordAsync(db, keys, e.PlantId, e.ShopId, e.OrderId, e.CustomerId, AnalyticsEventType.OrderCompleted, e.Total, e.OccurredAt, ct);

    public static Task Handle(OrderCancelled e, AnalyticsDbContext db, VisitorKeys keys, CancellationToken ct) =>
        RecordAsync(db, keys, e.PlantId, e.ShopId, e.OrderId, e.CustomerId, AnalyticsEventType.OrderLost, null, e.OccurredAt, ct);

    public static Task Handle(OrderRejected e, AnalyticsDbContext db, VisitorKeys keys, CancellationToken ct) =>
        RecordAsync(db, keys, e.PlantId, e.ShopId, e.OrderId, e.CustomerId, AnalyticsEventType.OrderLost, null, e.OccurredAt, ct);

    public static Task Handle(OrderExpired e, AnalyticsDbContext db, VisitorKeys keys, CancellationToken ct) =>
        RecordAsync(db, keys, e.PlantId, e.ShopId, e.OrderId, e.CustomerId, AnalyticsEventType.OrderLost, null, e.OccurredAt, ct);

    private static async Task RecordAsync(AnalyticsDbContext db, VisitorKeys keys, Guid plantId, Guid shopId, Guid orderId, Guid customerId,
        AnalyticsEventType type, decimal? amount, DateTimeOffset at, CancellationToken ct)
    {
        if (await AlreadyRecordedAsync(db, orderId, type, ct)) return;
        db.Events.Add(Event(plantId, shopId, orderId, keys.Visitor(customerId, VisitorKeys.BangkokDay(at)), type, amount, at));
    }

    // Delivery is at-least-once; the unique (order_id, type, item_id) index turns a concurrent duplicate into a retry
    // that then stops here.
    private static Task<bool> AlreadyRecordedAsync(AnalyticsDbContext db, Guid orderId, AnalyticsEventType type, CancellationToken ct) =>
        db.Events.AnyAsync(x => x.OrderId == orderId && x.Type == type, ct);

    private static AnalyticsEvent Event(Guid plantId, Guid shopId, Guid orderId, string visitor, AnalyticsEventType type, decimal? amount,
        DateTimeOffset at, bool repeat = false) => new()
        {
            PlantId = plantId,
            ShopId = shopId,
            OrderId = orderId,
            Visitor = visitor,
            Type = type,
            Amount = amount,
            Repeat = repeat,
            OccurredAt = at,
        };
}

/// <summary>
/// PDPA erasure. Aggregates are anonymous totals and stay. Raw events hold only day-scoped visitor keys, but the user's
/// keys of the retention window can be recomputed, so those rows are deleted too; the per-shop repeat-customer keys are
/// recomputed for every shop that has any and deleted. Afterwards nothing in analytics can be linked to the user.
/// </summary>
public static class AnalyticsUserErasedHandler
{
    public static async Task Handle(UserErased e, AnalyticsDbContext db, VisitorKeys keys, TimeProvider clock, CancellationToken ct)
    {
        var today = VisitorKeys.BangkokDay(clock.GetUtcNow());
        var visitorKeys = Enumerable.Range(0, (int)AnalyticsRetentionJob.Retention.TotalDays + 2)
            .Select(d => keys.Visitor(e.UserId, today.AddDays(-d))).ToList();
        await db.Events.Where(x => visitorKeys.Contains(x.Visitor)).ExecuteDeleteAsync(ct);

        var shopIds = await db.ShopCustomers.Select(c => c.ShopId).Distinct().ToListAsync(ct);
        var customerKeys = shopIds.Select(shopId => keys.Customer(shopId, e.UserId)).ToList();
        await db.ShopCustomers.Where(c => customerKeys.Contains(c.CustomerKey)).ExecuteDeleteAsync(ct);
    }
}
