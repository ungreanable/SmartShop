using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Identity;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Identity.Data;
using SmartShop.Modules.Identity.Domain;

namespace SmartShop.Modules.Identity.Services;

internal sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, UserProfile>> GetProfilesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, UserProfile>();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new UserProfile(u.Id, u.DisplayName, u.PictureUrl, u.Phone))
            .ToDictionaryAsync(u => u.Id, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetLineUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await db.ExternalLogins.AsNoTracking()
            .Where(l => l.Provider == LoginProviders.Line && ids.Contains(l.UserId))
            .ToDictionaryAsync(l => l.UserId, l => l.ProviderKey, ct);
    }

    public async Task<IReadOnlyList<Guid>> SearchByNameAsync(string text, IEnumerable<Guid> withinUserIds, CancellationToken ct = default)
    {
        var ids = withinUserIds.Distinct().ToList();
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(text)) return [];
        var pattern = $"%{SmartShop.Infrastructure.Persistence.Like.Escape(text.Trim())}%";
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && EF.Functions.ILike(u.DisplayName, pattern))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<Guid?> FindByLineUserIdAsync(string lineUserId, CancellationToken ct = default) =>
        await db.ExternalLogins.AsNoTracking()
            .Where(l => l.Provider == LoginProviders.Line && l.ProviderKey == lineUserId)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(ct);
}

internal sealed class IdentityPersonalData(IdentityDbContext db) : IPersonalDataContributor
{
    public string Section => "profile";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        return new
        {
            user.Id,
            user.DisplayName,
            user.PictureUrl,
            user.Phone,
            user.Locale,
            user.ConsentVersion,
            user.ConsentAt,
            user.CreatedAt,
            user.LastLoginAt,
            Logins = user.Logins.Select(l => new { l.Provider, l.LinkedAt }),
        };
    }
}
