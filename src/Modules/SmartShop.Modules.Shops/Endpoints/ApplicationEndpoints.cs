using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;
using SmartShop.Modules.Shops.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Shops.Endpoints;

public sealed record ApplicationRequest(string Name, string? Category, string? Description, string? HouseNo, string? Phone, List<Guid>? SampleImageIds);

public sealed record ApplicationDto(
    Guid Id, string Name, string? Category, string? Description, string? HouseNo, string? Phone,
    IReadOnlyList<Guid> SampleImageIds, IReadOnlyList<string?> SampleImageUrls,
    ApplicationStatus Status, string? ReviewNote, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt, Guid? ShopId,
    Guid ApplicantId, string? ApplicantName, string? ApplicantPictureUrl);

public sealed record ReviewNoteRequest(string? Note);

internal static class ApplicationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var member = api.MapGroup("/shop-applications").WithTags("Shop applications").RequirePlantMember();

        member.MapPost("/", async (ApplicationRequest req, ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<ShopsDbContext> outbox,
            IMediaService media, IMediaUrls urls, TimeProvider clock, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            if (await db.Applications.AnyAsync(a => a.PlantId == plant.PlantId && a.ApplicantId == user.Id
                    && (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.ChangesRequested), ct))
                throw new ConflictException("application_pending", "You already have an application waiting for review.");

            var details = await ValidateAsync(req, user.Id, media, ct);
            var application = ShopApplication.Submit(plant.PlantId, user.Id, details, clock.GetUtcNow());
            db.Applications.Add(application);
            await outbox.PublishAsync(new ShopApplicationSubmitted(plant.PlantId, application.Id, user.Id, application.Name));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.Created($"/api/shop-applications/{application.Id}", ToDto(application, urls, null));
        });

        member.MapGet("/mine", async (ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, IMediaUrls urls, CancellationToken ct) =>
        {
            var apps = await db.Applications.AsNoTracking()
                .Where(a => a.PlantId == plant.PlantId && a.ApplicantId == user.Id)
                .OrderByDescending(a => a.SubmittedAt).ToListAsync(ct);
            return apps.Select(a => ToDto(a, urls, null)).ToList();
        });

        member.MapPut("/{id:guid}", async (Guid id, ApplicationRequest req, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ShopsDbContext> outbox, IMediaService media, IMediaUrls urls, TimeProvider clock, CancellationToken ct) =>
        {
            var application = await outbox.DbContext.Applications
                .FirstOrDefaultAsync(a => a.Id == id && a.PlantId == plant.PlantId && a.ApplicantId == user.Id, ct)
                ?? throw new NotFoundException("Application", id);
            application.Resubmit(await ValidateAsync(req, user.Id, media, ct, application.SampleImageIds), clock.GetUtcNow());
            await outbox.PublishAsync(new ShopApplicationSubmitted(plant.PlantId, application.Id, user.Id, application.Name));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return ToDto(application, urls, null);
        });

        member.MapPost("/{id:guid}/withdraw", async (Guid id, ICurrentPlant plant, ICurrentUser user, ShopsDbContext db, CancellationToken ct) =>
        {
            var application = await db.Applications
                .FirstOrDefaultAsync(a => a.Id == id && a.PlantId == plant.PlantId && a.ApplicantId == user.Id, ct)
                ?? throw new NotFoundException("Application", id);
            application.Withdraw();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- review by plant administrators ----
        var admin = api.MapGroup("/plant/admin/shop-applications").WithTags("Village admin").RequirePlantAdmin();

        admin.MapGet("/", async (ApplicationStatus? status, ICurrentPlant plant, ShopsDbContext db, IUserDirectory users, IMediaUrls urls, CancellationToken ct) =>
        {
            var query = db.Applications.AsNoTracking().Where(a => a.PlantId == plant.PlantId);
            query = status is { } s ? query.Where(a => a.Status == s) : query;
            var apps = await query.OrderBy(a => a.Status == ApplicationStatus.Submitted ? 0 : 1).ThenByDescending(a => a.SubmittedAt)
                .Take(200).ToListAsync(ct);
            var profiles = await users.GetProfilesAsync(apps.Select(a => a.ApplicantId), ct);
            return apps.Select(a => ToDto(a, urls, profiles.GetValueOrDefault(a.ApplicantId))).ToList();
        });

        admin.MapPost("/{id:guid}/approve", async (Guid id, ReviewNoteRequest req, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ShopsDbContext> outbox, IPlantDirectory plants, TimeProvider clock, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == id && a.PlantId == plant.PlantId, ct)
                              ?? throw new NotFoundException("Application", id);
            var plantInfo = await plants.GetPlantAsync(plant.PlantId, ct) ?? throw new NotFoundException("Plant", plant.PlantId);
            var now = clock.GetUtcNow();

            var code = ShopMapper.CodeFor(await db.Shops.CountAsync(s => s.PlantId == plant.PlantId, ct));
            var shop = Shop.Create(plant.PlantId, code, plantInfo.TimeZoneId, application.ApplicantId, application, now);
            application.Approve(user.Id, shop.Id, req.Note, now);
            db.Shops.Add(shop);
            db.StatusTrackers.Add(new ShopStatusTracker(shop.Id));

            await outbox.PublishAsync(new ShopApplicationApproved(plant.PlantId, application.Id, application.ApplicantId, shop.Id, shop.Name));
            await outbox.PublishAsync(new ShopCreated(plant.PlantId, shop.Id, application.ApplicantId, shop.Name));
            await outbox.PublishAsync(new EvaluateShopStatus(shop.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.Ok(new { shopId = shop.Id, shop.Code });
        });

        admin.MapPost("/{id:guid}/request-changes", (Guid id, ReviewNoteRequest req, ReviewCtx ctx, CancellationToken ct) =>
            ctx.ReviewAsync(id, (a, actor, now) =>
            {
                a.RequestChanges(actor, req.Note ?? "", now);
                return new ShopApplicationChangesRequested(a.PlantId, a.Id, a.ApplicantId, a.Name, a.ReviewNote!);
            }, ct));

        admin.MapPost("/{id:guid}/reject", (Guid id, ReviewNoteRequest req, ReviewCtx ctx, CancellationToken ct) =>
            ctx.ReviewAsync(id, (a, actor, now) =>
            {
                a.Reject(actor, req.Note ?? "", now);
                return new ShopApplicationRejected(a.PlantId, a.Id, a.ApplicantId, a.Name, a.ReviewNote!);
            }, ct));
    }

    private static async Task<ApplicationDetails> ValidateAsync(ApplicationRequest req, Guid userId, IMediaService media, CancellationToken ct,
        IReadOnlyCollection<Guid>? alreadyAttached = null)
    {
        var images = req.SampleImageIds ?? [];
        foreach (var imageId in images.Where(i => alreadyAttached?.Contains(i) != true))
            await media.RequireOwnedAsync(imageId, userId, MediaPurpose.ApplicationSample, ct);
        return new ApplicationDetails(req.Name, req.Category, req.Description, req.HouseNo, req.Phone, images);
    }

    internal static ApplicationDto ToDto(ShopApplication a, IMediaUrls urls, UserProfile? applicant) => new(
        a.Id, a.Name, a.Category, a.Description, a.HouseNo, a.Phone, a.SampleImageIds,
        a.SampleImageIds.Select(i => urls.For(i, MediaVariant.Small)).ToList(),
        a.Status, a.ReviewNote, a.SubmittedAt, a.ReviewedAt, a.ShopId,
        a.ApplicantId, applicant?.DisplayName, applicant?.PictureUrl);

    internal sealed class ReviewCtx(ICurrentPlant plant, ICurrentUser user, IDbContextOutbox<ShopsDbContext> outbox, TimeProvider clock)
    {
        public async Task<IResult> ReviewAsync(Guid id, Func<ShopApplication, Guid, DateTimeOffset, IntegrationEvent> review, CancellationToken ct)
        {
            var application = await outbox.DbContext.Applications.FirstOrDefaultAsync(a => a.Id == id && a.PlantId == plant.PlantId, ct)
                              ?? throw new NotFoundException("Application", id);
            await outbox.PublishAsync(review(application, user.Id, clock.GetUtcNow()));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        }
    }
}
