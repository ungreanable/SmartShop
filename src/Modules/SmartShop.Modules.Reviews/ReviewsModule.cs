using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Reviews;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Reviews;

/// <summary>One review per completed order; the shop may reply once, admins may hide abusive reviews.</summary>
public sealed class Review
{
    private Review() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public int Rating { get; private set; }
    public string? Comment { get; private set; }
    public string? Reply { get; private set; }
    public DateTimeOffset? RepliedAt { get; private set; }
    public bool Hidden { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Review Create(Guid orderId, Guid shopId, Guid plantId, Guid customerId, int rating, string? comment, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        OrderId = orderId,
        ShopId = shopId,
        PlantId = plantId,
        CustomerId = customerId,
        Rating = Guard.Range(rating, "Rating", 1, 5),
        Comment = Guard.Optional(comment, "Comment", 1000),
        CreatedAt = now,
    };

    public void AnswerWith(string reply, DateTimeOffset now)
    {
        if (Reply is not null) throw new ConflictException("already_replied", "The shop already replied to this review.");
        Reply = Guard.NotEmpty(reply, "Reply", 1000);
        RepliedAt = now;
    }

    public void SetHidden(bool hidden) => Hidden = hidden;

    public void Anonymise() => Comment = null;
}

public sealed class ReviewsDbContext(DbContextOptions<ReviewsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "reviews";

    public override string Schema => SchemaName;

    public DbSet<Review> Reviews => Set<Review>();

    protected override void ConfigureModel(ModelBuilder b) =>
        b.Entity<Review>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.Property(x => x.Reply).HasMaxLength(1000);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.ShopId, x.CreatedAt });
        });
}

internal sealed class ReviewsDesignTimeFactory : ModuleDesignTimeFactory<ReviewsDbContext>
{
    protected override string Schema => ReviewsDbContext.SchemaName;
    protected override ReviewsDbContext Create(DbContextOptions<ReviewsDbContext> options) => new(options);
}

public sealed record ReviewRequest(int Rating, string? Comment);
public sealed record ReplyRequest(string Reply);
public sealed record HideRequest(bool Hidden);
public sealed record ReviewDto(Guid Id, Guid OrderId, Guid CustomerId, string CustomerName, string? CustomerPictureUrl, int Rating, string? Comment,
    string? Reply, DateTimeOffset CreatedAt, DateTimeOffset? RepliedAt, bool Hidden);
public sealed record ReviewSummaryDto(decimal Average, int Count, Dictionary<int, int> Distribution, List<ReviewDto> Reviews);

public sealed class ReviewsModule : IModule
{
    public string Name => "Reviews";

    public IReadOnlyList<Type> DbContexts => [typeof(ReviewsDbContext)];

    public void Register(IHostApplicationBuilder builder) =>
        builder.Services.AddModuleDbContext<ReviewsDbContext>(ReviewsDbContext.SchemaName);

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // Only the customer of a completed order may review it, once.
        app.MapPost("/orders/{orderId:guid}/review", async (Guid orderId, ReviewRequest req, ICurrentPlant plant, ICurrentUser user,
            IOrderDirectory orders, IDbContextOutbox<ReviewsDbContext> outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var order = await orders.GetAsync(orderId, ct);
            if (order is null || order.PlantId != plant.PlantId || order.CustomerId != user.Id) throw new NotFoundException("Order", orderId);
            if (order.Status != OrderStatus.Completed) throw new DomainException("order_not_completed", "You can review an order after it is completed.");
            var db = outbox.DbContext;
            if (await db.Reviews.AnyAsync(r => r.OrderId == orderId, ct)) throw new ConflictException("already_reviewed", "You already reviewed this order.");

            var review = Review.Create(orderId, order.ShopId, order.PlantId, user.Id, req.Rating, req.Comment, clock.GetUtcNow());
            db.Reviews.Add(review);
            await db.SaveChangesAsync(ct);
            var (average, count) = await StatsAsync(db, order.ShopId, ct);
            await outbox.PublishAsync(new ReviewSubmitted(order.PlantId, review.Id, orderId, order.ShopId, user.Id, review.Rating, average, count));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.Created($"/api/shops/{order.ShopId}/reviews", new { review.Id });
        }).WithTags("Reviews").RequirePlantMember();

