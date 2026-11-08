using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Modules.Shops.Data;
using SmartShop.SharedKernel.Scheduling;
using Wolverine;

namespace SmartShop.Modules.Shops.Services;

/// <summary>
/// Safety net for the <see cref="EvaluateShopStatus"/> chain. Each evaluation schedules the next boundary only when it
/// differs from the one already planned, so a single lost or dead-lettered evaluation (e.g. a redeploy at a boundary, a
/// database hiccup) stopped "shop opened/closed" announcements for that shop until someone changed its settings.
/// Re-evaluates every shop whose remembered state disagrees with its schedule or whose planned evaluation is overdue.
/// </summary>
internal sealed class ShopStatusSweepJob(ShopsDbContext db, IMessageBus bus, TimeProvider clock) : IRecurringJob
{
    /// <summary>A scheduled evaluation runs a few seconds after its boundary (polling); only later than this counts as lost.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public string Name => "shops.status-sweep";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Only what the schedule needs: hours (JSON column) and closures, not members or payment methods.
        var shops = await db.Shops.AsNoTracking().IgnoreAutoIncludes().Include(s => s.Closures).ToListAsync(ct);
        var trackers = await db.StatusTrackers.AsNoTracking().ToDictionaryAsync(t => t.ShopId, ct);
        foreach (var shop in shops)
        {
            var tracker = trackers.GetValueOrDefault(shop.Id);
            var state = ShopScheduleEvaluator.Evaluate(shop.ToSchedule(), now).State;
            if (tracker is not null && tracker.LastAnnouncedState == state && !(tracker.NextEvaluationAt < now - Grace)) continue;
            await bus.PublishAsync(new EvaluateShopStatus(shop.Id));
        }
    }
}
