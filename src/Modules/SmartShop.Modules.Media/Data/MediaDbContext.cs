using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Media.Domain;

namespace SmartShop.Modules.Media.Data;

public sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "media";

    public override string Schema => SchemaName;

    public DbSet<MediaObject> Objects => Set<MediaObject>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<MediaObject>(e =>
        {
            e.ToTable("objects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Ignore(x => x.OriginalKey);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Sha256);
        });
    }
}

internal sealed class MediaDesignTimeFactory : ModuleDesignTimeFactory<MediaDbContext>
{
    protected override string Schema => MediaDbContext.SchemaName;
    protected override MediaDbContext Create(DbContextOptions<MediaDbContext> options) => new(options);
}
