using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmartShop.Infrastructure.Tenancy;

namespace SmartShop.Infrastructure.Persistence;

/// <summary>
/// Optional PostgreSQL row-level security (<c>Database:RowLevelSecurity=true</c>) as defence in depth for tenant isolation.
/// Every table with a <c>plant_id</c> column gets a policy; during a request scoped to a village the connection switches to the
/// unprivileged <see cref="AppRole"/> and sets <see cref="PlantSetting"/>, so even a query that forgets its
/// <c>WHERE plant_id = ...</c> cannot read another village's rows. Background work (no village) sees everything as before.
/// </summary>
public static class RowLevelSecurity
{
    public const string ConfigKey = "Database:RowLevelSecurity";
    public const string AppRole = "smartshop_app";
    public const string PlantSetting = "smartshop.plant_id";
    public const string PolicyName = "tenant_isolation";

    /// <summary>Run by the migrator after EF migrations (as the owning user). Idempotent; disabling removes the policies again.</summary>
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, IReadOnlyCollection<string> schemas, bool enabled, ILogger logger, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var tables = new List<(string Schema, string Table, bool Enabled)>();
        await using (var find = new NpgsqlCommand("""
            SELECT c.table_schema, c.table_name, cl.relrowsecurity
            FROM information_schema.columns c
            JOIN pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_class cl ON cl.relnamespace = n.oid AND cl.relname = c.table_name AND cl.relkind = 'r'
            WHERE c.column_name = 'plant_id' AND c.table_schema = ANY(@schemas)
            ORDER BY 1, 2
            """, connection))
        {
            find.Parameters.AddWithValue("schemas", schemas.ToArray());
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        if (!enabled)
        {
            foreach (var (schema, table, _) in tables.Where(t => t.Enabled))
                await ExecAsync(connection, $"""
                    DROP POLICY IF EXISTS {PolicyName} ON "{schema}"."{table}";
                    ALTER TABLE "{schema}"."{table}" NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE "{schema}"."{table}" DISABLE ROW LEVEL SECURITY;
                    """, ct);
            return;
        }

        await ExecAsync(connection, $"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN CREATE ROLE {AppRole} NOLOGIN; END IF;
            END $$;
            GRANT {AppRole} TO CURRENT_USER;
            """, ct);

        foreach (var schema in schemas.Append(ModuleDbContext.WolverineSchema).Distinct())
        {
            await ExecAsync(connection, $"""
                CREATE SCHEMA IF NOT EXISTS "{schema}";
                GRANT USAGE ON SCHEMA "{schema}" TO {AppRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "{schema}" TO {AppRole};
                GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA "{schema}" TO {AppRole};
                ALTER DEFAULT PRIVILEGES IN SCHEMA "{schema}" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppRole};
                ALTER DEFAULT PRIVILEGES IN SCHEMA "{schema}" GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {AppRole};
                """, ct);
        }

        // Rows of the "system" plant (Guid.Empty) stay visible everywhere.
        foreach (var (schema, table, _) in tables)
            await ExecAsync(connection, $"""
                ALTER TABLE "{schema}"."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{schema}"."{table}" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS {PolicyName} ON "{schema}"."{table}";
                CREATE POLICY {PolicyName} ON "{schema}"."{table}"
                  USING (coalesce(current_setting('{PlantSetting}', true), '') = ''
                         OR plant_id = current_setting('{PlantSetting}', true)::uuid
                         OR plant_id = '00000000-0000-0000-0000-000000000000');
                """, ct);

        logger.LogInformation("Row-level security enabled on {Count} tables", tables.Count);
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Applies the request's village to every connection EF opens (see <see cref="RowLevelSecurity"/>).</summary>
public sealed class TenantConnectionInterceptor(IHttpContextAccessor accessor) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        // Only a verified membership counts (RequireMemberAsync ran); otherwise the request is not village scoped.
        var plantId = accessor.HttpContext?.RequestServices.GetService<ICurrentPlant>()?.ResolvedPlantId;
        var command = connection.CreateCommand();
        if (plantId is { } id)
        {
            command.CommandText = $"SET ROLE {RowLevelSecurity.AppRole}; SELECT set_config('{RowLevelSecurity.PlantSetting}', @plant, false)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "plant";
            parameter.Value = id.ToString();
            command.Parameters.Add(parameter);
        }
        else
        {
            // Pooled connections may carry a previous request's role/setting: always reset.
            command.CommandText = $"RESET ROLE; SELECT set_config('{RowLevelSecurity.PlantSetting}', '', false)";
        }
        return command;
    }
}
