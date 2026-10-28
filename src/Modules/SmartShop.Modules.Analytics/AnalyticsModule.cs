using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Analytics.Data;
using SmartShop.Modules.Analytics.Endpoints;
using SmartShop.Modules.Analytics.Services;
using StackExchange.Redis;

namespace SmartShop.Modules.Analytics;

/// <summary>
/// Shop statistics: anonymous totals only (PDPA, owner's decision). Browsing events come from the client, order events
/// from integration events; a recurring job rolls both up per Bangkok day. Shops never see who did what.
/// </summary>
public sealed class AnalyticsModule : IModule
{
    public string Name => "Analytics";

    public IReadOnlyList<Type> DbContexts => [typeof(AnalyticsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<AnalyticsDbContext>(AnalyticsDbContext.SchemaName);
        services.AddSingleton<VisitorKeys>();
        services.AddSingleton<InMemoryPresence>();
        // Valkey when configured (several API replicas must agree on the count), otherwise this process's memory.
        services.AddSingleton<IPresenceTracker>(sp => sp.GetService<IConnectionMultiplexer>() is { } redis
            ? new ValkeyPresence(redis, sp.GetRequiredService<InMemoryPresence>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ValkeyPresence>>())
            : sp.GetRequiredService<InMemoryPresence>());
        services.AddScoped<ShopGate>();
        services.AddScoped<IRecurringJob, AnalyticsRollupJob>();
        services.AddScoped<IRecurringJob, AnalyticsRetentionJob>();

        // Heartbeats (2/min) plus batched events (every few seconds at most): a generous per-user budget that still stops
        // a script from flooding the raw table. Failures are invisible to the user (the client ignores them).
        var permits = builder.Configuration.GetValue("Analytics:RequestsPerMinute", 40);
        services.Configure<RateLimiterOptions>(o => o.AddPolicy(AnalyticsEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirst(SmartShopClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) })));
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => AnalyticsEndpoints.Map(app);
}
