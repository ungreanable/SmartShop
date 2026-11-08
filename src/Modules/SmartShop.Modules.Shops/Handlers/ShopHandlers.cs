using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Reviews;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;
using SmartShop.SharedKernel.Scheduling;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Shops.Handlers;

/// <summary>
/// Re-evaluates a shop's open/closed status. Runs right after any schedule change and again at the next
/// schedule boundary, so "shop opened/closed" is announced as an event even when nobody touches the app.
/// </summary>
[RequiresEagerTransaction]
public static class EvaluateShopStatusHandler
{
    public static async Task Handle(EvaluateShopStatus command, ShopsDbContext db, IMessageBus bus, HybridCache cache,
        TimeProvider clock, CancellationToken ct)
    {
        var shop = await db.Shops.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.ShopId, ct);
        if (shop is null) return;
        // Locked: the scheduled evaluation, one triggered by a settings change and the status sweep may run at once, and
        // both would announce the same change (a second "your favourite shop opened" push).
        var tracker = (await db.StatusTrackers
            .FromSql($"select * from shops.status_trackers where shop_id = {shop.Id} for update").ToListAsync(ct)).SingleOrDefault();
        if (tracker is null) db.StatusTrackers.Add(tracker = new ShopStatusTracker(shop.Id));

        var now = clock.GetUtcNow();
        var schedule = shop.ToSchedule();
        var status = ShopScheduleEvaluator.Evaluate(schedule, now);

        if (tracker.LastAnnouncedState != status.State)
        {
            var becameOpen = status.State == ShopOpenState.Open && tracker.LastAnnouncedState is null or ShopOpenState.Closed;
            tracker.MarkAnnounced(status.State);
            await bus.PublishAsync(new ShopStatusChanged(shop.PlantId, shop.Id, shop.Name, status.State, becameOpen));
            await cache.RemoveAsync(CacheKeys.ShopProfile(shop.Id), ct);
        }

        var next = ShopScheduleEvaluator.NextBoundary(schedule, now);
        if (tracker.ScheduleEvaluation(next))
            await bus.ScheduleAsync(new EvaluateShopStatus(shop.Id), next!.Value.AddSeconds(1));
    }
}

public static class ReviewSubmittedHandler
{
    public static async Task Handle(ReviewSubmitted e, ShopsDbContext db, IMessageBus bus, CancellationToken ct)
    {
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.Id == e.ShopId, ct);
        if (shop is null) return;
        shop.UpdateRating(e.Average, e.Count);
        await bus.PublishAsync(new ShopRatingChanged(shop.PlantId, shop.Id, shop.RatingAverage, shop.RatingCount));
    }
}

public static class ReviewModeratedHandler
{
    public static async Task Handle(ReviewModerated e, ShopsDbContext db, IMessageBus bus, CancellationToken ct)
    {
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.Id == e.ShopId, ct);
        if (shop is null) return;
        shop.UpdateRating(e.Average, e.Count);
        await bus.PublishAsync(new ShopRatingChanged(shop.PlantId, shop.Id, shop.RatingAverage, shop.RatingCount));
    }
}
