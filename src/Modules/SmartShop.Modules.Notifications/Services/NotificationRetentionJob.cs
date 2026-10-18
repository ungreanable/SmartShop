using Microsoft.EntityFrameworkCore;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Modules.Notifications.Data;

namespace SmartShop.Modules.Notifications.Services;

/// <summary>
/// Deletes inbox entries (and their delivery records) older than <see cref="Retention"/>. Every order step writes one
/// row per recipient plus one per channel, so without this the two biggest tables grow forever while nobody scrolls
/// back more than a few weeks. Deleted in batches so one run never holds long locks or a huge transaction.
/// </summary>
internal sealed class NotificationRetentionJob(NotificationsDbContext db, TimeProvider clock) : IRecurringJob
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int BatchSize = 1000;

    public string Name => "notifications.retention";
    public TimeSpan Interval => TimeSpan.FromHours(12);

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        while (true)
        {
            var ids = await db.Notifications.Where(n => n.CreatedAt < cutoff).OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(BatchSize).ToListAsync(ct);
            if (ids.Count == 0) return;
            await db.Deliveries.Where(d => ids.Contains(d.NotificationId)).ExecuteDeleteAsync(ct);
            await db.Notifications.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize) return;
        }
    }
}
