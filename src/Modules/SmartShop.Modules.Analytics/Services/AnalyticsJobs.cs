using Microsoft.EntityFrameworkCore;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Modules.Analytics.Data;

namespace SmartShop.Modules.Analytics.Services;

/// <summary>
/// Recomputes yesterday's and today's (Bangkok) aggregates from the raw events. Set-based upserts: PostgreSQL does the
/// grouping, nothing is loaded into the app (the API box has 512 MB). Recomputing instead of incrementing makes the job
/// idempotent and lets late events (an order that arrived after midnight's run) still land in yesterday's numbers.
/// Days before yesterday are final.
/// </summary>
internal sealed class AnalyticsRollupJob(AnalyticsDbContext db, TimeProvider clock) : IRecurringJob
{
    public string Name => "analytics.rollup";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = VisitorKeys.BangkokDay(now);
        var from = VisitorKeys.StartOf(today.AddDays(-1));
        var to = VisitorKeys.StartOf(today.AddDays(1));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Day = UTC time + 7 h (Thailand has no daylight saving), the same rule as VisitorKeys.BangkokDay.
        await db.Database.ExecuteSqlAsync($"""
            WITH per_visitor AS (
                SELECT shop_id, ((occurred_at AT TIME ZONE 'UTC') + interval '7 hours')::date AS day, visitor,
                       (array_agg(plant_id))[1] AS plant_id,
                       bool_or(type IN (1, 2, 3, 4, 10)) AS visited,
                       bool_or(type = 2) AS viewed_item,
                       bool_or(type = 3) AS added,
                       bool_or(type = 4) AS viewed_cart,
                       bool_or(type = 10) AS ordered,
                       count(*) FILTER (WHERE type = 1) AS shop_views,
                       count(*) FILTER (WHERE type = 10) AS orders,
                       coalesce(sum(amount) FILTER (WHERE type = 10), 0) AS revenue,
                       count(*) FILTER (WHERE type = 10 AND repeat) AS repeat_orders,
                       count(*) FILTER (WHERE type = 12) AS completed_orders,
                       coalesce(sum(amount) FILTER (WHERE type = 12), 0) AS completed_revenue,
                       count(*) FILTER (WHERE type = 13) AS lost_orders
                FROM analytics.events
                WHERE occurred_at >= {from} AND occurred_at < {to} AND type <> 11
                GROUP BY 1, 2, 3
            )
            INSERT INTO analytics.shop_daily (shop_id, day, plant_id, visitors, shop_views, item_viewers, cart_adders, cart_viewers,
                buyers, orders, revenue, completed_orders, completed_revenue, lost_orders, abandoned_carts, repeat_orders, updated_at)
            SELECT shop_id, day, (array_agg(plant_id))[1],
                   count(*) FILTER (WHERE visited)::int, sum(shop_views)::int, count(*) FILTER (WHERE viewed_item)::int,
                   count(*) FILTER (WHERE added)::int, count(*) FILTER (WHERE viewed_cart)::int, count(*) FILTER (WHERE ordered)::int,
                   sum(orders)::int, sum(revenue), sum(completed_orders)::int, sum(completed_revenue), sum(lost_orders)::int,
                   count(*) FILTER (WHERE added AND NOT ordered)::int, sum(repeat_orders)::int, {now}
            FROM per_visitor
            GROUP BY shop_id, day
            ON CONFLICT (shop_id, day) DO UPDATE SET
                visitors = excluded.visitors, shop_views = excluded.shop_views, item_viewers = excluded.item_viewers,
                cart_adders = excluded.cart_adders, cart_viewers = excluded.cart_viewers, buyers = excluded.buyers,
                orders = excluded.orders, revenue = excluded.revenue, completed_orders = excluded.completed_orders,
                completed_revenue = excluded.completed_revenue, lost_orders = excluded.lost_orders,
                abandoned_carts = excluded.abandoned_carts, repeat_orders = excluded.repeat_orders, updated_at = excluded.updated_at
            """, ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.item_daily (shop_id, item_id, day, plant_id, views, viewers, add_to_cart, buyers, ordered_quantity, updated_at)
            SELECT shop_id, item_id, ((occurred_at AT TIME ZONE 'UTC') + interval '7 hours')::date, (array_agg(plant_id))[1],
                   count(*) FILTER (WHERE type = 2)::int, count(DISTINCT visitor) FILTER (WHERE type = 2)::int,
                   count(*) FILTER (WHERE type = 3)::int, count(DISTINCT visitor) FILTER (WHERE type = 11)::int,
                   coalesce(sum(quantity) FILTER (WHERE type = 11), 0)::int, {now}
            FROM analytics.events
            WHERE occurred_at >= {from} AND occurred_at < {to} AND item_id IS NOT NULL AND type IN (2, 3, 11)
            GROUP BY 1, 2, 3
            ON CONFLICT (shop_id, item_id, day) DO UPDATE SET
                views = excluded.views, viewers = excluded.viewers, add_to_cart = excluded.add_to_cart, buyers = excluded.buyers,
                ordered_quantity = excluded.ordered_quantity, updated_at = excluded.updated_at
            """, ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO analytics.shop_hourly (shop_id, day, hour, plant_id, views)
            SELECT shop_id, local::date, extract(hour FROM local)::smallint, (array_agg(plant_id))[1], count(*)::int
            FROM (SELECT shop_id, plant_id, (occurred_at AT TIME ZONE 'UTC') + interval '7 hours' AS local
                  FROM analytics.events
                  WHERE occurred_at >= {from} AND occurred_at < {to} AND type = 1) e
            GROUP BY shop_id, local::date, extract(hour FROM local)
            ON CONFLICT (shop_id, day, hour) DO UPDATE SET views = excluded.views
            """, ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>
/// Raw events are kept <see cref="Retention"/> (owner's decision); aggregates are kept forever. Deleted in batches so
/// one run never holds long locks or a huge transaction.
/// </summary>
internal sealed class AnalyticsRetentionJob(AnalyticsDbContext db, TimeProvider clock) : IRecurringJob
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int BatchSize = 5000;

    public string Name => "analytics.retention";
    public TimeSpan Interval => TimeSpan.FromHours(12);

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        int deleted;
        do
        {
            deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM analytics.events
                WHERE id IN (SELECT id FROM analytics.events WHERE occurred_at < {cutoff} LIMIT {BatchSize})
                """, ct);
        }
        while (deleted == BatchSize);
    }
}
