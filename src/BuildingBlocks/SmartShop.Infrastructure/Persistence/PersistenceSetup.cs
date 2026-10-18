using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Infrastructure.Persistence;

public static class PersistenceSetup
{
    public const string ConnectionStringName = "smartshop";

    public static string GetDatabaseConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

    /// <summary>Pool cap used when the connection string does not set one (Npgsql's own default is 100).</summary>
    public const int DefaultMaxPoolSize = 20;

    /// <summary>
    /// Registers the single <see cref="NpgsqlDataSource"/> (one connection pool) shared by every module DbContext and by
    /// Wolverine's message store. Built eagerly so messaging setup can hand the same instance to Wolverine.
    /// </summary>
    public static IServiceCollection AddSmartShopDataSource(this IServiceCollection services, IConfiguration configuration)
    {
        if (!services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            services.AddSingleton(BuildDataSource(configuration));
        return services;
    }

    /// <summary>The shared data source registered by <see cref="AddSmartShopDataSource"/>.</summary>
    public static NpgsqlDataSource GetSmartShopDataSource(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSmartShopDataSource(configuration);
        return services.Last(d => d.ServiceType == typeof(NpgsqlDataSource)).ImplementationInstance as NpgsqlDataSource
               ?? throw new InvalidOperationException("The NpgsqlDataSource must be registered as an instance (AddSmartShopDataSource).");
    }

    private static NpgsqlDataSource BuildDataSource(IConfiguration configuration)
    {
        var connectionString = configuration.GetDatabaseConnectionString();
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // Small deployments run Postgres with max_connections=40; Npgsql's default of 100 per pool lets a burst exhaust
        // the server instead of queueing in the app. An explicit "Maximum Pool Size" in the connection string still wins.
        if (!Sets(connectionString, "MaximumPoolSize", "MaxPoolSize"))
            builder.ConnectionStringBuilder.MaxPoolSize = configuration.GetValue("Database:MaxPoolSize", DefaultMaxPoolSize);
        // Give idle connections back to the server sooner than the default 300 s (memory on a small box).
        if (!Sets(connectionString, "ConnectionIdleLifetime"))
            builder.ConnectionStringBuilder.ConnectionIdleLifetime = 60;
        builder.EnableDynamicJson();
        return builder.Build();
    }

    private static bool Sets(string connectionString, params string[] keywords) =>
        new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }.Keys.Cast<string>()
            .Any(k => keywords.Contains(k.Replace(" ", "", StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase));

    /// <summary>Registers a module DbContext against the shared data source, its own schema and Wolverine's outbox.</summary>
    /// <param name="npgsql">Extra provider options for this module (e.g. split queries for aggregates with several collections).</param>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.AddDbContextWithWolverineIntegration<TContext>(
            (sp, options) =>
            {
                Configure(options, sp.GetRequiredService<NpgsqlDataSource>(), schema, npgsql);
                if (sp.GetRequiredService<IConfiguration>().GetValue(RowLevelSecurity.ConfigKey, false))
                    options.AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
            },
            ModuleDbContext.WolverineSchema);
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource, string schema,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null) =>
        options
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations", schema);
                configure?.Invoke(npgsql);
            })
            .UseSnakeCaseNamingConvention();
}

/// <summary>Design-time factory used by <c>dotnet ef</c>; no database connection is needed to add migrations.</summary>
public abstract class ModuleDesignTimeFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : ModuleDbContext
{
    protected abstract string Schema { get; }

    protected abstract TContext Create(DbContextOptions<TContext> options);

    public TContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SMARTSHOP_DESIGN_DB")
                 ?? "Host=localhost;Database=smartshop;Username=postgres;Password=postgres";
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseNpgsql(cs, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", Schema))
            .UseSnakeCaseNamingConvention();
        return Create(builder.Options);
    }
}
