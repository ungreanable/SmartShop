using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Infrastructure.Persistence;

public static class PersistenceSetup
{
    public const string ConnectionStringName = "smartshop";

    public static string GetDatabaseConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

    public static IServiceCollection AddSmartShopDataSource(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(configuration.GetDatabaseConnectionString());
            builder.EnableDynamicJson();
            return builder.Build();
        });
        return services;
    }

    /// <summary>Registers a module DbContext against the shared data source, its own schema and Wolverine's outbox.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.AddDbContextWithWolverineIntegration<TContext>(
            (sp, options) =>
            {
                Configure(options, sp.GetRequiredService<NpgsqlDataSource>(), schema);
                if (sp.GetRequiredService<IConfiguration>().GetValue(RowLevelSecurity.ConfigKey, false))
                    options.AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
            },
            ModuleDbContext.WolverineSchema);
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource, string schema) =>
        options
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", schema))
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
