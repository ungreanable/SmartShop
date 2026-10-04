using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using SmartShop.Infrastructure;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Infrastructure.Messaging;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Realtime;

namespace SmartShop.Bootstrap;

/// <summary>Composes all modules into a host. Used by the API, the worker and integration tests.</summary>
public static class SmartShopHost
{
    public static IReadOnlyList<IModule> Modules { get; } = ModuleCatalog.All();

    public static WebApplicationBuilder AddSmartShop(this WebApplicationBuilder builder, MessagingRole role)
    {
        builder.AddServiceDefaults();
        builder.AddSmartShopInfrastructure();

        foreach (var module in Modules)
            module.Register(builder);

        builder.AddSmartShopMessaging(role, Modules.Select(m => m.GetType().Assembly));

        if (role != MessagingRole.Api)
            builder.Services.AddHostedService<RecurringJobRunner>();

        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var authPermits = builder.Configuration.GetValue("RateLimiting:AuthPerMinute", 30);
        var userPermits = builder.Configuration.GetValue("RateLimiting:UserTokensPer10Seconds", 100);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Sign-in endpoints: per client IP.
            o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermits, Window = TimeSpan.FromMinutes(1) }));
            // Everything else: per authenticated user (or IP).
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetTokenBucketLimiter(
                ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = userPermits * 2,
                    TokensPerPeriod = userPermits,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                }));
        });
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
            if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));
        return builder;
    }

    public static WebApplication MapSmartShop(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        var api = app.MapGroup("/api");
        foreach (var module in Modules)
            module.MapEndpoints(api);

        app.MapHub<RealtimeHub>(RealtimeHub.Path);
        app.MapDefaultEndpoints();

        app.MapOpenApi();
        if (app.Environment.IsDevelopment())
            app.MapScalarApiReference(o => o.WithTitle("SmartShop API"));

        app.MapGet("/", () => Results.Redirect(app.Environment.IsDevelopment() ? "/scalar/v1" : "/health/live"))
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>Applies EF Core migrations of every module (each module owns its own schema and history table).</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");
        foreach (var module in Modules)
        {
            foreach (var contextType in module.DbContexts)
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
                logger.LogInformation("Migrating {Module} ({Context})", module.Name, contextType.Name);
                await context.Database.MigrateAsync(ct);
            }
        }
    }

    public static async Task RunSmartShopAsync(this WebApplication app, string[] args)
    {
        if (args.Contains("vapid", StringComparer.OrdinalIgnoreCase))
        {
            var (publicKey, privateKey) = SmartShop.Modules.Notifications.NotificationsModule.GenerateVapidKeys();
            Console.WriteLine($"VAPID_PUBLIC_KEY={publicKey}");
            Console.WriteLine($"VAPID_PRIVATE_KEY={privateKey}");
            return;
        }

        if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
        {
            await MigrateAsync(app.Services);
            return;
        }

        if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
            await MigrateAsync(app.Services);

        await app.RunAsync();
    }
}
