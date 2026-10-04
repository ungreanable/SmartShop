using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Domain;
using SmartShop.SharedKernel.Scheduling;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Catalog.Services;

/// <summary>"Chicken rice: 30 plates per day" - resets counters at the shop's chosen local time.</summary>
internal sealed class DailyStockResetJob(IDbContextOutbox<CatalogDbContext> outbox, TimeProvider clock) : IRecurringJob
{
    public string Name => "catalog.daily-stock-reset";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var db = outbox.DbContext;
        var now = clock.GetUtcNow();
        var candidates = await (from i in db.Items
                                join s in db.Inventories on i.Id equals s.ItemId
                                where i.StockMode == StockMode.Daily && i.DeletedAt == null && s.DailyQuota != null
                                select new { Item = i, Stock = s }).ToListAsync(ct);

        var reset = new List<Guid>();
        foreach (var c in candidates)
        {
            var local = TimeZoneInfo.ConvertTime(now, ShopScheduleEvaluator.ResolveTimeZone(c.Item.TimeZoneId));
            var today = DateOnly.FromDateTime(local.DateTime);
            var resetAt = c.Stock.DailyResetTime ?? new TimeOnly(0, 0);
            if (TimeOnly.FromDateTime(local.DateTime) < resetAt) continue;
            var movement = c.Stock.DailyReset(today, now);
            if (movement is null) continue;
            db.StockMovements.Add(movement);
            reset.Add(c.Item.Id);
        }

        if (reset.Count == 0) return;
        foreach (var c in candidates.Where(c => reset.Contains(c.Item.Id) && c.Item.AnnouncedSoldOut))
        {
            c.Item.MarkAnnouncedSoldOut(false);
            await outbox.PublishAsync(new ItemBackInStock(c.Item.PlantId, c.Item.ShopId, c.Item.Id));
        }
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }
}

/// <summary>Closes pre-order rounds at their cut-off and decides whether a group-buy target was reached.</summary>
internal sealed class PreOrderRoundCloseJob(IDbContextOutbox<CatalogDbContext> outbox, TimeProvider clock) : IRecurringJob
{
    public string Name => "catalog.close-preorder-rounds";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken ct)
    {
        var db = outbox.DbContext;
        var now = clock.GetUtcNow();
        var due = await db.PreOrderRounds.Where(r => r.Status == RoundStatus.Open && r.OrderCutoff <= now).ToListAsync(ct);
        foreach (var round in due)
        {
            var reached = round.Close(now);
            await outbox.PublishAsync(new PreOrderRoundClosed(round.PlantId, round.ShopId, round.Id, round.Title, reached));
        }
        if (due.Count > 0) await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }
}
