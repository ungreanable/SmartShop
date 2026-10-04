using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Caching;
using SmartShop.SharedKernel;

namespace SmartShop.Infrastructure.Tenancy;

/// <summary>
/// The plant (village) the request is scoped to, taken from the <c>X-Plant-Id</c> header and verified against
/// the caller's membership. Only <c>Active</c> members can see anything inside a plant.
/// </summary>
public interface ICurrentPlant
{
    const string Header = "X-Plant-Id";

    /// <summary>Available after <see cref="RequireMemberAsync"/> succeeded (the endpoint filters call it).</summary>
    Guid PlantId { get; }

    MembershipInfo Membership { get; }

    /// <summary>The verified plant, or null when the request is not (yet) scoped to a village.</summary>
    Guid? ResolvedPlantId { get; }

    Task<MembershipInfo> RequireMemberAsync(CancellationToken ct = default);

    Task<MembershipInfo> RequireAdminAsync(CancellationToken ct = default);
}

internal sealed class CurrentPlant(
    IHttpContextAccessor accessor,
    ICurrentUser user,
    IPlantDirectory plants,
    HybridCache cache) : ICurrentPlant
{
    private MembershipInfo? _membership;

    public Guid PlantId => Membership.PlantId;

    public Guid? ResolvedPlantId => _membership?.PlantId;

    public MembershipInfo Membership =>
        _membership ?? throw new InvalidOperationException("Plant membership has not been resolved for this request.");

    public async Task<MembershipInfo> RequireMemberAsync(CancellationToken ct = default)
    {
        if (_membership is not null) return _membership;

        var header = accessor.HttpContext?.Request.Headers[ICurrentPlant.Header].ToString();
        if (!Guid.TryParse(header, out var plantId))
            throw new DomainException("plant_required", $"Header {ICurrentPlant.Header} is required.");

        var userId = user.Id;
        var membership = await cache.GetOrCreateAsync(
            CacheKeys.Membership(plantId, userId),
            async token => await plants.GetMembershipAsync(plantId, userId, token),
            CacheKeys.Short,
            cancellationToken: ct);

        if (membership is not { Status: MembershipStatus.Active })
            throw new ForbiddenException("You are not an active member of this village.");

        return _membership = membership;
    }

    public async Task<MembershipInfo> RequireAdminAsync(CancellationToken ct = default)
    {
        var membership = await RequireMemberAsync(ct);
        if (membership.Role != PlantRole.PlantAdmin && !user.IsSystemAdmin)
            throw new ForbiddenException("Only village administrators can do this.");
        return membership;
    }
}

/// <summary>Checks the caller's role in a shop. Shop roles are cached briefly and invalidated on member changes.</summary>
public interface IShopAccess
{
    Task<ShopRole> RequireAsync(Guid shopId, ShopRole minimum, CancellationToken ct = default);

    Task<ShopRole?> GetRoleAsync(Guid shopId, CancellationToken ct = default);
}

internal sealed class ShopAccess(ICurrentUser user, IShopDirectory shops, HybridCache cache) : IShopAccess
{
    public async Task<ShopRole?> GetRoleAsync(Guid shopId, CancellationToken ct = default)
    {
        var userId = user.Id;
        var role = await cache.GetOrCreateAsync(
            CacheKeys.ShopRole(shopId, userId),
            async token => (int?)await shops.GetRoleAsync(shopId, userId, token),
            CacheKeys.Short,
            cancellationToken: ct);
        return role is { } r ? (ShopRole)r : null;
    }

    public async Task<ShopRole> RequireAsync(Guid shopId, ShopRole minimum, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(shopId, ct);
        if (role is null || role < minimum)
            throw new ForbiddenException("You do not have the required role in this shop.");
        return role.Value;
    }
}

public static class TenancySetup
{
    public static IServiceCollection AddSmartShopTenancy(this IServiceCollection services)
    {
        services.AddScoped<ICurrentPlant, CurrentPlant>();
        services.AddScoped<IShopAccess, ShopAccess>();
        return services;
    }

    /// <summary>All endpoints in the group require an authenticated, active member of the plant in <c>X-Plant-Id</c>.</summary>
    public static TBuilder RequirePlantMember<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            await ctx.HttpContext.RequestServices.GetRequiredService<ICurrentPlant>()
                .RequireMemberAsync(ctx.HttpContext.RequestAborted);
            return await next(ctx);
        });
        return builder;
    }

    public static TBuilder RequirePlantAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            await ctx.HttpContext.RequestServices.GetRequiredService<ICurrentPlant>()
                .RequireAdminAsync(ctx.HttpContext.RequestAborted);
            return await next(ctx);
        });
        return builder;
    }
}
