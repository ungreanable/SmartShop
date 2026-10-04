using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Identity;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Identity.Data;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Identity.Endpoints;

public sealed record MeResponse(
    Guid Id, string DisplayName, string? PictureUrl, string? Phone, string Locale, bool IsSystemAdmin,
    string? ConsentVersion, DateTimeOffset? ConsentAt, IReadOnlyList<string> LinkedProviders);

public sealed record UpdateMeRequest(string DisplayName, string? Phone, string? Locale);
public sealed record ConsentRequest(string Version);

internal static class MeEndpoints
{
    public const string CurrentConsentVersion = "2026-10";

    public static void Map(IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Me").RequireAuthorization();

        me.MapGet("/", async (ICurrentUser current, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.Id, ct)
                       ?? throw new NotFoundException("User", current.Id);
            return ToResponse(user);
        });

        me.MapPut("/", async (UpdateMeRequest req, ICurrentUser current, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == current.Id, ct);
            user.UpdateProfile(req.DisplayName, req.Phone, req.Locale);
            await db.SaveChangesAsync(ct);
            return ToResponse(user);
        });

        me.MapGet("/consent", () => new { Version = CurrentConsentVersion }).AllowAnonymous();

        me.MapPost("/consent", async (ConsentRequest req, ICurrentUser current, IdentityDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == current.Id, ct);
            user.GiveConsent(req.Version, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return ToResponse(user);
        });

        // PDPA: right to data portability. Every module contributes its section.
        me.MapGet("/export", async (ICurrentUser current, IEnumerable<IPersonalDataContributor> contributors, TimeProvider clock, CancellationToken ct) =>
        {
            var export = new Dictionary<string, object?> { ["exportedAt"] = clock.GetUtcNow() };
            foreach (var contributor in contributors)
                export[contributor.Section] = await contributor.ExportAsync(current.Id, ct);
            return Results.Json(export, contentType: "application/json");
        });

        // PDPA: right to erasure. Personal data is anonymised; every module reacts to UserErased.
        me.MapDelete("/", async (ICurrentUser current, IDbContextOutbox<IdentityDbContext> outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var now = clock.GetUtcNow();
            var user = await db.Users.FirstAsync(u => u.Id == current.Id, ct);
            user.Erase(now);
            await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            await outbox.PublishAsync(new UserErased(user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        });
    }

    private static MeResponse ToResponse(Domain.User u) => new(
        u.Id, u.DisplayName, u.PictureUrl, u.Phone, u.Locale, u.IsSystemAdmin, u.ConsentVersion, u.ConsentAt,
        u.Logins.Select(l => l.Provider).ToList());
}
