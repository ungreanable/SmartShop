using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Catalog.Domain;

namespace SmartShop.Modules.Catalog.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "catalog";

    public override string Schema => SchemaName;

    public DbSet<ItemCategory> Categories => Set<ItemCategory>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<SlotBooking> SlotBookings => Set<SlotBooking>();
    public DbSet<PreOrderRound> PreOrderRounds => Set<PreOrderRound>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ItemCategory>(e =>
        {
            e.ToTable("categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => x.ShopId);
        });

        b.Entity<Item>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Price).HasPrecision(12, 2);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.StockMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Version).IsRowVersion();
            e.OwnsMany(x => x.AvailabilityWindows, w => w.ToJson("availability_windows"));
            e.OwnsMany(x => x.ModifierGroups, g =>
            {
                g.ToJson("modifier_groups");
                g.OwnsMany(x => x.Options);
            });
            e.HasIndex(x => new { x.ShopId, x.DeletedAt });
            e.HasIndex(x => x.PlantId);
            e.HasIndex(x => x.PreOrderRoundId);
        });

        b.Entity<Inventory>(e =>
        {
            e.ToTable("inventories");
            e.HasKey(x => x.ItemId);
            e.Ignore(x => x.Available);
            e.ToTable(t => t.HasCheckConstraint("ck_inventory_non_negative", "reserved >= 0 AND on_hand >= reserved"));
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.ItemId, x.At });
            // Makes reserve/commit/release idempotent under at-least-once event delivery.
            e.HasIndex(x => new { x.OrderId, x.ItemId, x.Type }).IsUnique().HasFilter("order_id IS NOT NULL");
        });

        b.Entity<SlotBooking>(e =>
        {
            e.ToTable("slot_bookings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ItemId, x.SlotStart });
            e.HasIndex(x => new { x.OrderId, x.ItemId }).IsUnique();
        });

        b.Entity<PreOrderRound>(e =>
        {
            e.ToTable("preorder_rounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ShopId, x.Status });
        });
    }
}

internal sealed class CatalogDesignTimeFactory : ModuleDesignTimeFactory<CatalogDbContext>
{
    protected override string Schema => CatalogDbContext.SchemaName;
    protected override CatalogDbContext Create(DbContextOptions<CatalogDbContext> options) => new(options);
}
