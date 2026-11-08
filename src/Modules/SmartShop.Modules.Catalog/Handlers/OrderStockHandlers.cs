using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Ordering;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Services;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Catalog.Handlers;

/// <summary>Stock was already reserved synchronously at checkout; here we update rounds and announce sold-out.</summary>
[RequiresEagerTransaction] // the round ledger insert and the round total must commit together
public static class CatalogOrderPlacedHandler
{
    public static async Task Handle(OrderPlaced e, CatalogDbContext db, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct)
    {
        var lines = Catalogued.Of(e.Lines);
        await RoundCounter.AddAsync(db, e.OrderId, lines, ct);
        await Announce.PublishAsync(bus, await announcer.CheckAsync(lines.Select(l => l.ItemId), ct));
    }
}

[RequiresEagerTransaction]
public static class CatalogOrderCompletedHandler
{
    public static async Task Handle(OrderCompleted e, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct)
    {
        var lines = Catalogued.Of(e.Lines);
        await ledger.CommitAsync(e.OrderId, lines.Select(l => l.ItemId), ct);
        await Announce.PublishAsync(bus, await announcer.CheckAsync(lines.Select(l => l.ItemId), ct));
    }
}

[RequiresEagerTransaction]
public static class CatalogOrderCancelledHandler
{
    public static Task Handle(OrderCancelled e, CatalogDbContext db, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct) =>
        Release.RunAsync(db, ledger, announcer, bus, e.OrderId, e.Lines, ct);
}

[RequiresEagerTransaction]
public static class CatalogOrderRejectedHandler
{
    public static Task Handle(OrderRejected e, CatalogDbContext db, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct) =>
        Release.RunAsync(db, ledger, announcer, bus, e.OrderId, e.Lines, ct);
}

[RequiresEagerTransaction]
public static class CatalogOrderExpiredHandler
{
    public static Task Handle(OrderExpired e, CatalogDbContext db, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct) =>
        Release.RunAsync(db, ledger, announcer, bus, e.OrderId, e.Lines, ct);
}

/// <summary>Custom dishes ("ตามสั่ง") have no catalog item (empty id): nothing to count or release for them.</summary>
internal static class Catalogued
{
    public static IReadOnlyList<OrderLineInfo> Of(IReadOnlyList<OrderLineInfo> lines) => lines.Where(l => l.ItemId != Guid.Empty).ToList();
}

internal static class Release
{
    public static async Task RunAsync(CatalogDbContext db, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, Guid orderId,
        IReadOnlyList<OrderLineInfo> lines, CancellationToken ct)
    {
        lines = Catalogued.Of(lines);
        await ledger.ReleaseAsync(orderId, lines.Select(l => l.ItemId), ct);
        await RoundCounter.ReleaseAsync(db, orderId, lines, ct);
        await Announce.PublishAsync(bus, await announcer.CheckAsync(lines.Select(l => l.ItemId), ct));
    }
}

internal static class Announce
{
    public static async Task PublishAsync(IMessageBus bus, IEnumerable<Contracts.IntegrationEvent> events)
    {
        foreach (var e in events) await bus.PublishAsync(e);
    }
}

/// <summary>
/// Keeps the ordered total of open pre-order rounds (group-buy targets) up to date, through <see cref="Domain.RoundOrder"/>
/// so that at-least-once delivery can neither count an order twice nor take it back twice.
/// </summary>
internal static class RoundCounter
{
    public static async Task AddAsync(CatalogDbContext db, Guid orderId, IReadOnlyList<OrderLineInfo> lines, CancellationToken ct)
    {
        foreach (var (round, quantity) in await OpenRoundsAsync(db, lines, ct))
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.round_orders (order_id, round_id, quantity, released) VALUES ({orderId}, {round.Id}, {quantity}, false)
                ON CONFLICT (order_id, round_id) DO UPDATE SET quantity = excluded.quantity WHERE round_orders.quantity < 0
                """, ct);
            // New row: count it. Updated row: the cancellation got here first and already took it back, so this evens it out.
            if (inserted == 1) round.AddOrdered(quantity);
        }
    }

    public static async Task ReleaseAsync(CatalogDbContext db, Guid orderId, IReadOnlyList<OrderLineInfo> lines, CancellationToken ct)
    {
        var recorded = await db.RoundOrders.Where(r => r.OrderId == orderId).ToListAsync(ct);
        if (recorded.Count == 0)
        {
            // Placed before the ledger existed, or its "placed" event is still on the way: take back what the lines say and
            // record it released with a negative quantity, so a redelivery does nothing and a late "placed" evens it out.
            foreach (var (round, quantity) in await OpenRoundsAsync(db, lines, ct))
            {
                round.AddOrdered(-quantity);
                db.RoundOrders.Add(new Domain.RoundOrder { OrderId = orderId, RoundId = round.Id, Quantity = -quantity, Released = true });
            }
            return;
        }

        var pending = recorded.Where(r => !r.Released).ToList();
        if (pending.Count == 0) return; // already taken back
        var roundIds = pending.Select(r => r.RoundId).ToList();
        var rounds = await db.PreOrderRounds.Where(r => roundIds.Contains(r.Id) && r.Status == Domain.RoundStatus.Open).ToDictionaryAsync(r => r.Id, ct);
        foreach (var entry in pending)
        {
            entry.Released = true;
            rounds.GetValueOrDefault(entry.RoundId)?.AddOrdered(-entry.Quantity);
        }
    }

    private static async Task<List<(Domain.PreOrderRound Round, int Quantity)>> OpenRoundsAsync(CatalogDbContext db, IReadOnlyList<OrderLineInfo> lines,
        CancellationToken ct)
    {
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        if (itemIds.Count == 0) return [];
        var itemRounds = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id) && i.PreOrderRoundId != null)
            .ToDictionaryAsync(i => i.Id, i => i.PreOrderRoundId!.Value, ct);
        if (itemRounds.Count == 0) return [];

        var roundIds = itemRounds.Values.Distinct().ToList();
        var rounds = await db.PreOrderRounds.Where(r => roundIds.Contains(r.Id) && r.Status == Domain.RoundStatus.Open).ToListAsync(ct);
        return rounds.Select(r => (r, lines.Where(l => itemRounds.GetValueOrDefault(l.ItemId) == r.Id).Sum(l => l.Quantity))).ToList();
    }
}
