using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Infrastructure.Persistence;

/// <summary>
/// Base DbContext for a module. Every module owns exactly one PostgreSQL schema and maps Wolverine's
/// envelope tables so that integration events are written in the same transaction (transactional outbox).
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    public const string WolverineSchema = "wolverine";

    public abstract string Schema { get; }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureModel(modelBuilder);

        modelBuilder.MapWolverineEnvelopeStorage(WolverineSchema);
        // Wolverine creates and migrates its own tables; keep them out of EF migrations.
        foreach (var entity in modelBuilder.Model.GetEntityTypes()
                     .Where(e => e.ClrType.Namespace?.StartsWith("Wolverine", StringComparison.Ordinal) == true))
        {
            entity.SetIsTableExcludedFromMigrations(true);
        }
    }

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);
}
