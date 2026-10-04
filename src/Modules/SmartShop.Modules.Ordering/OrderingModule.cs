using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Ordering;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Endpoints;
using SmartShop.Modules.Ordering.Services;

namespace SmartShop.Modules.Ordering;

public sealed class OrderingModule : IModule
{
    public string Name => "Ordering";

    public IReadOnlyList<Type> DbContexts => [typeof(OrderingDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<OrderingDbContext>(OrderingDbContext.SchemaName);
        services.AddScoped<CartService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<OrderActions>();
        services.AddScoped<IOrderDirectory, OrderDirectory>();
        services.AddScoped<IPersonalDataContributor, OrderingPersonalData>();
        services.AddScoped<IRecurringJob, StaleCartCleanupJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        CustomerOrderEndpoints.Map(app);
        MerchantOrderEndpoints.Map(app);
        ConversationEndpoints.Map(app);
    }
}

internal sealed class OrderDirectory(OrderingDbContext db) : IOrderDirectory
{
    public Task<OrderSummaryInfo?> GetAsync(Guid orderId, CancellationToken ct = default) =>
        db.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new OrderSummaryInfo(o.Id, o.PlantId, o.ShopId, o.CustomerId, o.OrderNo, o.Status, o.Total, o.PaymentMethodId, o.FulfillmentType))
            .FirstOrDefaultAsync(ct);
}

internal sealed class OrderingPersonalData(OrderingDbContext db) : IPersonalDataContributor
{
    public string Section => "orders";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct = default) =>
        await db.Orders.AsNoTracking().Where(o => o.CustomerId == userId).OrderByDescending(o => o.PlacedAt)
            .Select(o => new { o.OrderNo, o.ShopName, o.Status, o.Total, o.PlacedAt, o.DeliveryAddress, o.Note, o.Lines })
            .ToListAsync(ct);
}

/// <summary>Removes carts untouched for 7 days.</summary>
internal sealed class StaleCartCleanupJob(OrderingDbContext db, TimeProvider clock) : IRecurringJob
{
    public string Name => "ordering.stale-carts";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-7);
        await db.Carts.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
