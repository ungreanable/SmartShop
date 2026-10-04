using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmartShop.Infrastructure.Caching;

public static class CacheKeys
{
    public static readonly HybridCacheEntryOptions Short = new()
    {
        Expiration = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public static readonly HybridCacheEntryOptions Medium = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public static string Membership(Guid plantId, Guid userId) => $"membership:{plantId}:{userId}";
    public static string ShopRole(Guid shopId, Guid userId) => $"shoprole:{shopId}:{userId}";
    public static string ShopProfile(Guid shopId) => $"shop:{shopId}:profile";
    public static string ShopMenu(Guid shopId) => $"shop:{shopId}:menu";
    public static string ShopRecipients(Guid shopId) => $"shop:{shopId}:notify-recipients";
    public static string PlantFeed(Guid plantId) => $"plant:{plantId}:feed";
    public static string UserChannels(Guid userId) => $"user:{userId}:channels";
}

public static class CachingSetup
{
    /// <summary>
    /// HybridCache: L1 in-process memory + L2 Valkey (when the <c>cache</c> connection string is set).
    /// L1 entries are kept short (30s) because invalidations from other processes only reach L2.
    /// </summary>
    public static IServiceCollection AddSmartShopCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var redis = configuration.GetConnectionString("cache");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "smartshop:";
            });
        }

        services.AddHybridCache(o =>
        {
            o.MaximumPayloadBytes = 4 * 1024 * 1024;
            o.DefaultEntryOptions = CacheKeys.Medium;
        });
        return services;
    }
}
