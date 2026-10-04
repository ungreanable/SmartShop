using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Plants;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Plants.Data;

namespace SmartShop.Modules.Plants.Services;

internal sealed class PlantDirectory(PlantsDbContext db) : IPlantDirectory
{
    public async Task<MembershipInfo?> GetMembershipAsync(Guid plantId, Guid userId, CancellationToken ct = default)
    {
        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.PlantId == plantId && m.UserId == userId, ct);
        return membership?.ToInfo();
    }

    public Task<PlantInfo?> GetPlantAsync(Guid plantId, CancellationToken ct = default) =>
        db.Plants.AsNoTracking()
            .Where(p => p.Id == plantId)
            .Select(p => new PlantInfo(p.Id, p.Name, p.TimeZoneId, p.PushRequirement))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetActiveAdminIdsAsync(Guid plantId, CancellationToken ct = default) =>
        await db.Memberships.AsNoTracking()
            .Where(m => m.PlantId == plantId && m.Role == PlantRole.PlantAdmin && m.Status == MembershipStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetActiveMemberIdsAsync(Guid plantId, CancellationToken ct = default) =>
        await db.Memberships.AsNoTracking()
            .Where(m => m.PlantId == plantId && m.Status == MembershipStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(ct);
}

/// <summary>Removes cached memberships so permission changes apply within seconds.</summary>
internal sealed class MembershipCache(HybridCache cache)
{
    public ValueTask InvalidateAsync(Guid plantId, Guid userId, CancellationToken ct = default) =>
        cache.RemoveAsync(CacheKeys.Membership(plantId, userId), ct);
}

internal sealed class PlantsPersonalData(PlantsDbContext db) : IPersonalDataContributor
{
    public string Section => "villages";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct = default) =>
        await (from m in db.Memberships.AsNoTracking()
               join p in db.Plants.AsNoTracking() on m.PlantId equals p.Id
               where m.UserId == userId
               select new { Village = p.Name, m.Role, m.Status, m.HouseNo, m.Soi, m.Nickname, m.RequestedAt })
            .ToListAsync(ct);
}
