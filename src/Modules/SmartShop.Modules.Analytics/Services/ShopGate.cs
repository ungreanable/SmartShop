using Microsoft.Extensions.Caching.Hybrid;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Analytics.Services;

/// <summary>Checks done on every analytics request, cached so a heartbeat costs no database round trip.</summary>
internal sealed class ShopGate(IShopDirectory shops, IShopAccess access, ICurrentPlant plant, HybridCache cache)
{
    // A shop never moves to another village, so its village can be cached for long.
    private static readonly HybridCacheEntryOptions ShopPlant = new()
    {
        Expiration = TimeSpan.FromHours(6),
        LocalCacheExpiration = TimeSpan.FromMinutes(30),
    };

    /// <summary>Shops of other villages are "not found", exactly like the rest of the API.</summary>
    public async Task RequireInPlantAsync(Guid shopId, CancellationToken ct)
    {
        if (!await IsInPlantAsync(shopId, ct)) throw new NotFoundException("Shop", shopId);
    }

    public async Task<bool> IsInPlantAsync(Guid shopId, CancellationToken ct)
    {
        var key = $"analytics:shop-plant:{shopId}";
        var plantId = await cache.GetOrCreateAsync(key,
            async token => (await shops.GetListingSnapshotAsync(shopId, token))?.PlantId ?? Guid.Empty, ShopPlant, cancellationToken: ct);
        if (plantId == plant.PlantId) return true;
        // Never keep a miss: with row-level security a lookup from another village sees nothing, which must not block
        // the shop's own villagers for hours.
        if (plantId == Guid.Empty) await cache.RemoveAsync(key, ct);
        return false;
    }

    /// <summary>The shop's own people looking at their shop are not customers (role is cached by <see cref="IShopAccess"/>).</summary>
    public async Task<bool> IsMemberAsync(Guid shopId, CancellationToken ct) => await access.GetRoleAsync(shopId, ct) is not null;
}
