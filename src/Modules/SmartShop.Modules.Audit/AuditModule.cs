using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Reviews;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Audit;

/// <summary>
/// Append-only record of moderation and administration actions inside a village, built from integration events
/// so no module has to remember to write it. Idempotent on <see cref="EventId"/> (events may be delivered twice).
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry() { }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid PlantId { get; private set; }
    public string Action { get; private set; } = "";
    public Guid? ActorId { get; private set; }
    public string SubjectType { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public string? Detail { get; private set; }
    public DateTimeOffset At { get; private set; }

    public static AuditEntry From(IIntegrationEvent e, string action, Guid? actorId, string subjectType, Guid subjectId, string? detail) => new()
    {
        Id = Ids.New(),
        EventId = e.EventId,
        PlantId = e.PlantId,
        Action = action,
        ActorId = actorId is { } a && a != Guid.Empty ? a : null,
        SubjectType = subjectType,
        SubjectId = subjectId,
        Detail = detail is { Length: > 500 } ? detail[..500] : detail,
        At = e.OccurredAt,
    };
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void ConfigureModel(ModelBuilder b) =>
        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.SubjectType).HasMaxLength(30);
            e.Property(x => x.Detail).HasMaxLength(500);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.PlantId, x.At });
        });
}

internal sealed class AuditDesignTimeFactory : ModuleDesignTimeFactory<AuditDbContext>
{
    protected override string Schema => AuditDbContext.SchemaName;
    protected override AuditDbContext Create(DbContextOptions<AuditDbContext> options) => new(options);
}

public sealed record AuditDto(Guid Id, string Action, Guid? ActorId, string? ActorName, string SubjectType, Guid SubjectId, string? SubjectName, string? Detail, DateTimeOffset At);

public sealed class AuditModule : IModule
{
    public string Name => "Audit";

    public IReadOnlyList<Type> DbContexts => [typeof(AuditDbContext)];

    public void Register(IHostApplicationBuilder builder) =>
        builder.Services.AddModuleDbContext<AuditDbContext>(AuditDbContext.SchemaName);

    public void MapEndpoints(IEndpointRouteBuilder app) =>
        app.MapGet("/plant/admin/audit", async (ICurrentPlant plant, AuditDbContext db, IUserDirectory users, string? action, int? page, int? pageSize, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var number = Math.Max(page ?? 1, 1);
            var query = db.Entries.AsNoTracking().Where(e => e.PlantId == plant.PlantId);
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => e.Action.StartsWith(action));
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(e => e.At).Skip((number - 1) * size).Take(size).ToListAsync(ct);

            var userIds = rows.Select(r => r.ActorId).OfType<Guid>().Concat(rows.Where(r => r.SubjectType == "User").Select(r => r.SubjectId));
            var people = await users.GetProfilesAsync(userIds.Distinct(), ct);
            var items = rows.Select(r => new AuditDto(r.Id, r.Action, r.ActorId,
                r.ActorId is { } a ? people.GetValueOrDefault(a)?.DisplayName : null,
                r.SubjectType, r.SubjectId, r.SubjectType == "User" ? people.GetValueOrDefault(r.SubjectId)?.DisplayName : null,
                r.Detail, r.At)).ToList();
            return new PagedResult<AuditDto>(items, total, number, size);
        }).WithTags("Village admin").RequirePlantAdmin();
}

/// <summary>One method per audited event (Wolverine cannot dispatch on an interface).</summary>
public static class AuditHandler
{
    public static Task Handle(MembershipApproved e, AuditDbContext db, CancellationToken ct) => Record(db, e, "membership.approved", e.ActorId, "User", e.UserId, null, ct);
    public static Task Handle(MembershipRejected e, AuditDbContext db, CancellationToken ct) => Record(db, e, "membership.rejected", e.ActorId, "User", e.UserId, e.Reason, ct);
    public static Task Handle(MembershipSuspended e, AuditDbContext db, CancellationToken ct) => Record(db, e, "membership.suspended", e.ActorId, "User", e.UserId, e.Reason, ct);
    public static Task Handle(MembershipReinstated e, AuditDbContext db, CancellationToken ct) => Record(db, e, "membership.reinstated", e.ActorId, "User", e.UserId, null, ct);
    public static Task Handle(MembershipRoleChanged e, AuditDbContext db, CancellationToken ct) => Record(db, e, "membership.role_changed", e.ActorId, "User", e.UserId, e.Role.ToString(), ct);
    public static Task Handle(AnnouncementPublished e, AuditDbContext db, CancellationToken ct) => Record(db, e, "announcement.published", e.ActorId, "Announcement", e.AnnouncementId, e.Title, ct);

    public static Task Handle(ShopApplicationApproved e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop_application.approved", e.ActorId, "Shop", e.ShopId, e.ShopName, ct);
    public static Task Handle(ShopApplicationRejected e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop_application.rejected", e.ActorId, "ShopApplication", e.ApplicationId, $"{e.ShopName}: {e.Reason}", ct);
    public static Task Handle(ShopApplicationChangesRequested e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop_application.changes_requested", e.ActorId, "ShopApplication", e.ApplicationId, $"{e.ShopName}: {e.Note}", ct);
    public static Task Handle(ShopSuspended e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop.suspended", e.ActorId, "Shop", e.ShopId, e.Reason, ct);
    public static Task Handle(ShopReinstated e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop.reinstated", e.ActorId, "Shop", e.ShopId, null, ct);
    public static Task Handle(ShopOwnershipTransferred e, AuditDbContext db, CancellationToken ct) => Record(db, e, "shop.ownership_transferred", e.FromUserId, "User", e.ToUserId, e.ShopId.ToString(), ct);

    public static Task Handle(ReviewModerated e, AuditDbContext db, CancellationToken ct) =>
        Record(db, e, e.Hidden ? "review.hidden" : "review.unhidden", e.ActorId, "Review", e.ReviewId, null, ct);

    private static async Task Record(AuditDbContext db, IIntegrationEvent e, string action, Guid? actor, string subjectType, Guid subjectId, string? detail, CancellationToken ct)
    {
        if (await db.Entries.AnyAsync(x => x.EventId == e.EventId, ct)) return;
        db.Entries.Add(AuditEntry.From(e, action, actor, subjectType, subjectId, detail));
    }
}
