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
        services.AddScoped<IRecurringJob, OverdueOrderSweepJob>();
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

    public async Task<IReadOnlyList<OrderExport>> ListForShopAsync(Guid shopId, DateTimeOffset? placedSince, OrderStatus? status, int limit, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.ShopId == shopId);
        if (placedSince is { } since) query = query.Where(o => o.PlacedAt >= since);
        if (status is { } s) query = query.Where(o => o.Status == s);
        var orders = await query.OrderByDescending(o => o.PlacedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        return orders.Select(Export).ToList();
    }

    public async Task<OrderExport?> GetForShopAsync(Guid shopId, Guid orderId, CancellationToken ct = default) =>
        await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.ShopId == shopId, ct) is { } order ? Export(order) : null;

    private static OrderExport Export(Domain.Order o) => new(
        o.Id, o.OrderNo, o.Status, o.CustomerId, o.FulfillmentType,
        o.DeliveryAddress?.HouseNo, o.DeliveryAddress?.Soi, o.DeliveryAddress?.Note, o.Note,
        o.ScheduledFrom, o.ScheduledTo, o.Subtotal, o.Discount, o.Total, o.PaymentMethodType, o.PaymentStatus,
        o.PlacedAt, o.AcceptedAt, o.CompletedAt,
        o.Lines.Select(l => new OrderExportLine(l.ItemId, l.Name, l.Quantity, l.UnitPrice, l.LineTotal,
            l.Options.Select(x => $"{x.Group}: {x.Name}").ToList(), l.Note, l.SlotStart)).ToList());
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

/// <summary>
/// Safety net for the order deadlines that run as scheduled commands (expiry while waiting for the shop or for the customer
/// to agree to the prices, automatic completion after delivery). A lost or dead-lettered command left the order open
/// forever: its stock and coupon stayed reserved, the shop kept being asked to accept it. The handlers re-check the
/// order, so sending a command again is harmless.
/// </summary>
internal sealed class OverdueOrderSweepJob(OrderingDbContext db, Wolverine.IMessageBus bus, TimeProvider clock) : IRecurringJob
{
    /// <summary>Scheduled commands run a few seconds late (polling); only later than this counts as lost.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public string Name => "ordering.overdue-sweep";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - Grace;
        // Open orders only (closed_at is null): the small partial index, not the order history.
        var overdue = await db.Orders.AsNoTracking()
            .Where(o => o.ClosedAt == null)
            .Where(o => ((o.Status == OrderStatus.PendingAcceptance || o.Status == OrderStatus.AwaitingPriceApproval) && o.ExpiresAt < cutoff)
                        || (o.Status == OrderStatus.Delivered && o.AutoCompleteAt < cutoff))
            .Select(o => new { o.Id, o.Status })
            .ToListAsync(ct);
        foreach (var order in overdue)
        {
            if (order.Status == OrderStatus.PendingAcceptance) await bus.PublishAsync(new ExpireOrderIfNotAccepted(order.Id));
            else if (order.Status == OrderStatus.AwaitingPriceApproval) await bus.PublishAsync(new ExpireQuoteIfNotAnswered(order.Id));
            else await bus.PublishAsync(new AutoCompleteOrder(order.Id));
        }
    }
}
