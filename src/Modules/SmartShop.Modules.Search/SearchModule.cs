using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Search;

/// <summary>Denormalised shop card for the village home feed, maintained from Shops/Catalog/Reviews events.</summary>
public sealed class ShopListing
{
    public Guid ShopId { get; set; }
    public Guid PlantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public Guid? LogoId { get; set; }
    public Guid? CoverId { get; set; }
    public string? HouseNo { get; set; }
    public bool PickupEnabled { get; set; }
    public bool DeliveryEnabled { get; set; }
    public bool AllowPreorder { get; set; }
    public int PrepTimeMinutes { get; set; }
    public decimal RatingAverage { get; set; }
    public int RatingCount { get; set; }
    public bool IsActive { get; set; }

    /// <summary>The village admin's position for the shop; null (not arranged yet) sorts after every arranged shop.</summary>
    public int? DisplayOrder { get; set; }

    public ShopScheduleSnapshot Schedule { get; set; } = new("Asia/Bangkok", [], []);

    /// <summary>Lower-case name, description, category and item names for trigram search (works for Thai).</summary>
    public string SearchText { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemListing
{
    public Guid ItemId { get; set; }
    public Guid ShopId { get; set; }
    public Guid PlantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public Guid? ImageId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsSoldOut { get; set; }
}

public sealed class SearchDbContext(DbContextOptions<SearchDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "search";

    public override string Schema => SchemaName;

    public DbSet<ShopListing> Shops => Set<ShopListing>();
    public DbSet<ItemListing> Items => Set<ItemListing>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");
        b.Entity<ShopListing>(e =>
        {
            e.ToTable("shop_listings");
            e.HasKey(x => x.ShopId);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.RatingAverage).HasPrecision(3, 2);
            e.Property(x => x.Schedule).HasColumnType("jsonb");
            e.HasIndex(x => x.PlantId);
            e.HasIndex(x => x.SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
        });
        b.Entity<ItemListing>(e =>
        {
            e.ToTable("item_listings");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Price).HasPrecision(12, 2);
            e.HasIndex(x => new { x.PlantId, x.ShopId });
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        });
    }
}

internal sealed class SearchDesignTimeFactory : ModuleDesignTimeFactory<SearchDbContext>
{
    protected override string Schema => SearchDbContext.SchemaName;
    protected override SearchDbContext Create(DbContextOptions<SearchDbContext> options) => new(options);
}

public sealed record StatusLiteDto(ShopOpenState State, ShopClosedReason Reason, DateTimeOffset? Until, DateTimeOffset? NextOpenAt, bool AcceptingOrders);
public sealed record ShopCardDto(Guid Id, string Name, string? Category, string? Description, string? LogoUrl, string? CoverUrl, string? HouseNo,
    StatusLiteDto Status, bool PickupEnabled, bool DeliveryEnabled, bool AcceptsPreorders, int PrepTimeMinutes,
    decimal RatingAverage, int RatingCount, bool IsNew);
/// <summary>
/// One page of the village shop list: shops taking orders first (<see cref="OpenCount"/> of them across all pages), then the closed
/// ones, each part in the village admin's order. The open part is therefore complete before the first closed shop arrives.
/// </summary>
/// <remarks><paramref name="OpenNow"/> is always empty: it only keeps app versions released before paging (still cached on
/// phones until the update prompt is accepted) from failing on a missing list. Remove a few releases later.</remarks>
public sealed record HomeDto(List<string> Categories, List<ShopCardDto> Shops, int OpenCount, int Total, bool HasMore)
{
    public List<ShopCardDto> OpenNow { get; } = [];
}
public sealed record ItemHitDto(Guid ItemId, Guid ShopId, string ShopName, string Name, decimal Price, string? ThumbnailUrl, bool IsSoldOut);
public sealed record SearchResultDto(List<ShopCardDto> Shops, List<ItemHitDto> Items);

public sealed class SearchModule : IModule
{
    public string Name => "Search";

    public IReadOnlyList<Type> DbContexts => [typeof(SearchDbContext)];

    public void Register(IHostApplicationBuilder builder) =>
        builder.Services.AddModuleDbContext<SearchDbContext>(SearchDbContext.SchemaName);

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/").WithTags("Home & search").RequirePlantMember();

