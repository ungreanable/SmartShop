using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Identity.Domain;

namespace SmartShop.Modules.Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PhoneOtp> PhoneOtps => Set<PhoneOtp>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.PictureUrl).HasMaxLength(500);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Locale).HasMaxLength(5);
            e.Property(x => x.ConsentVersion).HasMaxLength(20);
            e.Ignore(x => x.LineUserId);
            e.HasMany(x => x.Logins).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Logins).AutoInclude();
        });

        b.Entity<ExternalLogin>(e =>
        {
            e.HasKey(x => new { x.Provider, x.ProviderKey });
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.ProviderKey).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Device).HasMaxLength(200);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PhoneOtp>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => new { x.Phone, x.CreatedAt });
        });
    }
}

internal sealed class IdentityDesignTimeFactory : ModuleDesignTimeFactory<IdentityDbContext>
{
    protected override string Schema => IdentityDbContext.SchemaName;
    protected override IdentityDbContext Create(DbContextOptions<IdentityDbContext> options) => new(options);
}
