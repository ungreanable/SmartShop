using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Ordering;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Services;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Catalog.Handlers;

/// <summary>Stock was already reserved synchronously at checkout; here we update rounds and announce sold-out.</summary>
public static class CatalogOrderPlacedHandler
{
    public static async Task Handle(OrderPlaced e, CatalogDbContext db, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct)
    {
        await RoundCounter.AddAsync(db, e.Lines, +1, ct);
        await Announce.PublishAsync(bus, await announcer.CheckAsync(e.Lines.Select(l => l.ItemId), ct));
    }
}

[RequiresEagerTransaction]
public static class CatalogOrderCompletedHandler
{
    public static async Task Handle(OrderCompleted e, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, CancellationToken ct)
    {
        await ledger.CommitAsync(e.OrderId, e.Lines.Select(l => l.ItemId), ct);
        await Announce.PublishAsync(bus, await announcer.CheckAsync(e.Lines.Select(l => l.ItemId), ct));
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

internal static class Release
{
    public static async Task RunAsync(CatalogDbContext db, StockLedger ledger, StockAnnouncer announcer, IMessageBus bus, Guid orderId,
        IReadOnlyList<OrderLineInfo> lines, CancellationToken ct)
    {
        await ledger.ReleaseAsync(orderId, lines.Select(l => l.ItemId), ct);
        await RoundCounter.AddAsync(db, lines, -1, ct);
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

internal static class RoundCounter
{
    /// <summary>Keeps the ordered total of open pre-order rounds (group-buy targets) up to date.</summary>
    public static async Task AddAsync(CatalogDbContext db, IReadOnlyList<OrderLineInfo> lines, int sign, CancellationToken ct)
    {
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var itemRounds = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id) && i.PreOrderRoundId != null)
            .ToDictionaryAsync(i => i.Id, i => i.PreOrderRoundId!.Value, ct);
        if (itemRounds.Count == 0) return;

        var roundIds = itemRounds.Values.Distinct().ToList();
        var rounds = await db.PreOrderRounds.Where(r => roundIds.Contains(r.Id) && r.Status == Domain.RoundStatus.Open).ToListAsync(ct);
        foreach (var round in rounds)
        {
            var quantity = lines.Where(l => itemRounds.GetValueOrDefault(l.ItemId) == round.Id).Sum(l => l.Quantity);
            round.AddOrdered(sign * quantity);
        }
    }
}
