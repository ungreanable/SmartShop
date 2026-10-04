using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using SmartShop.Contracts.Media;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Storage;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Media.Data;
using SmartShop.Modules.Media.Services;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Media;

public sealed record UploadResult(Guid Id, string ContentType, int? Width, int? Height, string? Url, string? ThumbnailUrl);

public sealed class MediaModule : IModule
{
    public string Name => "Media";

    public IReadOnlyList<Type> DbContexts => [typeof(MediaDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<MediaDbContext>(MediaDbContext.SchemaName);
        builder.Services.AddScoped<MediaService>();
        builder.Services.AddScoped<IMediaService>(sp => sp.GetRequiredService<MediaService>());
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var media = app.MapGroup("/media").WithTags("Media");

        media.MapPost("/", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] MediaPurpose purpose, HttpContext http,
            ICurrentUser user, MediaService service, IMediaUrls urls, CancellationToken ct) =>
        {
            Guid? plantId = Guid.TryParse(http.Request.Headers[ICurrentPlant.Header], out var p) ? p : null;
            if (plantId is not null) await http.RequestServices.GetRequiredService<ICurrentPlant>().RequireMemberAsync(ct);
            else if (purpose != MediaPurpose.PlantPicture)
                throw new DomainException("plant_required", $"Header {ICurrentPlant.Header} is required for this upload.");

            await using var stream = file.OpenReadStream();
            var stored = await service.UploadAsync(user.Id, plantId, purpose, stream, ct);
            return Results.Ok(new UploadResult(stored.Id, stored.ContentType, stored.Width, stored.Height,
                urls.For(stored.Id), stored.HasVariants ? urls.For(stored.Id, MediaVariant.Small) : null));
        })
        .RequireAuthorization()
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttributeShim(20 * 1024 * 1024));

        // Anonymous on purpose: <img> tags cannot send tokens. The HMAC signature proves the URL was issued
        // to an authorised viewer and expires within ~2 hours.
        media.MapGet("/{id:guid}/{variant}", async (Guid id, string variant, long exp, string sig, HttpContext http,
            IMediaUrls urls, MediaDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            var parsed = MediaUrls.Parse(variant);
            if (parsed is null || !urls.Verify(id, parsed.Value, exp, sig)) return Results.NotFound();

            var obj = await db.Objects.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (obj is null) return Results.NotFound();

            var key = parsed == MediaVariant.Original || !obj.HasVariants
                ? obj.OriginalKey
                : obj.VariantKey(MediaUrls.Name(parsed.Value));
            var stored = await storage.GetAsync(key, ct);
            if (stored is null) return Results.NotFound();

            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600, immutable";
            http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            return Results.Stream(stored.Content, stored.ContentType, enableRangeProcessing: false);
        }).AllowAnonymous().ExcludeFromDescription();
    }
}

/// <summary>Per-endpoint body size limit for minimal APIs.</summary>
internal sealed class RequestSizeLimitAttributeShim(long bytes) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => bytes;
}
