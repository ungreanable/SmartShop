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

/// <summary>
/// Review events only say that the rating changed. The figures inside them were counted when each review was saved, so two
/// reviews close together (or a redelivered older event) could leave an out-of-date rating; it is always re-read instead.
/// </summary>
public static class ReviewSubmittedHandler
{
    public static Task Handle(ReviewSubmitted e, ShopsDbContext db, IShopRatingSource ratings, IMessageBus bus, CancellationToken ct) =>
        ShopRatingRefresh.RunAsync(db, ratings, bus, e.ShopId, ct);
}

public static class ReviewModeratedHandler
{
    public static Task Handle(ReviewModerated e, ShopsDbContext db, IShopRatingSource ratings, IMessageBus bus, CancellationToken ct) =>
        ShopRatingRefresh.RunAsync(db, ratings, bus, e.ShopId, ct);
}

internal static class ShopRatingRefresh
{
    public static async Task RunAsync(ShopsDbContext db, IShopRatingSource ratings, IMessageBus bus, Guid shopId, CancellationToken ct)
    {
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.Id == shopId, ct);
        if (shop is null) return;
        var rating = await ratings.GetAsync(shopId, ct);
        shop.UpdateRating(rating.Average, rating.Count);
        await bus.PublishAsync(new ShopRatingChanged(shop.PlantId, shop.Id, shop.RatingAverage, shop.RatingCount));
    }
}
