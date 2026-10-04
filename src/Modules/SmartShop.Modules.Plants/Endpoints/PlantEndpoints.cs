using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Plants;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Plants.Data;
using SmartShop.Modules.Plants.Domain;
using SmartShop.Modules.Plants.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Plants.Endpoints;

public sealed record CreatePlantRequest(string Name, string? TimeZoneId, Guid? AdminUserId);
public sealed record PlantSummary(Guid Id, string Name, string JoinCode, string TimeZoneId, int ActiveMembers, int PendingMembers, DateTimeOffset CreatedAt);
public sealed record AppointAdminRequest(Guid UserId);
public sealed record JoinPreview(Guid PlantId, string Name, string? PictureUrl, string? Description);
public sealed record JoinRequest(string JoinCode, string HouseNo, string? Soi, string? Nickname, string? Message);
public sealed record MyMembership(Guid PlantId, string PlantName, string? PictureUrl, PlantRole Role, MembershipStatus Status, string? HouseNo, string? Soi, string? Nickname, string? DecisionReason);
public sealed record PlantDetails(Guid Id, string Name, string? Description, string? PictureUrl, Guid? PictureId, string TimeZoneId, PushRequirement PushRequirement, MyMembership Me);
public sealed record UpdateAddressRequest(string HouseNo, string? Soi, string? Nickname);

