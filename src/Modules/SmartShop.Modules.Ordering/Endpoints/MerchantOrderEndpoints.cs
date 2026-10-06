using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Services;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;
using Wolverine;

namespace SmartShop.Modules.Ordering.Endpoints;

public sealed record DeliverRequest(Guid? PhotoId);
public sealed record OrderCountsDto(int New, int Active, int Ready, int Today);
public sealed record PrepItemDto(Guid ItemId, string Name, string Options, int Quantity, int Orders);
public sealed record SalesDayDto(DateOnly Day, int Orders, decimal Revenue);
public sealed record TopItemDto(string Name, int Quantity, decimal Revenue);
public sealed record SalesReportDto(DateOnly From, DateOnly To, int Orders, decimal Revenue, decimal AverageOrder, int Cancelled,
    List<SalesDayDto> Days, List<TopItemDto> TopItems);

public sealed record ForceCloseRequest(string Outcome, string? Reason);

internal static class MerchantOrderEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var shop = api.MapGroup("/merchant/shops/{shopId:guid}/orders").WithTags("Merchant orders").RequirePlantMember();

        // Tabs like a merchant app: new | active (accepted/preparing) | ready (ready/out/delivered) | done
        shop.MapGet("/", async (Guid shopId, string? tab, DateOnly? date, IShopAccess access, ICurrentPlant plant, OrderingDbContext db,
            OrderActions actions, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Staff, ct);
            var query = db.Orders.AsNoTracking().Where(o => o.ShopId == shopId && o.PlantId == plant.PlantId);
            query = tab switch
            {
                "new" => query.Where(o => o.Status == OrderStatus.PendingAcceptance),
                "active" => query.Where(o => o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Preparing),
                "ready" => query.Where(o => o.Status == OrderStatus.Ready || o.Status == OrderStatus.OutForDelivery || o.Status == OrderStatus.Delivered),
                _ => query.Where(o => o.Status == OrderStatus.Completed || o.Status == OrderStatus.Cancelled
                                      || o.Status == OrderStatus.Rejected || o.Status == OrderStatus.Expired),
            };
            if (date is { } d)
            {
                var start = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime();
                query = query.Where(o => o.PlacedAt >= start && o.PlacedAt < start.AddDays(1));
            }
            var rows = tab is "new" or "active" or "ready"
                ? await query.OrderBy(o => o.ScheduledFrom ?? o.PlacedAt).Take(200).ToListAsync(ct)
                : await query.OrderByDescending(o => o.PlacedAt).Take(100).ToListAsync(ct);
            return await actions.SummariesAsync(rows, ct);
        });

        shop.MapGet("/counts", async (Guid shopId, IShopAccess access, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Staff, ct);
            var counts = await db.Orders.AsNoTracking().Where(o => o.ShopId == shopId && o.ClosedAt == null)
                .GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            int Of(params OrderStatus[] s) => counts.Where(c => s.Contains(c.Key)).Sum(c => c.Count);
            var since = clock.GetUtcNow().AddHours(-24);
            var today = await db.Orders.CountAsync(o => o.ShopId == shopId && o.PlacedAt >= since, ct);
            return new OrderCountsDto(Of(OrderStatus.PendingAcceptance), Of(OrderStatus.Accepted, OrderStatus.Preparing),
                Of(OrderStatus.Ready, OrderStatus.OutForDelivery, OrderStatus.Delivered), today);
        });

        // "What do I need to cook?" - totals across all accepted, not yet ready orders (great for pre-order rounds).
        shop.MapGet("/prep-summary", async (Guid shopId, Guid? roundId, IShopAccess access, OrderingDbContext db, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Staff, ct);
            var query = db.Orders.AsNoTracking().Where(o => o.ShopId == shopId &&
                (o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Preparing || o.Status == OrderStatus.PendingAcceptance));
            if (roundId is { } r) query = query.Where(o => o.PreOrderRoundId == r);
            var orders = await query.ToListAsync(ct);
            return orders.SelectMany(o => o.Lines.Select(l => (o.Id, Line: l)))
                .GroupBy(x => (x.Line.ItemId, x.Line.Name, Options: string.Join(", ", x.Line.Options.Select(op => op.Name))))
                .Select(g => new PrepItemDto(g.Key.ItemId, g.Key.Name, g.Key.Options, g.Sum(x => x.Line.Quantity), g.Select(x => x.Id).Distinct().Count()))
                .OrderByDescending(x => x.Quantity).ToList();
        });

        shop.MapGet("/report", async (Guid shopId, DateOnly? from, DateOnly? to, IShopAccess access, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Manager, ct);
            var (start, end, f, t) = Range(from, to, clock);
            var orders = await db.Orders.AsNoTracking().Where(o => o.ShopId == shopId && o.PlacedAt >= start && o.PlacedAt < end).ToListAsync(ct);
            // Delivered orders are sales already (they auto-complete after AutoCompleteHours); counting only Completed
            // left the report empty while customers had not pressed "received" yet.
            var completed = orders.Where(o => o.Status is OrderStatus.Completed or OrderStatus.Delivered).ToList();
            var revenue = completed.Sum(o => o.Total);
            var bangkok = TimeSpan.FromHours(7);
            var days = completed.GroupBy(o => DateOnly.FromDateTime(o.PlacedAt.ToOffset(bangkok).DateTime))
                .Select(g => new SalesDayDto(g.Key, g.Count(), g.Sum(o => o.Total))).OrderBy(d => d.Day).ToList();
            var top = completed.SelectMany(o => o.Lines).GroupBy(l => l.Name)
                .Select(g => new TopItemDto(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal)))
                .OrderByDescending(x => x.Quantity).Take(10).ToList();
            return new SalesReportDto(f, t, completed.Count, revenue, completed.Count == 0 ? 0 : decimal.Round(revenue / completed.Count, 2),
                orders.Count(o => o.Status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired), days, top);
        });

        shop.MapGet("/export.csv", async (Guid shopId, DateOnly? from, DateOnly? to, IShopAccess access, OrderingDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            await access.RequireAsync(shopId, ShopRole.Manager, ct);
            var (start, end, f, t) = Range(from, to, clock);
            var orders = await db.Orders.AsNoTracking().Where(o => o.ShopId == shopId && o.PlacedAt >= start && o.PlacedAt < end)
                .OrderBy(o => o.PlacedAt).ToListAsync(ct);
            var csv = new System.Text.StringBuilder("﻿order_no,placed_at,status,fulfillment,items,subtotal,discount,total,payment_method,payment_status\n");
            foreach (var o in orders)
                csv.AppendLine(string.Join(',', o.OrderNo, o.PlacedAt.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm"), o.Status, o.FulfillmentType,
                    Csv(string.Join("; ", o.Lines.Select(l => $"{l.Name} x{l.Quantity}"))), o.Subtotal, o.Discount, o.Total, Csv(o.PaymentMethodName), o.PaymentStatus));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"orders-{f:yyyyMMdd}-{t:yyyyMMdd}.csv");
        });

        // ---- state changes ----
        var o = api.MapGroup("/merchant/orders/{id:guid}").WithTags("Merchant orders").RequirePlantMember();

        o.MapPost("/accept", async (Guid id, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.Accept(a.UserId, a.Now);
            await a.SaveAsync(ct, new OrderAccepted(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, a.UserId));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/reject", async (Guid id, ReasonRequest req, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.Reject(a.UserId, req.Reason ?? "", a.Now);
            await a.SaveAsync(ct, new OrderRejected(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, a.UserId,
                order.CancelReason!, order.PromotionId, order.LineInfos()));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/start", async (Guid id, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            var shopInfo = await a.ShopAsync(order.ShopId, ct);
            if (shopInfo?.RequirePaymentBeforePreparing == true && order.PaymentMethodType != PaymentMethodType.Cash && order.PaymentStatus != PaymentStatus.Paid)
                throw new DomainException("payment_required", "This shop requires payment to be verified before preparing.");
            order.StartPreparing(a.UserId, a.Now);
            await a.SaveAsync(ct, new OrderPreparing(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/ready", async (Guid id, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.MarkReady(a.UserId, a.Now);
            await a.SaveAsync(ct, new OrderReady(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, order.FulfillmentType));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/dispatch", async (Guid id, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.Dispatch(a.UserId, a.Now);
            await a.SaveAsync(ct, new OrderOutForDelivery(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/deliver", async (Guid id, DeliverRequest req, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            var photo = await a.ValidatePhotoAsync(req.PhotoId, ct);
            var hours = (await a.ShopAsync(order.ShopId, ct))?.AutoCompleteHours ?? 12;
            order.MarkDelivered(a.UserId, photo, hours, a.Now);
            await a.Bus.ScheduleAsync(new AutoCompleteOrder(order.Id), order.AutoCompleteAt!.Value);
            await a.SaveAsync(ct, new OrderDelivered(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, photo, hours));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        // Owner/manager: close an order that is stuck in the flow, as completed (goods handed over) or cancelled.
        o.MapPost("/force-close", async (Guid id, ForceCloseRequest req, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Manager, ct);
            if (req.Outcome == "completed")
            {
                order.ForceComplete(a.UserId, req.Reason ?? "", a.Now);
                await a.SaveAsync(ct, new OrderCompleted(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, true,
                    order.Total, order.LineInfos()));
            }
            else
            {
                order.ForceCancel(a.UserId, req.Reason ?? "", a.Now);
                await a.SaveAsync(ct, new OrderCancelled(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, a.UserId, false,
                    order.CancelReason, order.PromotionId, order.LineInfos()));
            }
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/cancel", async (Guid id, ReasonRequest req, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.CancelByShop(a.UserId, req.Reason ?? order.CancelRequestReason ?? "", a.Now);
            await a.SaveAsync(ct, new OrderCancelled(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.ShopName, order.CustomerId, a.UserId, false,
                order.CancelReason, order.PromotionId, order.LineInfos()));
            return await a.ToDtoAsync(order, "shop", ct);
        });

        o.MapPost("/decline-cancel", async (Guid id, ReasonRequest req, OrderActions a, CancellationToken ct) =>
        {
            var order = await a.ForShopAsync(id, ShopRole.Staff, ct);
            order.DeclineCancelRequest(a.UserId, req.Reason, a.Now);
            await a.SaveAsync(ct);
            return await a.ToDtoAsync(order, "shop", ct);
        });
    }

    private static (DateTimeOffset Start, DateTimeOffset End, DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, TimeProvider clock)
    {
        var bangkok = TimeSpan.FromHours(7);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(bangkok).DateTime);
        var t = to ?? today;
        var f = from ?? t.AddDays(-6);
        if (f > t) (f, t) = (t, f);
        if (t.DayNumber - f.DayNumber > 366) f = t.AddDays(-366);
        return (new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), bangkok).ToUniversalTime(),
            new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), bangkok).ToUniversalTime(), f, t);
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
