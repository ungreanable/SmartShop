using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Catalog.Services;

/// <summary>
/// Commits or releases reservations made at checkout. Every operation is recorded in the ledger with a unique
/// (order, item, type) key first, so redelivered events never double-count. Must run inside a transaction.
/// </summary>
public sealed class StockLedger(CatalogDbContext db, TimeProvider clock)
{
    public Task CommitAsync(Guid orderId, IEnumerable<Guid> itemIds, CancellationToken ct) =>
        ApplyAsync(orderId, itemIds, StockMovementType.Commit, ct);

    public Task ReleaseAsync(Guid orderId, IEnumerable<Guid> itemIds, CancellationToken ct) =>
        ApplyAsync(orderId, itemIds, StockMovementType.Release, ct);

    private async Task ApplyAsync(Guid orderId, IEnumerable<Guid> itemIds, StockMovementType type, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var itemId in itemIds.Distinct().Order())
        {
            var reserved = await db.StockMovements.AsNoTracking()
                .Where(m => m.OrderId == orderId && m.ItemId == itemId && m.Type == StockMovementType.Reserve)
                .Select(m => (int?)m.Quantity).FirstOrDefaultAsync(ct);
            if (reserved is not { } quantity) continue; // untracked item: nothing reserved

            // Commit and Release are mutually exclusive outcomes of one reservation.
            var settled = await db.StockMovements.AnyAsync(m => m.OrderId == orderId && m.ItemId == itemId
                && (m.Type == StockMovementType.Commit || m.Type == StockMovementType.Release), ct);
            if (settled) continue;

            var movementId = Ids.New();
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.stock_movements (id, item_id, shop_id, type, quantity, on_hand_after, reserved_after, order_id, at)
                SELECT {movementId}, item_id, shop_id, {type.ToString()}, {quantity}, 0, 0, {orderId}, {now}
                FROM catalog.inventories WHERE item_id = {itemId}
                ON CONFLICT DO NOTHING
                """, ct);
            if (inserted == 0) continue;

            if (type == StockMovementType.Commit)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE catalog.inventories SET on_hand = on_hand - {quantity}, reserved = reserved - {quantity} WHERE item_id = {itemId}
                    """, ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE catalog.inventories SET reserved = reserved - {quantity} WHERE item_id = {itemId}
                    """, ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE catalog.stock_movements m SET on_hand_after = i.on_hand, reserved_after = i.reserved
                FROM catalog.inventories i WHERE m.id = {movementId} AND i.item_id = m.item_id
                """, ct);
        }

        var slotStatus = type == StockMovementType.Commit ? "Committed" : "Released";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.slot_bookings SET status = {slotStatus} WHERE order_id = {orderId} AND status = 'Reserved'
            """, ct);
    }
}

/// <summary>
/// Detects sold-out / back-in-stock / low-stock transitions exactly once. Returns the events so the caller can
/// publish them through its own transaction (handler context or DbContext outbox).
/// </summary>
public sealed class StockAnnouncer(CatalogDbContext db, TimeProvider clock)
{
    public async Task<List<Contracts.IntegrationEvent>> CheckAsync(IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var events = new List<Contracts.IntegrationEvent>();
        var ids = itemIds.Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        var stock = await db.Inventories.Where(s => ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
        var now = clock.GetUtcNow();

        foreach (var item in items)
        {
            var inventory = stock.GetValueOrDefault(item.Id);
            var soldOut = item.DeletedAt is null && item.IsAvailable
                          && item.UnavailableReason(now, inventory?.Available) == "sold_out";
            if (soldOut != item.AnnouncedSoldOut)
            {
                item.MarkAnnouncedSoldOut(soldOut);
                events.Add(soldOut
                    ? new ItemSoldOut(item.PlantId, item.ShopId, item.Id)
                    : new ItemBackInStock(item.PlantId, item.ShopId, item.Id));
            }

            if (item.IsCounted && inventory?.ShouldWarnLowStock() == true && !soldOut)
                events.Add(new StockLow(item.PlantId, item.ShopId, item.Id, item.Name, inventory.Available));
        }
        return events;
    }
}
