using Microsoft.EntityFrameworkCore;

using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Payments.Domain;

namespace SmartShop.Modules.Payments.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "payments";

    public override string Schema => SchemaName;

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentProof> Proofs => Set<PaymentProof>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderNo).HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.MethodType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.RejectReason).HasMaxLength(300);
            e.Property(x => x.Version).IsRowVersion();
            e.OwnsOne(x => x.Method, m => m.ToJson("method"));
            e.HasMany(x => x.Proofs).WithOne().HasForeignKey(p => p.PaymentId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Proofs).AutoInclude();
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.ShopId, x.Status });
        });

        b.Entity<PaymentProof>(e =>
        {
            e.ToTable("proofs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.SlipReference).HasMaxLength(500);
            e.Property(x => x.VerificationMessage).HasMaxLength(300);
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => x.SlipReference);
        });
    }
}

internal sealed class PaymentsDesignTimeFactory : ModuleDesignTimeFactory<PaymentsDbContext>
{
    protected override string Schema => PaymentsDbContext.SchemaName;
    protected override PaymentsDbContext Create(DbContextOptions<PaymentsDbContext> options) => new(options);
}