        // Listing data is cached per plant (one entry whatever the page or category); open/closed is evaluated per request because
        // it depends on "now", then the ordered list is sliced. A village has tens of shops, so sorting them all per request is cheap.
        group.MapGet("/home", async (string? category, int? page, int? size, ICurrentPlant plant, SearchDbContext db, HybridCache cache,
            IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            var listings = await cache.GetOrCreateAsync(CacheKeys.PlantFeed(plant.PlantId),
                async token => await db.Shops.AsNoTracking().Where(s => s.PlantId == plant.PlantId && s.IsActive).ToListAsync(token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            var now = clock.GetUtcNow();
            // Cards are built for the requested page only; ordering needs just the status, position and rating.
            var ordered = listings
                .Where(l => category is null || l.Category == category)
                .Select(l => (Listing: l, Status: ShopScheduleEvaluator.Evaluate(l.Schedule, now)))
                .Where(x => x.Status.Reason != ShopClosedReason.Vacation) // vacation hides the shop from the feed
                .OrderBy(x => x.Status.AcceptingOrders ? 0 : 1)
                .ThenBy(x => x.Listing.DisplayOrder ?? int.MaxValue)
                .ThenBy(x => Rank(x.Status.State, PreorderBadge(x.Listing, x.Status)))
                .ThenBy(x => x.Status.NextOpenAt ?? DateTimeOffset.MaxValue)
                .ThenByDescending(x => x.Listing.RatingAverage)
                .ThenBy(x => x.Listing.ShopId) // stable across requests, so pages neither repeat nor skip a shop
                .ToList();

            var pageSize = Math.Clamp(size ?? 20, 1, 100);
            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000); // bounded so page * size cannot overflow
            var cards = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).Select(x => Card(x.Listing, x.Status, now, media)).ToList();
            var categories = listings.Where(l => l.Category is not null).Select(l => l.Category!).Distinct().Order().ToList();
            return new HomeDto(categories, cards, ordered.Count(x => x.Status.AcceptingOrders), ordered.Count,
                pageNumber * pageSize < ordered.Count);
        });

        group.MapGet("/search", async (string q, ICurrentPlant plant, SearchDbContext db, IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            var term = (q ?? "").Trim().ToLowerInvariant();
            if (term.Length == 0) return new SearchResultDto([], []);
            if (term.Length > 50) term = term[..50];
            var pattern = $"%{term.Replace("%", "").Replace("_", "")}%";

            var shops = await db.Shops.AsNoTracking()
                .Where(s => s.PlantId == plant.PlantId && s.IsActive && EF.Functions.ILike(s.SearchText, pattern))
                .Take(30).ToListAsync(ct);
            // Only the names: a listing row also carries the schedule jsonb, description and search text.
            var shopNames = await db.Shops.AsNoTracking().Where(s => s.PlantId == plant.PlantId && s.IsActive)
                .Select(s => new { s.ShopId, s.Name })
                .ToDictionaryAsync(s => s.ShopId, s => s.Name, ct);
            var items = await db.Items.AsNoTracking()
                .Where(i => i.PlantId == plant.PlantId && i.IsVisible && EF.Functions.ILike(i.Name, pattern))
                .OrderBy(i => i.IsSoldOut).Take(50).ToListAsync(ct);

            var now = clock.GetUtcNow();
            return new SearchResultDto(
                shops.Select(s => Card(s, ShopScheduleEvaluator.Evaluate(s.Schedule, now), now, media))
                    .OrderBy(c => Rank(c.Status.State, c.AcceptsPreorders)).ToList(),
                items.Where(i => shopNames.ContainsKey(i.ShopId))
                    .Select(i => new ItemHitDto(i.ItemId, i.ShopId, shopNames[i.ShopId], i.Name, i.Price, media.For(i.ImageId, MediaVariant.Small), i.IsSoldOut))
                    .ToList());
        });
    }

    private static int Rank(ShopOpenState state, bool acceptsPreorders) => state switch
    {
        ShopOpenState.Open => 0,
        ShopOpenState.Busy => 1,
        _ when acceptsPreorders => 2,
        _ => 3,
    };

    private static bool PreorderBadge(ShopListing l, ShopStatus s) =>
        !s.AcceptingOrders && l.AllowPreorder && s.State == ShopOpenState.Closed
        && s.Reason is not (ShopClosedReason.Suspended or ShopClosedReason.Vacation);

    private static ShopCardDto Card(ShopListing l, ShopStatus s, DateTimeOffset now, IMediaUrls media) =>
        new(l.ShopId, l.Name, l.Category, l.Description, media.For(l.LogoId, MediaVariant.Small), media.For(l.CoverId),
            l.HouseNo, new StatusLiteDto(s.State, s.Reason, s.Until, s.NextOpenAt, s.AcceptingOrders),
            l.PickupEnabled, l.DeliveryEnabled, PreorderBadge(l, s),
            l.PrepTimeMinutes, l.RatingAverage, l.RatingCount, now - l.CreatedAt < TimeSpan.FromDays(14));
}
