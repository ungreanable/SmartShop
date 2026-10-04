using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Plants;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Plants.Data;
using SmartShop.Modules.Plants.Domain;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Plants.Endpoints;

public sealed record AnnouncementRequest(string Title, string? Body, Guid? ImageId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool IsPinned, bool Notify);
public sealed record AnnouncementDto(Guid Id, string Title, string? Body, string? ImageUrl, Guid? ImageId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    bool IsPinned, DateTimeOffset CreatedAt);

internal static class AnnouncementEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/plant/announcements", async (ICurrentPlant plant, PlantsDbContext db, IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var items = await db.Announcements.AsNoTracking()
                .Where(a => a.PlantId == plant.PlantId && a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now))
                .OrderByDescending(a => a.IsPinned).ThenByDescending(a => a.StartsAt).Take(5).ToListAsync(ct);
            return items.Select(a => ToDto(a, media)).ToList();
        }).WithTags("Village").RequirePlantMember();

        var admin = api.MapGroup("/plant/admin/announcements").WithTags("Village admin").RequirePlantAdmin();

        admin.MapGet("/", async (ICurrentPlant plant, PlantsDbContext db, IMediaUrls media, CancellationToken ct) =>
            (await db.Announcements.AsNoTracking().Where(a => a.PlantId == plant.PlantId)
                .OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct)).Select(a => ToDto(a, media)).ToList());

        admin.MapPost("/", async (AnnouncementRequest req, ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<PlantsDbContext> outbox,
            IMediaService mediaService, IMediaUrls media, TimeProvider clock, CancellationToken ct) =>
        {
            if (req.ImageId is { } imageId) await mediaService.RequireOwnedAsync(imageId, user.Id, MediaPurpose.Announcement, ct);
            var now = clock.GetUtcNow();
            var announcement = Announcement.Create(plant.PlantId, req.Title, req.Body, req.ImageId, req.StartsAt ?? now, req.EndsAt, req.IsPinned, user.Id, now);
            outbox.DbContext.Announcements.Add(announcement);
            await outbox.PublishAsync(new AnnouncementPublished(plant.PlantId, announcement.Id, announcement.Title, req.Notify, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.Ok(ToDto(announcement, media));
        });

        admin.MapDelete("/{id:guid}", async (Guid id, ICurrentPlant plant, PlantsDbContext db, CancellationToken ct) =>
        {
            await db.Announcements.Where(a => a.Id == id && a.PlantId == plant.PlantId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }

    private static AnnouncementDto ToDto(Announcement a, IMediaUrls media) =>
        new(a.Id, a.Title, a.Body, media.For(a.ImageId), a.ImageId, a.StartsAt, a.EndsAt, a.IsPinned, a.CreatedAt);
}
