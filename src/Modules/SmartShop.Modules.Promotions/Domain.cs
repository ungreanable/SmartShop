using Microsoft.EntityFrameworkCore;
using SmartShop.Infrastructure.Persistence;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Promotions;

public enum PromotionType { Percent, Amount }

public enum RedemptionStatus { Active, Released }

public sealed record PromotionInput(string? Code, string Title, PromotionType Type, decimal Value, decimal? MinOrder, decimal? MaxDiscount,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UsageLimit, int? PerUserLimit, bool AutoApply, bool Active);

/// <summary>
/// A shop discount: either a coupon code customers type in, or an automatic promotion (no code needed).
/// <see cref="UsedCount"/> is only changed by atomic SQL so concurrent checkouts can never exceed <see cref="UsageLimit"/>.
/// </summary>
public sealed class Promotion
{
    private Promotion() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public string? Code { get; private set; }
    public string Title { get; private set; } = "";
    public PromotionType Type { get; private set; }
    public decimal Value { get; private set; }
    public decimal? MinOrder { get; private set; }
    public decimal? MaxDiscount { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public int? UsageLimit { get; private set; }
    public int? PerUserLimit { get; private set; }
    public int UsedCount { get; private set; }
    public bool AutoApply { get; private set; }
    public bool Active { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Promotion Create(Guid shopId, Guid plantId, PromotionInput input, DateTimeOffset now)
    {
        var p = new Promotion { Id = Ids.New(), ShopId = shopId, PlantId = plantId, CreatedAt = now };
        p.Update(input);
        return p;
    }

    public void Update(PromotionInput input)
    {
        Code = NormalizeCode(input.Code);
        Title = Guard.NotEmpty(input.Title, "Title", 100);
        Type = input.Type;
        Value = Guard.Money(input.Value, "Value");
        Guard.That(Value > 0, "validation", "Value must be greater than 0.");
        Guard.That(Type != PromotionType.Percent || Value <= 100, "validation", "A percentage must be at most 100.");
        MinOrder = input.MinOrder is { } min ? Guard.Money(min, "MinOrder") : null;
        MaxDiscount = input.MaxDiscount is { } max ? Guard.Money(max, "MaxDiscount") : null;
        Guard.That(input.StartsAt is null || input.EndsAt is null || input.StartsAt < input.EndsAt, "validation", "The end must be after the start.");
        StartsAt = input.StartsAt?.ToUniversalTime();
        EndsAt = input.EndsAt?.ToUniversalTime();
        UsageLimit = input.UsageLimit is { } limit ? Guard.Range(limit, "UsageLimit", 1, 1_000_000) : null;
        PerUserLimit = input.PerUserLimit is { } perUser ? Guard.Range(perUser, "PerUserLimit", 1, 1000) : null;
        // A promotion without a code can only ever be applied automatically.
        AutoApply = input.AutoApply || Code is null;
        Active = input.Active;
    }

    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = code.Trim().ToUpperInvariant();
        Guard.That(normalized.Length is >= 3 and <= 30 && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "validation", "A coupon code is 3-30 letters, digits, '-' or '_'.");
        return normalized;
    }

    public bool IsLive(DateTimeOffset now) =>
        Active && (StartsAt is null || StartsAt <= now) && (EndsAt is null || now < EndsAt) && (UsageLimit is null || UsedCount < UsageLimit);

    /// <summary>Discount for a subtotal, or 0 when the minimum order is not met.</summary>
    public decimal DiscountFor(decimal subtotal)
    {
        if (MinOrder is { } min && subtotal < min) return 0;
        var discount = Type == PromotionType.Percent ? decimal.Round(subtotal * Value / 100, 0, MidpointRounding.ToZero) : Value;
        if (MaxDiscount is { } cap) discount = Math.Min(discount, cap);
        return Math.Min(discount, subtotal);
    }

    public string Describe() => Type == PromotionType.Percent
        ? $"{Title} (-{Value:0.##}%)"
        : $"{Title} (-{Value:N0}฿)";
}

public sealed class Redemption
{
    private Redemption() { }

    public Guid Id { get; private set; }
    public Guid PromotionId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public decimal Discount { get; private set; }
    public RedemptionStatus Status { get; private set; }
    public DateTimeOffset At { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
}

public sealed class PromotionsDbContext(DbContextOptions<PromotionsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "promotions";

    public override string Schema => SchemaName;

    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<Redemption> Redemptions => Set<Redemption>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<Promotion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Value).HasPrecision(12, 2);
            e.Property(x => x.MinOrder).HasPrecision(12, 2);
            e.Property(x => x.MaxDiscount).HasPrecision(12, 2);
            e.HasIndex(x => new { x.ShopId, x.Code }).IsUnique().HasFilter("code IS NOT NULL");
            e.HasIndex(x => x.ShopId);
        });
        b.Entity<Redemption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Discount).HasPrecision(12, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.PromotionId, x.UserId });
        });
    }
}

internal sealed class PromotionsDesignTimeFactory : ModuleDesignTimeFactory<PromotionsDbContext>
{
    protected override string Schema => PromotionsDbContext.SchemaName;
    protected override PromotionsDbContext Create(DbContextOptions<PromotionsDbContext> options) => new(options);
}
