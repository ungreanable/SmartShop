using Microsoft.EntityFrameworkCore;
using SmartShop.Infrastructure.Persistence;

namespace SmartShop.Modules.Analytics.Data;

/// <summary>Stored as a number: the raw table is the biggest in the module, and the rollup SQL filters on these values.</summary>
public enum AnalyticsEventType : short
{
    ShopView = 1,
    ItemView = 2,
    AddToCart = 3,
    CartView = 4,
    OrderPlaced = 10,
    /// <summary>One row per catalogued item of a placed order (quantity summed), for per-item "ordered".</summary>
    OrderLine = 11,
    OrderCompleted = 12,
    /// <summary>Cancelled, rejected or expired.</summary>
    OrderLost = 13,
}

/// <summary>
/// One raw event, kept for <see cref="Services.AnalyticsRetentionJob.Retention"/> and then deleted; the daily
/// aggregates are what the dashboard reads. <see cref="Visitor"/> is a day-scoped keyed hash, never a user id.
/// </summary>
public sealed class AnalyticsEvent
{
    public long Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid ShopId { get; set; }
    public AnalyticsEventType Type { get; set; }
    public Guid? ItemId { get; set; }
    public string Visitor { get; set; } = "";
    public int Quantity { get; set; }
    /// <summary>Add-to-cart of a custom ("ตามสั่ง") dish.</summary>
    public bool Custom { get; set; }
    /// <summary>Placed order whose customer had ordered from this shop before (see <see cref="ShopCustomer"/>).</summary>
    public bool Repeat { get; set; }
    public Guid? OrderId { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Per shop and Bangkok day. Visitor-type figures are distinct visitors of that day.</summary>
public sealed class ShopDaily
{
    public Guid ShopId { get; set; }
    public DateOnly Day { get; set; }
    public Guid PlantId { get; set; }
    public int Visitors { get; set; }
    public int ShopViews { get; set; }
    public int ItemViewers { get; set; }
    public int CartAdders { get; set; }
    public int CartViewers { get; set; }
    public int Buyers { get; set; }
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public int CompletedOrders { get; set; }
    public decimal CompletedRevenue { get; set; }
    public int LostOrders { get; set; }
    /// <summary>Visitors who added to the cart but did not order that day.</summary>
    public int AbandonedCarts { get; set; }
    public int RepeatOrders { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemDaily
{
    public Guid ShopId { get; set; }
    public Guid ItemId { get; set; }
    public DateOnly Day { get; set; }
    public Guid PlantId { get; set; }
    public int Views { get; set; }
    public int Viewers { get; set; }
    public int AddToCart { get; set; }
    public int Buyers { get; set; }
    public int OrderedQuantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Shop-page views per Bangkok hour, summed by day of week for the "busy times" heatmap.</summary>
public sealed class ShopHourly
{
    public Guid ShopId { get; set; }
    public DateOnly Day { get; set; }
    public short Hour { get; set; }
    public Guid PlantId { get; set; }
    public int Views { get; set; }
}

/// <summary>
/// "Has this customer ordered here before?" without storing who they are: the key is a keyed hash of shop and user
/// (its own derived secret, see <see cref="Services.VisitorKeys"/>), so it differs per shop, cannot be reversed, and is
/// only ever used to count repeat orders. Deleted on <c>UserErased</c>.
/// </summary>
public sealed class ShopCustomer
{
    public Guid ShopId { get; set; }
    public string CustomerKey { get; set; } = "";
    public Guid PlantId { get; set; }
    public Guid FirstOrderId { get; set; }
    public DateTimeOffset FirstOrderAt { get; set; }
}

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "analytics";

    public override string Schema => SchemaName;

    public DbSet<AnalyticsEvent> Events => Set<AnalyticsEvent>();
    public DbSet<ShopDaily> ShopDaily => Set<ShopDaily>();
    public DbSet<ItemDaily> ItemDaily => Set<ItemDaily>();
    public DbSet<ShopHourly> ShopHourly => Set<ShopHourly>();
    public DbSet<ShopCustomer> ShopCustomers => Set<ShopCustomer>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<AnalyticsEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Visitor).HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasIndex(x => new { x.ShopId, x.OccurredAt });
            // Append-only and time ordered: a BRIN index is a few pages instead of a B-tree as big as the table, and is all the
            // rollup (last two days) and the retention sweep (older than 90 days) need.
            e.HasIndex(x => x.OccurredAt).HasMethod("brin");
            // Integration events can be delivered twice; an order is recorded once per kind (and item).
            e.HasIndex(x => new { x.OrderId, x.Type, x.ItemId }).IsUnique().AreNullsDistinct(false).HasFilter("order_id IS NOT NULL");
        });

        b.Entity<ShopDaily>(e =>
        {
            e.ToTable("shop_daily");
            e.HasKey(x => new { x.ShopId, x.Day });
            e.Property(x => x.Revenue).HasPrecision(14, 2);
            e.Property(x => x.CompletedRevenue).HasPrecision(14, 2);
        });

        b.Entity<ItemDaily>(e =>
        {
            e.ToTable("item_daily");
            e.HasKey(x => new { x.ShopId, x.ItemId, x.Day });
            e.HasIndex(x => new { x.ShopId, x.Day });
        });

        b.Entity<ShopHourly>(e =>
        {
            e.ToTable("shop_hourly");
            e.HasKey(x => new { x.ShopId, x.Day, x.Hour });
        });

        b.Entity<ShopCustomer>(e =>
        {
            e.ToTable("shop_customers");
            e.HasKey(x => new { x.ShopId, x.CustomerKey });
            e.Property(x => x.CustomerKey).HasMaxLength(32);
            e.HasIndex(x => x.CustomerKey);
        });
    }
}

internal sealed class AnalyticsDesignTimeFactory : ModuleDesignTimeFactory<AnalyticsDbContext>
{
    protected override string Schema => AnalyticsDbContext.SchemaName;
    protected override AnalyticsDbContext Create(DbContextOptions<AnalyticsDbContext> options) => new(options);
}
