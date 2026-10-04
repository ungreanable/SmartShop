using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Catalog.Services;

internal sealed class CatalogService(CatalogDbContext db, TimeProvider clock) : ICatalogService
{
    public async Task<IReadOnlyList<CheckoutItem>> GetCheckoutItemsAsync(Guid shopId, IReadOnlyCollection<Guid> itemIds, DateTimeOffset at, CancellationToken ct = default)
    {
        var items = await db.Items.AsNoTracking().Where(i => i.ShopId == shopId && itemIds.Contains(i.Id)).ToListAsync(ct);
        var stock = await db.Inventories.AsNoTracking().Where(s => itemIds.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, ct);
        var roundIds = items.Where(i => i.PreOrderRoundId is not null).Select(i => i.PreOrderRoundId!.Value).Distinct().ToList();
        var rounds = await db.PreOrderRounds.AsNoTracking().Where(r => roundIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var now = clock.GetUtcNow();

        return items.Select(i =>
        {
            int? available = i.IsCounted ? stock.GetValueOrDefault(i.Id)?.Available ?? 0 : null;
            var round = i.PreOrderRoundId is { } rid ? rounds.GetValueOrDefault(rid) : null;
            // Pre-order items are judged at order time against the round, not against pick-up time.
            var reason = i.UnavailableReason(round is null ? at : now, available);
            if (reason is null && round is not null && !round.IsOpenAt(now)) reason = "round_closed";
            return new CheckoutItem(i.Id, i.ShopId, i.Name, i.Price, i.Kind, i.StockMode, reason is null, reason, available,
                i.MaxPerOrder, i.DurationMinutes,
                i.ModifierGroups.Select(g => new ModifierGroupInfo(g.Id, g.Name, g.MinSelect, g.MaxSelect,
                    g.Options.Select(o => new ModifierOptionInfo(o.Id, o.Name, o.PriceDelta, o.IsAvailable)).ToList())).ToList(),
                i.AllowedFulfillment,
                round is null ? null : new PreOrderRoundInfo(round.Id, round.Title, round.OrderCutoff, round.FulfillFrom, round.FulfillTo,
                    round.Status.ToString(), round.MinTotalQuantity),
                i.ImageIds.FirstOrDefault() is var img && img != Guid.Empty ? img : null);
        }).ToList();
    }

    public async Task ReserveAsync(DbTransaction transaction, Guid orderId, IReadOnlyList<ReserveLine> lines, Guid actorId, CancellationToken ct = default)
    {
        var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var connection = (NpgsqlConnection)transaction.Connection!;
        var now = clock.GetUtcNow();

        // Lock rows in a stable order to avoid deadlocks between concurrent checkouts.
        foreach (var group in lines.GroupBy(l => (l.ItemId, l.SlotStart)).OrderBy(g => g.Key.ItemId).ThenBy(g => g.Key.SlotStart))
        {
            var item = items.GetValueOrDefault(group.Key.ItemId) ?? throw new NotFoundException("Item", group.Key.ItemId);
            var quantity = group.Sum(l => l.Quantity);

            if (item.IsCounted)
                await ReserveCountedAsync(connection, transaction, item, orderId, quantity, actorId, now, ct);
            else if (item.StockMode == StockMode.Slot)
                await ReserveSlotAsync(connection, transaction, item, orderId, group.Key.SlotStart, quantity, now, ct);
        }
    }

    private static async Task ReserveCountedAsync(NpgsqlConnection connection, DbTransaction tx, Item item, Guid orderId, int quantity,
        Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        // Atomic check-and-reserve: succeeds only when enough stock is available, no explicit lock needed.
        const string sql = """
            WITH upd AS (
                UPDATE catalog.inventories SET reserved = reserved + @qty
                WHERE item_id = @item AND on_hand - reserved >= @qty
                RETURNING item_id, shop_id, on_hand, reserved)
            INSERT INTO catalog.stock_movements (id, item_id, shop_id, type, quantity, on_hand_after, reserved_after, order_id, actor_id, at)
            SELECT @id, item_id, shop_id, 'Reserve', @qty, on_hand, reserved, @order, @actor, @at FROM upd
            RETURNING 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("item", item.Id);
        cmd.Parameters.AddWithValue("id", Ids.New());
        cmd.Parameters.AddWithValue("order", orderId);
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("at", now);
        if (await cmd.ExecuteScalarAsync(ct) is null)
            throw new DomainException("out_of_stock", $"\"{item.Name}\" does not have enough stock left.");
    }

    private static async Task ReserveSlotAsync(NpgsqlConnection connection, DbTransaction tx, Item item, Guid orderId, DateTimeOffset? slotStart,
        int quantity, DateTimeOffset now, CancellationToken ct)
    {
        if (slotStart is not { } localStart) throw new DomainException("slot_required", $"Please choose a time slot for \"{item.Name}\".");
        var start = localStart.ToUniversalTime();
        var end = start.AddMinutes(item.DurationMinutes ?? 60);

        // Serialise bookings of the same slot, then check remaining capacity.
        const string sql = """
            SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));
            INSERT INTO catalog.slot_bookings (id, item_id, shop_id, order_id, slot_start, slot_end, quantity, status, created_at)
            SELECT @id, @item, @shop, @order, @start, @end, @qty, 'Reserved', @now
            WHERE (SELECT coalesce(sum(quantity), 0) FROM catalog.slot_bookings
                   WHERE item_id = @item AND slot_start = @start AND status <> 'Released') + @qty <= @capacity
            RETURNING 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("key", $"{item.Id}|{start.ToUnixTimeSeconds()}");
        cmd.Parameters.AddWithValue("id", Ids.New());
        cmd.Parameters.AddWithValue("item", item.Id);
        cmd.Parameters.AddWithValue("shop", item.ShopId);
        cmd.Parameters.AddWithValue("order", orderId);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", end);
        cmd.Parameters.AddWithValue("qty", quantity);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("capacity", item.SlotCapacity ?? 1);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct); // skip advisory lock result
        if (!await reader.ReadAsync(ct))
            throw new DomainException("slot_full", $"The selected time for \"{item.Name}\" is fully booked. Please choose another time.");
    }

    public async Task<ItemListingSnapshot?> GetListingSnapshotAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return null;
        var stock = item.IsCounted ? await db.Inventories.AsNoTracking().FirstOrDefaultAsync(s => s.ItemId == itemId, ct) : null;
        var soldOut = item.UnavailableReason(clock.GetUtcNow(), stock?.Available) == "sold_out";
        return new ItemListingSnapshot(item.Id, item.ShopId, item.PlantId, item.Name, item.Description, item.Price,
            item.ImageIds.FirstOrDefault() is var img && img != Guid.Empty ? img : null,
            item.DeletedAt is null && item.IsAvailable, soldOut);
    }
}