        app.MapGet("/shops/{shopId:guid}/reviews", async (Guid shopId, ICurrentPlant plant, IShopAccess access, ReviewsDbContext db,
            IUserDirectory users, CancellationToken ct) =>
        {
            var canModerate = plant.Membership.Role == PlantRole.PlantAdmin || await access.GetRoleAsync(shopId, ct) is not null;
            var query = db.Reviews.AsNoTracking().Where(r => r.ShopId == shopId && r.PlantId == plant.PlantId);
            var visible = query.Where(r => !r.Hidden);
            var (average, count) = await StatsAsync(db, shopId, ct);
            var distribution = await visible.GroupBy(r => r.Rating).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var reviews = await (canModerate ? query : visible).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
            var people = await users.GetProfilesAsync(reviews.Select(r => r.CustomerId), ct);
            return new ReviewSummaryDto(average, count, distribution, reviews.Select(r => new ReviewDto(r.Id, r.OrderId, r.CustomerId,
                people.GetValueOrDefault(r.CustomerId)?.DisplayName ?? "?", people.GetValueOrDefault(r.CustomerId)?.PictureUrl,
                r.Rating, r.Comment, r.Reply, r.CreatedAt, r.RepliedAt, r.Hidden)).ToList());
        }).WithTags("Reviews").RequirePlantMember();

        app.MapPost("/merchant/reviews/{reviewId:guid}/reply", async (Guid reviewId, ReplyRequest req, IShopAccess access,
            IDbContextOutbox<ReviewsDbContext> outbox, ICurrentPlant plant, TimeProvider clock, CancellationToken ct) =>
        {
            var review = await outbox.DbContext.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.PlantId == plant.PlantId, ct)
                         ?? throw new NotFoundException("Review", reviewId);
            await access.RequireAsync(review.ShopId, ShopRole.Manager, ct);
            review.AnswerWith(req.Reply, clock.GetUtcNow());
            await outbox.PublishAsync(new ReviewReplied(review.PlantId, review.Id, review.ShopId, review.CustomerId));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        }).WithTags("Reviews").RequirePlantMember();

        app.MapPut("/plant/admin/reviews/{reviewId:guid}/hidden", async (Guid reviewId, HideRequest req, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ReviewsDbContext> outbox, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.PlantId == plant.PlantId, ct)
                         ?? throw new NotFoundException("Review", reviewId);
            review.SetHidden(req.Hidden);
            await db.SaveChangesAsync(ct);
            var (average, count) = await StatsAsync(db, review.ShopId, ct);
            await outbox.PublishAsync(new ReviewModerated(review.PlantId, review.Id, review.ShopId, user.Id, review.Hidden, average, count));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        }).WithTags("Village admin").RequirePlantAdmin();
    }

    private static async Task<(decimal Average, int Count)> StatsAsync(ReviewsDbContext db, Guid shopId, CancellationToken ct)
    {
        var visible = db.Reviews.Where(r => r.ShopId == shopId && !r.Hidden);
        var count = await visible.CountAsync(ct);
        var average = count == 0 ? 0 : (decimal)await visible.AverageAsync(r => r.Rating, ct);
        return (decimal.Round(average, 2), count);
    }
}

public static class ReviewsUserErasedHandler
{
    public static async Task Handle(UserErased e, ReviewsDbContext db, CancellationToken ct)
    {
        foreach (var review in await db.Reviews.Where(r => r.CustomerId == e.UserId).ToListAsync(ct)) review.Anonymise();
    }
}
