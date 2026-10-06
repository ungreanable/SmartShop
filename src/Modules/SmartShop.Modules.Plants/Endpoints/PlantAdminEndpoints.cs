using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Plants;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Plants.Data;
using SmartShop.Modules.Plants.Domain;
using SmartShop.Modules.Plants.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Plants.Endpoints;

public sealed record MemberRow(
    Guid MembershipId, Guid UserId, string DisplayName, string? PictureUrl, PlantRole Role, MembershipStatus Status,
    string? HouseNo, string? Soi, string? Nickname, string? RequestMessage, string? DecisionReason, DateTimeOffset RequestedAt);

public sealed record DecisionRequest(string? Reason);
public sealed record ChangeRoleRequest(PlantRole Role);
public sealed record JoinLink(string JoinCode, string Url);
public sealed record UpdatePlantRequest(string Name, string? Description, Guid? PictureId, PushRequirement PushRequirement, string? TimeZoneId);
public sealed record PlantCounts(int Active, int Pending, int Suspended, int Admins);

internal static class PlantAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/plant/admin").WithTags("Village admin").RequirePlantAdmin();

        admin.MapGet("/members", async (MembershipStatus? status, string? q, int? page, int? pageSize,
            ICurrentPlant plant, PlantsDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var number = Math.Max(page ?? 1, 1);
            var query = db.Memberships.AsNoTracking().Where(m => m.PlantId == plant.PlantId);
            query = status is { } s ? query.Where(m => m.Status == s) : query.Where(m => m.Status != MembershipStatus.Left);
            if (!string.IsNullOrWhiteSpace(q))
            {
                // LINE display names live in the Identity module: find the matching members there, then OR them in.
                var pattern = $"%{Like.Escape(q.Trim())}%";
                var named = await users.SearchByNameAsync(q, await query.Select(m => m.UserId).ToListAsync(ct), ct);
                query = query.Where(m => named.Contains(m.UserId)
                    || EF.Functions.ILike(m.HouseNo!, pattern) || EF.Functions.ILike(m.Nickname!, pattern) || EF.Functions.ILike(m.Soi!, pattern));
            }

            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(m => m.Status == MembershipStatus.Pending ? 0 : 1).ThenByDescending(m => m.RequestedAt)
                .Skip((number - 1) * size).Take(size).ToListAsync(ct);
            var profiles = await users.GetProfilesAsync(rows.Select(r => r.UserId), ct);

            var items = rows.Select(m =>
            {
                var profile = profiles.GetValueOrDefault(m.UserId);
                return new MemberRow(m.Id, m.UserId, profile?.DisplayName ?? "?", profile?.PictureUrl, m.Role, m.Status,
                    m.HouseNo, m.Soi, m.Nickname, m.RequestMessage, m.DecisionReason, m.RequestedAt);
            }).ToList();
            return new PagedResult<MemberRow>(items, total, number, size);
        });

        admin.MapGet("/counts", async (ICurrentPlant plant, PlantsDbContext db, CancellationToken ct) =>
        {
            var counts = await db.Memberships.AsNoTracking().Where(m => m.PlantId == plant.PlantId)
                .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var admins = await db.Memberships.CountAsync(m => m.PlantId == plant.PlantId && m.Role == PlantRole.PlantAdmin && m.Status == MembershipStatus.Active, ct);
            int Of(MembershipStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
            return new PlantCounts(Of(MembershipStatus.Active), Of(MembershipStatus.Pending), Of(MembershipStatus.Suspended), admins);
        });

        admin.MapPost("/members/{id:guid}/approve", (Guid id, Ctx ctx, CancellationToken ct) =>
            ctx.DecideAsync(id, (m, actor, now) =>
            {
                m.Approve(actor, now);
                return new MembershipApproved(m.PlantId, m.Id, m.UserId, actor);
            }, ct));

        admin.MapPost("/members/{id:guid}/reject", (Guid id, DecisionRequest req, Ctx ctx, CancellationToken ct) =>
            ctx.DecideAsync(id, (m, actor, now) =>
            {
                m.Reject(actor, req.Reason, now);
                return new MembershipRejected(m.PlantId, m.Id, m.UserId, actor, req.Reason);
            }, ct));

        admin.MapPost("/members/{id:guid}/suspend", (Guid id, DecisionRequest req, Ctx ctx, CancellationToken ct) =>
            ctx.DecideAsync(id, (m, actor, now) =>
            {
                if (m.UserId == actor) throw new DomainException("cannot_suspend_self", "You cannot suspend yourself.");
                m.Suspend(actor, req.Reason, now);
                return new MembershipSuspended(m.PlantId, m.Id, m.UserId, actor, req.Reason);
            }, ct, checkLastAdmin: true));

        admin.MapPost("/members/{id:guid}/reinstate", (Guid id, Ctx ctx, CancellationToken ct) =>
            ctx.DecideAsync(id, (m, actor, now) =>
            {
                m.Reinstate(actor, now);
                return new MembershipReinstated(m.PlantId, m.Id, m.UserId, actor);
            }, ct));

        admin.MapPut("/members/{id:guid}/role", (Guid id, ChangeRoleRequest req, Ctx ctx, CancellationToken ct) =>
            ctx.DecideAsync(id, (m, actor, _) =>
            {
                m.ChangeRole(req.Role);
                return new MembershipRoleChanged(m.PlantId, m.UserId, req.Role, actor);
            }, ct, checkLastAdmin: req.Role != PlantRole.PlantAdmin));

        admin.MapGet("/join-link", async (ICurrentPlant plant, PlantsDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var p = await db.Plants.AsNoTracking().FirstAsync(x => x.Id == plant.PlantId, ct);
            return new JoinLink(p.JoinCode, JoinUrl(config, p.JoinCode));
        });

        // Use when the link leaks outside the village group chat.
        admin.MapPost("/join-link/reset", async (ICurrentPlant plant, PlantsDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var p = await db.Plants.FirstAsync(x => x.Id == plant.PlantId, ct);
            var code = p.ResetJoinCode();
            await db.SaveChangesAsync(ct);
            return new JoinLink(code, JoinUrl(config, code));
        });

        admin.MapPut("/settings", async (UpdatePlantRequest req, ICurrentPlant plant, ICurrentUser user, PlantsDbContext db,
            IMediaService media, CancellationToken ct) =>
        {
            if (req.PictureId is { } pictureId) await media.RequireOwnedAsync(pictureId, user.Id, MediaPurpose.PlantPicture, ct);
            var p = await db.Plants.FirstAsync(x => x.Id == plant.PlantId, ct);
            p.Update(req.Name, req.Description, req.PictureId, req.PushRequirement, req.TimeZoneId);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string JoinUrl(IConfiguration config, string code) =>
        $"{(config["App:PublicUrl"] ?? "").TrimEnd('/')}/join/{code}";

    /// <summary>Shared plumbing for admin decisions on a membership.</summary>
    internal sealed class Ctx(ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<PlantsDbContext> outbox, MembershipCache cache, TimeProvider clock)
    {
        public async Task<IResult> DecideAsync(Guid membershipId, Func<Membership, Guid, DateTimeOffset, IntegrationEvent> decide,
            CancellationToken ct, bool checkLastAdmin = false)
        {
            var db = outbox.DbContext;
            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.Id == membershipId && m.PlantId == plant.PlantId, ct)
                             ?? throw new NotFoundException("Membership", membershipId);
            if (checkLastAdmin && membership.Role == PlantRole.PlantAdmin)
                await PlantEndpoints.EnsureNotLastAdminAsync(db, plant.PlantId, membership.Id, ct);

            var evt = decide(membership, user.Id, clock.GetUtcNow());
            await outbox.PublishAsync(evt);
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await cache.InvalidateAsync(membership.PlantId, membership.UserId, ct);
            return Results.NoContent();
        }
    }
}
