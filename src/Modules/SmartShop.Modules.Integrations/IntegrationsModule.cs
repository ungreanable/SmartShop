using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Integrations;

public sealed record CreateApiKeyRequest(string Name, List<string> Scopes);
public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, List<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);
public sealed record WebhookRequest(string Url, List<string> Events, bool Active = true);
public sealed record WebhookDto(Guid Id, string Url, List<string> Events, bool Active, int ConsecutiveFailures, string? DisabledReason, string Secret, DateTimeOffset CreatedAt);
public sealed record DeliveryDto(Guid Id, string EventType, DeliveryStatus Status, int Attempts, int? ResponseStatus, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt);

/// <summary>The shop an API key belongs to, resolved by <see cref="IntegrationsModule.RequireApiKey"/>.</summary>
public sealed record ApiKeyContext(Guid KeyId, Guid ShopId, Guid PlantId, IReadOnlyList<string> Scopes);

public sealed class IntegrationsModule : IModule
{
    public const string ApiKeyHeader = "X-Api-Key";

    public string Name => "Integrations";

    public IReadOnlyList<Type> DbContexts => [typeof(IntegrationsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<IntegrationsDbContext>(IntegrationsDbContext.SchemaName);
        builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));
        builder.Services.AddScoped<WebhookSender>();
        builder.Services.AddHttpClient(WebhookSender.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => WebhookUrlPolicy.CreateHandler(sp.GetRequiredService<IOptions<WebhookOptions>>()));
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        MapMerchant(app);
        MapPublicApi(app);
    }

    private static void MapMerchant(IEndpointRouteBuilder app)
    {
        var merchant = app.MapGroup("/merchant").WithTags("Merchant integrations").RequirePlantMember();

        merchant.MapGet("/shops/{shopId:guid}/api-keys", async (Guid shopId, IShopAccess access, IntegrationsDbContext db, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Owner, ct);
            return (await db.ApiKeys.AsNoTracking().Where(k => k.ShopId == shopId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();
        });

        merchant.MapPost("/shops/{shopId:guid}/api-keys", async (Guid shopId, CreateApiKeyRequest req, IShopAccess access, ICurrentPlant plant,
            ICurrentUser user, IShopDirectory shops, IntegrationsDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Owner, ct);
            await RequireShopInPlantAsync(shops, shopId, plant, ct);
            if (await db.ApiKeys.CountAsync(k => k.ShopId == shopId && k.RevokedAt == null, ct) >= 10)
                throw new DomainException("too_many_api_keys", "A shop can have at most 10 active API keys.");
            var (key, plain) = ApiKey.Create(shopId, plant.PlantId, req.Name, req.Scopes ?? [], user.Id, clock.GetUtcNow());
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new CreatedApiKeyDto(ToDto(key), plain));
        });

        merchant.MapDelete("/api-keys/{id:guid}", async (Guid id, IShopAccess access, ICurrentPlant plant, IntegrationsDbContext db, HybridCache cache,
            TimeProvider clock, CancellationToken ct) =>
        {
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.PlantId == plant.PlantId, ct) ?? throw new NotFoundException("ApiKey", id);
            await access.RequireAsync(key.ShopId, ShopRole.Owner, ct);
            key.Revoke(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(CacheKey(key.Hash), ct);
            return Results.NoContent();
        });

        merchant.MapGet("/shops/{shopId:guid}/webhooks", async (Guid shopId, IShopAccess access, IntegrationsDbContext db, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Owner, ct);
            return (await db.Webhooks.AsNoTracking().Where(w => w.ShopId == shopId).OrderBy(w => w.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();
        });

        merchant.MapPost("/shops/{shopId:guid}/webhooks", async (Guid shopId, WebhookRequest req, IShopAccess access, ICurrentPlant plant,
            IShopDirectory shops, IntegrationsDbContext db, IOptions<WebhookOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Owner, ct);
            await RequireShopInPlantAsync(shops, shopId, plant, ct);
            if (await db.Webhooks.CountAsync(w => w.ShopId == shopId, ct) >= 5)
                throw new DomainException("too_many_webhooks", "A shop can have at most 5 webhook endpoints.");
            var url = WebhookUrlPolicy.Validate(req.Url, options.Value).ToString();
            var endpoint = WebhookEndpoint.Create(shopId, plant.PlantId, url, req.Events ?? [], clock.GetUtcNow());
            db.Webhooks.Add(endpoint);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(endpoint));
        });

        merchant.MapPut("/webhooks/{id:guid}", async (Guid id, WebhookRequest req, IShopAccess access, ICurrentPlant plant, IntegrationsDbContext db,
            IOptions<WebhookOptions> options, CancellationToken ct) =>
        {
            var endpoint = await FindWebhookAsync(db, id, plant, access, ct);
            endpoint.Update(WebhookUrlPolicy.Validate(req.Url, options.Value).ToString(), req.Events ?? [], req.Active);
            await db.SaveChangesAsync(ct);
            return ToDto(endpoint);
        });

        merchant.MapDelete("/webhooks/{id:guid}", async (Guid id, IShopAccess access, ICurrentPlant plant, IntegrationsDbContext db, CancellationToken ct) =>
        {
            var endpoint = await FindWebhookAsync(db, id, plant, access, ct);
            await db.Deliveries.Where(d => d.EndpointId == id).ExecuteDeleteAsync(ct);
            db.Webhooks.Remove(endpoint);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Sends a "ping" right away so the shop can check its receiver.
        merchant.MapPost("/webhooks/{id:guid}/test", async (Guid id, IShopAccess access, ICurrentPlant plant, IntegrationsDbContext db,
            WebhookSender sender, TimeProvider clock, CancellationToken ct) =>
        {
            var endpoint = await FindWebhookAsync(db, id, plant, access, ct);
            var now = clock.GetUtcNow();
            var deliveryId = Ids.New();
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new { id = deliveryId, type = WebhookEvents.Ping, createdAt = now, shopId = endpoint.ShopId, data = new { message = "SmartShop webhook test" } },
                WebhookSender.Json);
            var delivery = WebhookDelivery.Create(deliveryId, endpoint, WebhookEvents.Ping, payload, now);
            var (ok, status, error) = await sender.SendAsync(endpoint, delivery, ct);
            if (ok) delivery.Succeeded(status!.Value, clock.GetUtcNow());
            else
            {
                delivery.Failed(status, error ?? "error", clock.GetUtcNow());
                delivery.Skip(); // a test is never retried
            }
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync(ct);
            return ToDto(delivery);
        });

        merchant.MapGet("/webhooks/{id:guid}/deliveries", async (Guid id, IShopAccess access, ICurrentPlant plant, IntegrationsDbContext db, CancellationToken ct) =>
        {
            await FindWebhookAsync(db, id, plant, access, ct);
            return (await db.Deliveries.AsNoTracking().Where(d => d.EndpointId == id).OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync(ct))
                .Select(ToDto).ToList();
        });
    }

    private static void MapPublicApi(IEndpointRouteBuilder app)
    {
        // Machine-to-machine API for one shop, authenticated with an API key (no LINE user involved).
        var v1 = app.MapGroup("/public/v1").WithTags("Public API v1").RequireRateLimiting("public-api");

        v1.MapGet("/shop", async (HttpContext http, IShopDirectory shops, CancellationToken ct) =>
        {
            var key = ApiKeyOf(http);
            var info = await shops.GetOrderingInfoAsync(key.ShopId, ct) ?? throw new NotFoundException("Shop", key.ShopId);
            return new { info.ShopId, info.Name, info.Code, info.IsActive, key.Scopes };
        }).AddEndpointFilter(RequireApiKey(ApiScopes.OrdersRead, ApiScopes.MenuRead));

        v1.MapGet("/orders", async (HttpContext http, IOrderDirectory orders, IUserDirectory users, DateTimeOffset? since, OrderStatus? status, int? limit, CancellationToken ct) =>
        {
            var key = ApiKeyOf(http);
            var list = await orders.ListForShopAsync(key.ShopId, since, status, limit ?? 50, ct);
            var names = await users.GetProfilesAsync(list.Select(o => o.CustomerId).Distinct(), ct);
            return list.Select(o => new WebhookOrderData(o, names.GetValueOrDefault(o.CustomerId)?.DisplayName)).ToList();
        }).AddEndpointFilter(RequireApiKey(ApiScopes.OrdersRead));

        v1.MapGet("/orders/{orderId:guid}", async (Guid orderId, HttpContext http, IOrderDirectory orders, IUserDirectory users, CancellationToken ct) =>
        {
            var key = ApiKeyOf(http);
            var order = await orders.GetForShopAsync(key.ShopId, orderId, ct) ?? throw new NotFoundException("Order", orderId);
            var customer = (await users.GetProfilesAsync([order.CustomerId], ct)).GetValueOrDefault(order.CustomerId);
            return new WebhookOrderData(order, customer?.DisplayName);
        }).AddEndpointFilter(RequireApiKey(ApiScopes.OrdersRead));

        v1.MapGet("/menu", async (HttpContext http, ICatalogService catalog, CancellationToken ct) =>
            await catalog.ListItemsAsync(ApiKeyOf(http).ShopId, ct))
            .AddEndpointFilter(RequireApiKey(ApiScopes.MenuRead));
    }

    /// <summary>Validates <c>X-Api-Key</c> (cached by hash) and the required scope; 401 without a valid key, 403 without the scope.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireApiKey(params string[] anyOfScopes) =>
        async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var plain = http.Request.Headers[ApiKeyHeader].ToString();
            if (string.IsNullOrEmpty(plain) || !plain.StartsWith("ssk_", StringComparison.Ordinal) || plain.Length > 100)
                return Results.Problem(statusCode: 401, title: "A valid X-Api-Key header is required.", extensions: new Dictionary<string, object?> { ["code"] = "api_key_invalid" });

            var hash = ApiKey.HashOf(plain);
            var cache = http.RequestServices.GetRequiredService<HybridCache>();
            var key = await cache.GetOrCreateAsync(CacheKey(hash), async token =>
            {
                var db = http.RequestServices.GetRequiredService<IntegrationsDbContext>();
                var found = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Hash == hash && k.RevokedAt == null, token);
                return found is null ? null : new ApiKeyContext(found.Id, found.ShopId, found.PlantId, found.Scopes);
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(2), LocalCacheExpiration = TimeSpan.FromSeconds(30) },
                cancellationToken: http.RequestAborted);

            if (key is null)
                return Results.Problem(statusCode: 401, title: "A valid X-Api-Key header is required.", extensions: new Dictionary<string, object?> { ["code"] = "api_key_invalid" });
            if (!anyOfScopes.Any(key.Scopes.Contains))
                return Results.Problem(statusCode: 403, title: "This API key does not have the required scope.", extensions: new Dictionary<string, object?> { ["code"] = "api_key_scope" });

            http.Items[typeof(ApiKeyContext)] = key;
            await TouchAsync(http, key.KeyId);
            return await next(ctx);
        };

    private static ApiKeyContext ApiKeyOf(HttpContext http) => (ApiKeyContext)http.Items[typeof(ApiKeyContext)]!;

    /// <summary>Records "last used" at most once a minute per key to keep the API cheap.</summary>
    private static async Task TouchAsync(HttpContext http, Guid keyId)
    {
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var cutoff = now.AddMinutes(-1);
        var db = http.RequestServices.GetRequiredService<IntegrationsDbContext>();
        await db.ApiKeys.Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt < cutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), http.RequestAborted);
    }

    private static string CacheKey(string hash) => $"integrations:apikey:{hash}";

    private static async Task RequireShopInPlantAsync(IShopDirectory shops, Guid shopId, ICurrentPlant plant, CancellationToken ct)
    {
        var shop = await shops.GetOrderingInfoAsync(shopId, ct);
        if (shop is null || shop.PlantId != plant.PlantId) throw new NotFoundException("Shop", shopId);
    }

    private static async Task<WebhookEndpoint> FindWebhookAsync(IntegrationsDbContext db, Guid id, ICurrentPlant plant, IShopAccess access, CancellationToken ct)
    {
        var endpoint = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.PlantId == plant.PlantId, ct) ?? throw new NotFoundException("Webhook", id);
        await access.RequireAsync(endpoint.ShopId, ShopRole.Owner, ct);
        return endpoint;
    }

    private static ApiKeyDto ToDto(ApiKey k) => new(k.Id, k.Name, k.Prefix, k.Scopes, k.CreatedAt, k.LastUsedAt, k.RevokedAt is not null);

    private static WebhookDto ToDto(WebhookEndpoint w) => new(w.Id, w.Url, w.Events, w.Active, w.ConsecutiveFailures, w.DisabledReason, w.Secret, w.CreatedAt);

    private static DeliveryDto ToDto(WebhookDelivery d) => new(d.Id, d.EventType, d.Status, d.Attempts, d.ResponseStatus, d.Error, d.CreatedAt, d.LastAttemptAt);
}
