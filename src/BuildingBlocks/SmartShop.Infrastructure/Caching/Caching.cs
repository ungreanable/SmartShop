using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

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
    public static string ShopMembers(Guid shopId) => $"shop:{shopId}:members";
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
            // One connection shared by the cache and modules that use Valkey directly (shop presence counters), instead of
            // a second socket per process. Connects on first use; AbortOnConnectFail=false keeps retrying in the background.
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redis);
                options.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddStackExchangeRedisCache(o => o.InstanceName = "smartshop:");
            services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((o, sp) =>
                o.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>()));
        }

        services.AddHybridCache(o =>
        {
            o.MaximumPayloadBytes = 4 * 1024 * 1024;
            o.DefaultEntryOptions = CacheKeys.Medium;
        });
        return services;
    }
}
