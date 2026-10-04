using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartShop.Infrastructure.Persistence;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Integrations;

public static class ApiScopes
{
    public const string OrdersRead = "orders:read";
    public const string MenuRead = "menu:read";

    public static readonly string[] All = [OrdersRead, MenuRead];
}

/// <summary>
/// A shop's key for the public API (POS, printers, spreadsheets). Only a SHA-256 hash is stored;
/// the full key (<c>ssk_{prefix}_{secret}</c>) is shown once when created.
/// </summary>
public sealed class ApiKey
{
    private ApiKey() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public string Name { get; private set; } = "";
    public string Prefix { get; private set; } = "";
    public string Hash { get; private set; } = "";
    public List<string> Scopes { get; private set; } = [];
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static (ApiKey Key, string Plain) Create(Guid shopId, Guid plantId, string name, IEnumerable<string> scopes, Guid actor, DateTimeOffset now)
    {
        var requested = scopes.Distinct().ToList();
        Guard.That(requested.Count > 0 && requested.All(ApiScopes.All.Contains), "validation", "Unknown or missing scopes.");
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var plain = $"ssk_{prefix}_{Base64Url(RandomNumberGenerator.GetBytes(24))}";
        return (new ApiKey
        {
            Id = Ids.New(),
            ShopId = shopId,
            PlantId = plantId,
            Name = Guard.NotEmpty(name, "Name", 60),
            Prefix = prefix,
            Hash = HashOf(plain),
            Scopes = requested,
            CreatedBy = actor,
            CreatedAt = now,
        }, plain);
    }

    public static string HashOf(string plain) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void Touch(DateTimeOffset now) => LastUsedAt = now;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class WebhookEvents
{
    public const string Ping = "ping";

    public static readonly string[] All =
    [
        "order.placed", "order.accepted", "order.preparing", "order.ready", "order.delivered", "order.completed",
        "order.cancelled", "order.rejected", "order.expired", "payment.verified",
    ];
}

/// <summary>An HTTPS endpoint of the shop that receives signed event notifications.</summary>
public sealed class WebhookEndpoint
{
    public const int DisableAfterFailures = 20;

    private WebhookEndpoint() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid PlantId { get; private set; }
    public string Url { get; private set; } = "";
    public string Secret { get; private set; } = "";
    public List<string> Events { get; private set; } = [];
    public bool Active { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public string? DisabledReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static WebhookEndpoint Create(Guid shopId, Guid plantId, string url, IEnumerable<string> events, DateTimeOffset now)
    {
        var endpoint = new WebhookEndpoint
        {
            Id = Ids.New(),
            ShopId = shopId,
            PlantId = plantId,
            Secret = "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            CreatedAt = now,
        };
        endpoint.Update(url, events, true);
        return endpoint;
    }

    public void Update(string url, IEnumerable<string> events, bool active)
    {
        Url = Guard.NotEmpty(url, "Url", 500);
        var list = events.Distinct().ToList();
        Guard.That(list.Count > 0 && list.All(WebhookEvents.All.Contains), "validation", "Unknown or missing events.");
        Events = list;
        if (active && !Active) ConsecutiveFailures = 0;
        Active = active;
        if (active) DisabledReason = null;
    }

    public void RecordSuccess() => ConsecutiveFailures = 0;

    public void RecordFailure()
    {
        if (++ConsecutiveFailures < DisableAfterFailures) return;
        Active = false;
        DisabledReason = "too_many_failures";
    }
}

public enum DeliveryStatus { Pending, Delivered, Failed, Skipped }

public sealed class WebhookDelivery
{
    public const int MaxAttempts = 6;

    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    private WebhookDelivery() { }

    public Guid Id { get; private set; }
    public Guid EndpointId { get; private set; }
    public Guid ShopId { get; private set; }
    public string EventType { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public DeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int? ResponseStatus { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }

    public static WebhookDelivery Create(Guid id, WebhookEndpoint endpoint, string eventType, string payload, DateTimeOffset now) => new()
    {
        Id = id,
        EndpointId = endpoint.Id,
        ShopId = endpoint.ShopId,
        EventType = eventType,
        Payload = payload,
        Status = DeliveryStatus.Pending,
        CreatedAt = now,
    };

    public void Succeeded(int status, DateTimeOffset now)
    {
        Attempts++;
        (Status, ResponseStatus, Error, LastAttemptAt) = (DeliveryStatus.Delivered, status, null, now);
    }

    /// <summary>Returns when to try again, or null when giving up.</summary>
    public TimeSpan? Failed(int? status, string error, DateTimeOffset now)
    {
        Attempts++;
        (ResponseStatus, Error, LastAttemptAt) = (status, error.Length > 300 ? error[..300] : error, now);
        if (Attempts >= MaxAttempts)
        {
            Status = DeliveryStatus.Failed;
            return null;
        }
        return Backoff[Math.Min(Attempts - 1, Backoff.Length - 1)];
    }

    public void Skip() => Status = DeliveryStatus.Skipped;
}

public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "integrations";

    public override string Schema => SchemaName;

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<WebhookEndpoint> Webhooks => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => x.ShopId);
        });
        b.Entity<WebhookEndpoint>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.Secret).HasMaxLength(80);
            e.Property(x => x.DisabledReason).HasMaxLength(60);
            e.HasIndex(x => x.ShopId);
        });
        b.Entity<WebhookDelivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(300);
            e.HasIndex(x => new { x.EndpointId, x.CreatedAt });
        });
    }
}

internal sealed class IntegrationsDesignTimeFactory : ModuleDesignTimeFactory<IntegrationsDbContext>
{
    protected override string Schema => IntegrationsDbContext.SchemaName;
    protected override IntegrationsDbContext Create(DbContextOptions<IntegrationsDbContext> options) => new(options);
}
