using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Plants.Domain;

namespace SmartShop.Modules.Plants.Data;

public sealed class PlantsDbContext(DbContextOptions<PlantsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "plants";

    public override string Schema => SchemaName;

    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Announcement> Announcements => Set<Announcement>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<Plant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.JoinCode).HasMaxLength(16);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.PushRequirement).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.JoinCode).IsUnique();
        });

        b.Entity<Membership>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.HouseNo).HasMaxLength(30);
            e.Property(x => x.Soi).HasMaxLength(60);
            e.Property(x => x.Nickname).HasMaxLength(60);
            e.Property(x => x.RequestMessage).HasMaxLength(500);
            e.Property(x => x.DecisionReason).HasMaxLength(500);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.PlantId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.PlantId, x.Status });
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Announcement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.HasIndex(x => new { x.PlantId, x.StartsAt });
        });
    }
}

internal sealed class PlantsDesignTimeFactory : ModuleDesignTimeFactory<PlantsDbContext>
{
    protected override string Schema => PlantsDbContext.SchemaName;
    protected override PlantsDbContext Create(DbContextOptions<PlantsDbContext> options) => new(options);
}
