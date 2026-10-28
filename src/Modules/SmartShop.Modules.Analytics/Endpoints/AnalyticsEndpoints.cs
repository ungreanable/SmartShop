using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Analytics.Data;
using SmartShop.Modules.Analytics.Services;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Analytics.Endpoints;

/// <summary>What the client may report. Everything else (who, when, which village) is decided by the server.</summary>
public sealed record AnalyticsEventRequest(string? Type, Guid ShopId, Guid? ItemId, int? Quantity, bool? Custom);
public sealed record PresenceDto(int Count);

public sealed record AnalyticsTotalsDto(int Visitors, int ShopViews, int ItemViewers, int CartAdders, int CartViewers, int Buyers,
    int Orders, decimal Revenue, int CompletedOrders, decimal CompletedRevenue, int LostOrders, int AbandonedCarts, int RepeatOrders);
public sealed record AnalyticsDayDto(DateOnly Day, int Visitors, int Orders, decimal Revenue);
public sealed record AnalyticsItemDto(Guid ItemId, string Name, int Views, int Viewers, int AddToCart, int Buyers, int OrderedQuantity,
    decimal Conversion, bool LowConversion);
/// <param name="Heatmap">Shop-page views as [day of week, Sunday = 0][hour 0-23], Bangkok time.</param>
public sealed record AnalyticsDashboardDto(DateOnly From, DateOnly To, AnalyticsTotalsDto Totals, List<AnalyticsDayDto> Daily,
    List<AnalyticsItemDto> Items, int[][] Heatmap, DateTimeOffset? UpdatedAt);

internal static class AnalyticsEndpoints
{
    public const string RateLimitPolicy = "analytics";
    public const int MaxBatch = 50;
    private const long MaxBodyBytes = 16 * 1024;

    // "Viewed a lot, ordered little": enough viewers to mean something in a village, and few of them ordering.
    private const int LowConversionMinViewers = 8;
    private const decimal LowConversionBelow = 0.15m;

    private static readonly Dictionary<string, AnalyticsEventType> ClientTypes = new(StringComparer.Ordinal)
    {
        ["shop_view"] = AnalyticsEventType.ShopView,
        ["item_view"] = AnalyticsEventType.ItemView,
        ["add_to_cart"] = AnalyticsEventType.AddToCart,
        ["cart_view"] = AnalyticsEventType.CartView,
    };

    public static void Map(IEndpointRouteBuilder api)
    {
        var analytics = api.MapGroup("/analytics").WithTags("Analytics").RequirePlantMember().RequireRateLimiting(RateLimitPolicy);

        // Fire-and-forget from the client: a small batch, at most MaxBatch events; unknown types are dropped.
        analytics.MapPost("/events", async (HttpRequest http, ICurrentPlant plant, ICurrentUser user,
            ShopGate gate, VisitorKeys keys, AnalyticsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var events = await ReadBatchAsync(http, ct);
            var now = clock.GetUtcNow();
            var visitor = keys.Visitor(user.Id, VisitorKeys.BangkokDay(now));
            var rows = new List<AnalyticsEvent>();
            var shops = events.Take(MaxBatch).Where(e => e.Type is not null && ClientTypes.ContainsKey(e.Type)).GroupBy(e => e.ShopId).ToList();
            var foreign = 0;
            foreach (var shop in shops)
            {
                // Events queued just before the user switched village arrive with the new village: drop those, keep the rest.
                if (!await gate.IsInPlantAsync(shop.Key, ct))
                {
                    foreign++;
                    continue;
                }
                if (await gate.IsMemberAsync(shop.Key, ct)) continue;
                foreach (var e in shop)
                {
                    var type = ClientTypes[e.Type!];
                    // A page that renders twice must not count twice: identical views within one batch count once.
                    if (type != AnalyticsEventType.AddToCart && rows.Any(r => r.ShopId == e.ShopId && r.Type == type && r.ItemId == e.ItemId)) continue;
                    rows.Add(new AnalyticsEvent
                    {
                        PlantId = plant.PlantId,
                        ShopId = e.ShopId,
                        Type = type,
                        ItemId = type is AnalyticsEventType.ItemView or AnalyticsEventType.AddToCart && e.ItemId != Guid.Empty ? e.ItemId : null,
                        Visitor = visitor,
                        Quantity = type == AnalyticsEventType.AddToCart ? Math.Clamp(e.Quantity ?? 1, 1, 999) : 0,
                        Custom = type == AnalyticsEventType.AddToCart && e.Custom == true,
                        OccurredAt = now,
                    });
                }
            }
            if (foreign > 0 && foreign == shops.Count) throw new NotFoundException("Shop", shops[0].Key);
            if (rows.Count > 0)
            {
                db.Events.AddRange(rows);
                await db.SaveChangesAsync(ct);
            }
            return Results.Accepted(value: new { accepted = rows.Count });
        }).Accepts<List<AnalyticsEventRequest>>("application/json");

        // Heartbeat while the shop page is visible; answers how many people (the caller included) look at it now.
        analytics.MapPost("/presence/{shopId:guid}", async (Guid shopId, ICurrentUser user, ShopGate gate, VisitorKeys keys,
            IPresenceTracker presence, TimeProvider clock, CancellationToken ct) =>
        {
            await gate.RequireInPlantAsync(shopId, ct);
            if (await gate.IsMemberAsync(shopId, ct)) return new PresenceDto(await presence.CountAsync(shopId, ct));
            var visitor = keys.Visitor(user.Id, VisitorKeys.BangkokDay(clock.GetUtcNow()));
            return new PresenceDto(await presence.TouchAsync(shopId, visitor, ct));
        });

        analytics.MapDelete("/presence/{shopId:guid}", async (Guid shopId, ICurrentUser user, VisitorKeys keys, IPresenceTracker presence,
            TimeProvider clock, CancellationToken ct) =>
        {
            await presence.LeaveAsync(shopId, keys.Visitor(user.Id, VisitorKeys.BangkokDay(clock.GetUtcNow())), ct);
            return Results.NoContent();
        });

        var merchant = api.MapGroup("/merchant/shops/{shopId:guid}/analytics").WithTags("Merchant analytics").RequirePlantMember();

        merchant.MapGet("/live", async (Guid shopId, IShopAccess access, IPresenceTracker presence, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Staff, ct);
            return new PresenceDto(await presence.CountAsync(shopId, ct));
        });

