using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Ordering.Domain;

namespace SmartShop.Modules.Ordering.Data;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "ordering";

    public override string Schema => SchemaName;

    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderMessage> Messages => Set<OrderMessage>();
    public DbSet<OrderReport> Reports => Set<OrderReport>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<Cart>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.PlantId }).IsUnique();
            e.OwnsMany(x => x.Lines, l => l.ToJson("lines"));
        });

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderNo).HasMaxLength(16);
            e.Property(x => x.ShopName).HasMaxLength(80);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.FulfillmentType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PaymentMethodType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PaymentStatus).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.PaymentMethodName).HasMaxLength(60);
            e.Property(x => x.PromotionCode).HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(300);
            e.Property(x => x.CancelRequestReason).HasMaxLength(300);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.Subtotal).HasPrecision(12, 2);
            e.Property(x => x.Discount).HasPrecision(12, 2);
            e.Property(x => x.Total).HasPrecision(12, 2);
            e.Property(x => x.Version).IsRowVersion();
            e.Ignore(x => x.IsTerminal);
            e.OwnsOne(x => x.DeliveryAddress, a => a.ToJson("delivery_address"));
            e.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson("lines");
                l.Ignore(x => x.LineTotal);
                l.OwnsMany(x => x.Options);
            });
            e.OwnsMany(x => x.Timeline, t => t.ToJson("timeline"));
            e.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.ShopId, x.Status, x.PlacedAt });
            e.HasIndex(x => new { x.CustomerId, x.PlacedAt });
            e.HasIndex(x => new { x.PlantId, x.PlacedAt });
            e.HasIndex(x => x.PreOrderRoundId);
            e.HasIndex(x => new { x.ShopId, x.OrderNo });
        });

        b.Entity<OrderCounter>(e =>
        {
            e.ToTable("order_counters");
            e.HasKey(x => new { x.ShopId, x.Day });
        });

        b.Entity<OrderMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Body).HasMaxLength(1000);
            e.HasIndex(x => new { x.OrderId, x.SentAt });
        });

        b.Entity<OrderReport>(e =>
        {
            e.ToTable("reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Resolution).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.PlantId, x.Status });
        });
    }
}

/// <summary>Daily running number per shop for readable order numbers (A-001, A-002 ...).</summary>
public sealed class OrderCounter
{
    public Guid ShopId { get; set; }
    public DateOnly Day { get; set; }
    public int Last { get; set; }
}

internal sealed class OrderingDesignTimeFactory : ModuleDesignTimeFactory<OrderingDbContext>
{
    protected override string Schema => OrderingDbContext.SchemaName;
    protected override OrderingDbContext Create(DbContextOptions<OrderingDbContext> options) => new(options);
}