internal static class PlantEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        MapSystemAdmin(api);
        MapUser(api);
    }

    private static void MapSystemAdmin(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin/plants").WithTags("System admin").RequireAuthorization(Policies.SystemAdmin);

        admin.MapGet("/", async (PlantsDbContext db, CancellationToken ct) =>
            await db.Plants.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new PlantSummary(p.Id, p.Name, p.JoinCode, p.TimeZoneId,
                    db.Memberships.Count(m => m.PlantId == p.Id && m.Status == MembershipStatus.Active),
                    db.Memberships.Count(m => m.PlantId == p.Id && m.Status == MembershipStatus.Pending),
                    p.CreatedAt))
                .ToListAsync(ct));

        // The creator (or the appointed user) becomes the first plant administrator.
        admin.MapPost("/", async (CreatePlantRequest req, ICurrentUser user, IDbContextOutbox<PlantsDbContext> outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var plant = Plant.Create(req.Name, req.TimeZoneId, user.Id, now);
            var adminId = req.AdminUserId ?? user.Id;
            outbox.DbContext.Plants.Add(plant);
            outbox.DbContext.Memberships.Add(Membership.CreateAdmin(plant.Id, adminId, user.Id, now));
            await outbox.PublishAsync(new PlantCreated(plant.Id, plant.Name));
            await outbox.PublishAsync(new MembershipRoleChanged(plant.Id, adminId, PlantRole.PlantAdmin, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.Created($"/api/admin/plants/{plant.Id}", new PlantSummary(plant.Id, plant.Name, plant.JoinCode, plant.TimeZoneId, 1, 0, plant.CreatedAt));
        });

        admin.MapPost("/{plantId:guid}/admins", async (Guid plantId, AppointAdminRequest req, ICurrentUser user,
            IDbContextOutbox<PlantsDbContext> outbox, MembershipCache cache, TimeProvider clock, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            _ = await db.Plants.FindAsync([plantId], ct) ?? throw new NotFoundException("Plant", plantId);
            var now = clock.GetUtcNow();
            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.PlantId == plantId && m.UserId == req.UserId, ct);
            if (membership is null) db.Memberships.Add(Membership.CreateAdmin(plantId, req.UserId, user.Id, now));
            else membership.PromoteToAdmin(user.Id, now);
            await outbox.PublishAsync(new MembershipRoleChanged(plantId, req.UserId, PlantRole.PlantAdmin, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await cache.InvalidateAsync(plantId, req.UserId, ct);
            return Results.NoContent();
        });
    }

    private static void MapUser(IEndpointRouteBuilder api)
    {
        var plants = api.MapGroup("/plants").WithTags("Villages").RequireAuthorization();

        // Shows only the village name so people know what they are asking to join.
        plants.MapGet("/join/{joinCode}", async (string joinCode, PlantsDbContext db, IMediaUrls media, CancellationToken ct) =>
        {
            var code = joinCode.Trim().ToUpperInvariant();
            var plant = await db.Plants.AsNoTracking().FirstOrDefaultAsync(p => p.JoinCode == code, ct)
                        ?? throw new NotFoundException("Village", joinCode);
            return new JoinPreview(plant.Id, plant.Name, media.For(plant.PictureId), plant.Description);
        });

        plants.MapPost("/join", async (JoinRequest req, ICurrentUser user, IDbContextOutbox<PlantsDbContext> outbox,
            MembershipCache cache, TimeProvider clock, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var code = Guard.NotEmpty(req.JoinCode, "Join code", 16).ToUpperInvariant();
            var plant = await db.Plants.FirstOrDefaultAsync(p => p.JoinCode == code, ct)
                        ?? throw new DomainException("join_code_invalid", "This invitation link is invalid or has been reset.");

            var now = clock.GetUtcNow();
            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.PlantId == plant.Id && m.UserId == user.Id, ct);
            if (membership is null)
            {
                membership = Membership.Request(plant.Id, user.Id, req.HouseNo, req.Soi, req.Nickname, req.Message, now);
                db.Memberships.Add(membership);
            }
            else
            {
                membership.RequestAgain(req.HouseNo, req.Soi, req.Nickname, req.Message, now);
            }

            await outbox.PublishAsync(new MembershipRequested(plant.Id, membership.Id, user.Id, membership.Nickname, membership.HouseNo));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await cache.InvalidateAsync(plant.Id, user.Id, ct);
            return Results.Accepted(value: ToMine(membership, plant, null));
        });

        plants.MapPost("/{plantId:guid}/leave", async (Guid plantId, ICurrentUser user, IDbContextOutbox<PlantsDbContext> outbox,
            MembershipCache cache, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var membership = await db.Memberships.FirstOrDefaultAsync(m => m.PlantId == plantId && m.UserId == user.Id, ct)
                             ?? throw new NotFoundException("Membership", plantId);
            if (membership.Role == PlantRole.PlantAdmin)
                await EnsureNotLastAdminAsync(db, plantId, membership.Id, ct);
            membership.Leave();
            await outbox.PublishAsync(new MembershipLeft(plantId, membership.Id, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await cache.InvalidateAsync(plantId, user.Id, ct);
            return Results.NoContent();
        });

        api.MapGet("/me/memberships", async (ICurrentUser user, PlantsDbContext db, IMediaUrls media, CancellationToken ct) =>
        {
            var rows = await (from m in db.Memberships.AsNoTracking()
                              join p in db.Plants.AsNoTracking() on m.PlantId equals p.Id
                              where m.UserId == user.Id && m.Status != MembershipStatus.Left
                              orderby p.Name
                              select new { m, p }).ToListAsync(ct);
            return rows.Select(r => ToMine(r.m, r.p, media)).ToList();
        }).WithTags("Villages").RequireAuthorization();

        // ---- inside a plant (X-Plant-Id) ----
        var plant = api.MapGroup("/plant").WithTags("Village").RequirePlantMember();

        plant.MapGet("/", async (ICurrentPlant current, PlantsDbContext db, IMediaUrls media, CancellationToken ct) =>
        {
            var p = await db.Plants.AsNoTracking().FirstAsync(x => x.Id == current.PlantId, ct);
            var m = await db.Memberships.AsNoTracking().FirstAsync(x => x.Id == current.Membership.MembershipId, ct);
            return new PlantDetails(p.Id, p.Name, p.Description, media.For(p.PictureId), p.PictureId, p.TimeZoneId, p.PushRequirement, ToMine(m, p, media));
        });

        plant.MapPut("/me/address", async (UpdateAddressRequest req, ICurrentPlant current, PlantsDbContext db,
            MembershipCache cache, CancellationToken ct) =>
        {
            var m = await db.Memberships.FirstAsync(x => x.Id == current.Membership.MembershipId, ct);
            m.UpdateAddress(req.HouseNo, req.Soi, req.Nickname);
            await db.SaveChangesAsync(ct);
            await cache.InvalidateAsync(m.PlantId, m.UserId, ct);
            return Results.NoContent();
        });
    }

    internal static async Task EnsureNotLastAdminAsync(PlantsDbContext db, Guid plantId, Guid membershipId, CancellationToken ct)
    {
        var otherAdmins = await db.Memberships.CountAsync(m => m.PlantId == plantId && m.Id != membershipId
            && m.Role == PlantRole.PlantAdmin && m.Status == MembershipStatus.Active, ct);
        if (otherAdmins == 0)
            throw new DomainException("last_admin", "A village needs at least one administrator. Appoint another admin first.");
    }

    internal static MyMembership ToMine(Membership m, Plant p, IMediaUrls? media) =>
        new(p.Id, p.Name, media?.For(p.PictureId), m.Role, m.Status, m.HouseNo, m.Soi, m.Nickname, m.DecisionReason);
}
