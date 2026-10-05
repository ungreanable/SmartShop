using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Shops.Domain;

namespace SmartShop.Modules.Shops.Data;

public sealed class ShopsDbContext(DbContextOptions<ShopsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "shops";

    public override string Schema => SchemaName;

    public DbSet<ShopApplication> Applications => Set<ShopApplication>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<ShopMember> Members => Set<ShopMember>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<ShopClosure> Closures => Set<ShopClosure>();
    public DbSet<ShopInvite> Invites => Set<ShopInvite>();
    public DbSet<ShopFavorite> Favorites => Set<ShopFavorite>();
    public DbSet<ShopStatusTracker> StatusTrackers => Set<ShopStatusTracker>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ShopApplication>(e =>
        {
            e.ToTable("applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.HouseNo).HasMaxLength(30);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.ReviewNote).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.PlantId, x.Status });
            e.HasIndex(x => x.ApplicantId);
        });

        b.Entity<Shop>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Code).HasMaxLength(4);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.HouseNo).HasMaxLength(30);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.LineContact).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.SuspendReason).HasMaxLength(500);
            e.Property(x => x.VacationMessage).HasMaxLength(200);
            e.Property(x => x.PickupInstruction).HasMaxLength(300);
            e.Property(x => x.DeliveryZoneNote).HasMaxLength(300);
            e.Property(x => x.DeliveryMinOrder).HasPrecision(12, 2);
            e.Property(x => x.RatingAverage).HasPrecision(3, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OverrideMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AcceptMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Version).IsRowVersion();
            e.Ignore(x => x.OwnerId);
            e.Ignore(x => x.IsListed);
            e.HasIndex(x => new { x.PlantId, x.Code }).IsUnique();

            e.OwnsMany(x => x.Hours, h => h.ToJson("hours"));
            e.HasMany(x => x.Closures).WithOne().HasForeignKey(c => c.ShopId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.ShopId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.PaymentMethods).WithOne().HasForeignKey(p => p.ShopId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Closures).AutoInclude();
            e.Navigation(x => x.Members).AutoInclude();
            e.Navigation(x => x.PaymentMethods).AutoInclude();
        });

        b.Entity<ShopClosure>(e =>
        {
            e.ToTable("closures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(200);
        });

        b.Entity<ShopMember>(e =>
        {
            e.ToTable("members");
            e.HasKey(x => new { x.ShopId, x.UserId });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DisplayLabel).HasMaxLength(40);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PaymentMethod>(e =>
        {
            e.ToTable("payment_methods");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DisplayName).HasMaxLength(60);
            e.Property(x => x.PromptPayId).HasMaxLength(20);
            e.Property(x => x.BankName).HasMaxLength(60);
            e.Property(x => x.AccountNumber).HasMaxLength(30);
            e.Property(x => x.AccountName).HasMaxLength(100);
            e.Property(x => x.Instructions).HasMaxLength(500);
        });

        b.Entity<ShopInvite>(e =>
        {
            e.ToTable("invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<ShopStatusTracker>(e =>
        {
            e.ToTable("status_trackers");
            e.HasKey(x => x.ShopId);
            e.Property(x => x.LastAnnouncedState).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<ShopFavorite>(e =>
        {
            e.ToTable("favorites");
            e.HasKey(x => new { x.UserId, x.ShopId });
            e.HasIndex(x => x.ShopId);
        });
    }
}

internal sealed class ShopsDesignTimeFactory : ModuleDesignTimeFactory<ShopsDbContext>
{
    protected override string Schema => ShopsDbContext.SchemaName;
    protected override ShopsDbContext Create(DbContextOptions<ShopsDbContext> options) => new(options);
}
