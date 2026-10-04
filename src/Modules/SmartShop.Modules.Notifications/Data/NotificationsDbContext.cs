using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Notifications.Domain;

namespace SmartShop.Modules.Notifications.Data;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "notifications";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();
    public DbSet<UserDevice> Devices => Set<UserDevice>();
    public DbSet<LineFriendship> LineFriendships => Set<LineFriendship>();
    public DbSet<NotificationSettings> Settings => Set<NotificationSettings>();
    public DbSet<LineQuotaUsage> LineQuota => Set<LineQuotaUsage>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Body).HasMaxLength(500);
            e.Property(x => x.Link).HasMaxLength(300);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            // One inbox entry per user per source event, even if the event is delivered twice.
            e.HasIndex(x => new { x.SourceEventId, x.UserId }).IsUnique();
        });

        b.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.FailureReason).HasMaxLength(200);
            e.HasIndex(x => new { x.NotificationId, x.Channel }).IsUnique();
        });

        b.Entity<UserDevice>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Endpoint).HasMaxLength(1000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.Property(x => x.Label).HasMaxLength(100);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<LineFriendship>(e =>
        {
            e.ToTable("line_friendships");
            e.HasKey(x => x.UserId);
        });

        b.Entity<NotificationSettings>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.PushMode).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<LineQuotaUsage>(e =>
        {
            e.ToTable("line_quota");
            e.HasKey(x => x.Month);
            e.Property(x => x.Month).HasMaxLength(7);
        });
    }
}

internal sealed class NotificationsDesignTimeFactory : ModuleDesignTimeFactory<NotificationsDbContext>
{
    protected override string Schema => NotificationsDbContext.SchemaName;
    protected override NotificationsDbContext Create(DbContextOptions<NotificationsDbContext> options) => new(options);
}