        // Same audience as the sales report. Reads only the aggregates (at most 90 rows per table and shop).
        merchant.MapGet("/", async (Guid shopId, int? days, IShopAccess access, ICurrentPlant plant, AnalyticsDbContext db,
            ICatalogService catalog, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Manager, ct);
            var span = days is 30 or 90 ? days.Value : 7;
            var to = VisitorKeys.BangkokDay(clock.GetUtcNow());
            var from = to.AddDays(-(span - 1));

            var daily = await db.ShopDaily.AsNoTracking()
                .Where(d => d.ShopId == shopId && d.PlantId == plant.PlantId && d.Day >= from && d.Day <= to).ToListAsync(ct);
            var items = await db.ItemDaily.AsNoTracking()
                .Where(d => d.ShopId == shopId && d.PlantId == plant.PlantId && d.Day >= from && d.Day <= to)
                .GroupBy(d => d.ItemId)
                .Select(g => new
                {
                    ItemId = g.Key,
                    Views = g.Sum(x => x.Views),
                    Viewers = g.Sum(x => x.Viewers),
                    AddToCart = g.Sum(x => x.AddToCart),
                    Buyers = g.Sum(x => x.Buyers),
                    Ordered = g.Sum(x => x.OrderedQuantity),
                })
                .ToListAsync(ct);
            var hours = await db.ShopHourly.AsNoTracking()
                .Where(h => h.ShopId == shopId && h.PlantId == plant.PlantId && h.Day >= from && h.Day <= to)
                .Select(h => new { h.Day, h.Hour, h.Views }).ToListAsync(ct);

            var byDay = daily.ToDictionary(d => d.Day);
            var series = Enumerable.Range(0, span).Select(i => from.AddDays(i)).Select(day => byDay.TryGetValue(day, out var d)
                ? new AnalyticsDayDto(day, d.Visitors, d.Orders, d.Revenue)
                : new AnalyticsDayDto(day, 0, 0, 0)).ToList();

            var totals = new AnalyticsTotalsDto(
                daily.Sum(d => d.Visitors), daily.Sum(d => d.ShopViews), daily.Sum(d => d.ItemViewers), daily.Sum(d => d.CartAdders),
                daily.Sum(d => d.CartViewers), daily.Sum(d => d.Buyers), daily.Sum(d => d.Orders), daily.Sum(d => d.Revenue),
                daily.Sum(d => d.CompletedOrders), daily.Sum(d => d.CompletedRevenue), daily.Sum(d => d.LostOrders),
                daily.Sum(d => d.AbandonedCarts), daily.Sum(d => d.RepeatOrders));

            // Names come from the live menu; ids that are not (or no longer) on it are left out, which also ignores any
            // made-up item ids a client might send.
            var names = (await catalog.ListItemsAsync(shopId, ct)).ToDictionary(i => i.ItemId, i => i.Name);
            var top = items.Where(i => names.ContainsKey(i.ItemId))
                .Select(i =>
                {
                    var conversion = i.Viewers == 0 ? 0 : Math.Min(1m, decimal.Round((decimal)i.Buyers / i.Viewers, 3));
                    return new AnalyticsItemDto(i.ItemId, names[i.ItemId], i.Views, i.Viewers, i.AddToCart, i.Buyers, i.Ordered, conversion,
                        i.Viewers >= LowConversionMinViewers && conversion < LowConversionBelow);
                })
                .OrderByDescending(i => i.Views).ThenByDescending(i => i.OrderedQuantity).Take(20).ToList();

            var heatmap = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
            foreach (var h in hours) heatmap[(int)h.Day.DayOfWeek][h.Hour] += h.Views;

            return new AnalyticsDashboardDto(from, to, totals, series, top, heatmap, daily.Count == 0 ? null : daily.Max(d => d.UpdatedAt));
        });
    }

    /// <summary>
    /// Read by hand so the size cap applies before anything is parsed: model binding would first deserialize whatever
    /// the client sent (up to Kestrel's 30 MB default).
    /// </summary>
    private static async Task<List<AnalyticsEventRequest>> ReadBatchAsync(HttpRequest http, CancellationToken ct)
    {
        if (http.ContentLength > MaxBodyBytes) throw new TooLargeException("batch_too_large", "Too many analytics events.");
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
        try
        {
            return await http.ReadFromJsonAsync<List<AnalyticsEventRequest>>(ct) ?? [];
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new TooLargeException("batch_too_large", "Too many analytics events.");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new DomainException("invalid_events", "The analytics batch is not valid JSON.");
        }
    }
}
