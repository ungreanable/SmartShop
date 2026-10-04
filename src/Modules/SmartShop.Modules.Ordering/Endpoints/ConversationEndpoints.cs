using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Ordering;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Ordering.Data;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.Modules.Ordering.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Ordering.Endpoints;

public sealed record MessageRequest(string? Body, Guid? ImageId);
public sealed record MessageDto(Guid Id, Guid SenderId, string SenderName, bool FromShop, string? Body, string? ImageUrl, DateTimeOffset SentAt);
public sealed record ReportRequest(string Reason);
public sealed record ResolveRequest(string Resolution);
public sealed record ReportDto(Guid Id, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid ReporterId, string ReporterName,
    string Reason, ReportStatus Status, string? Resolution, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

internal static class ConversationEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var chat = api.MapGroup("/orders/{id:guid}/messages").WithTags("Order chat").RequirePlantMember();

        chat.MapGet("/", async (Guid id, OrderActions a, IUserDirectory users, IMediaUrls media, CancellationToken ct) =>
        {
            var (order, _) = await a.ForViewerAsync(id, ct);
            var messages = await a.Db.Messages.AsNoTracking().Where(m => m.OrderId == order.Id).OrderBy(m => m.SentAt).Take(500).ToListAsync(ct);
            var people = await users.GetProfilesAsync(messages.Select(m => m.SenderId), ct);
            return messages.Select(m => new MessageDto(m.Id, m.SenderId, people.GetValueOrDefault(m.SenderId)?.DisplayName ?? "?", m.FromShop,
                m.Body, media.For(m.ImageId), m.SentAt)).ToList();
        });

        chat.MapPost("/", async (Guid id, MessageRequest req, OrderActions a, IMediaService mediaService, IUserDirectory users, IMediaUrls media, CancellationToken ct) =>
        {
            var (order, role) = await a.ForViewerAsync(id, ct);
            if (role == "admin") throw new ForbiddenException("Administrators can read but not write in order chats.");
            if (order.ClosedAt is { } closed && a.Now - closed > TimeSpan.FromDays(7))
                throw new DomainException("chat_closed", "Chat closes 7 days after the order ended.");
            if (req.ImageId is { } image) await mediaService.RequireOwnedAsync(image, a.UserId, MediaPurpose.ChatImage, ct);

            var message = new OrderMessage(order.Id, a.UserId, role == "shop", req.Body, req.ImageId, a.Now);
            a.Db.Messages.Add(message);
            var preview = message.Body is { } b ? (b.Length > 80 ? b[..80] + "…" : b) : "📷";
            await a.SaveAsync(ct, new OrderMessagePosted(order.PlantId, order.Id, order.OrderNo, order.ShopId, order.CustomerId, a.UserId, role == "shop", preview));
            var me = (await users.GetProfilesAsync([a.UserId], ct)).GetValueOrDefault(a.UserId);
            return new MessageDto(message.Id, a.UserId, me?.DisplayName ?? "?", message.FromShop, message.Body, media.For(message.ImageId), message.SentAt);
        });

        api.MapPost("/orders/{id:guid}/report", async (Guid id, ReportRequest req, OrderActions a, CancellationToken ct) =>
        {
            var (order, role) = await a.ForViewerAsync(id, ct);
            if (role == "admin") throw new ForbiddenException();
            if (await a.Db.Reports.AnyAsync(r => r.OrderId == id && r.ReporterId == a.UserId && r.Status == ReportStatus.Open, ct))
                throw new ConflictException("report_open", "You already reported this order.");
            var report = new OrderReport(order.Id, order.PlantId, order.ShopId, a.UserId, req.Reason, a.Now);
            a.Db.Reports.Add(report);
            await a.SaveAsync(ct, new OrderReported(order.PlantId, order.Id, order.OrderNo, report.Id, a.UserId, report.Reason));
            return Results.Accepted();
        }).WithTags("Orders").RequirePlantMember();

        var admin = api.MapGroup("/plant/admin/reports").WithTags("Village admin").RequirePlantAdmin();

        admin.MapGet("/", async (ReportStatus? status, ICurrentPlant plant, OrderingDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var query = db.Reports.AsNoTracking().Where(r => r.PlantId == plant.PlantId);
            if (status is { } s) query = query.Where(r => r.Status == s);
            var reports = await query.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
            var orderIds = reports.Select(r => r.OrderId).ToList();
            var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
            var people = await users.GetProfilesAsync(reports.Select(r => r.ReporterId), ct);
            return reports.Select(r => new ReportDto(r.Id, r.OrderId, orders.GetValueOrDefault(r.OrderId)?.OrderNo ?? "?", r.ShopId,
                orders.GetValueOrDefault(r.OrderId)?.ShopName ?? "?", r.ReporterId, people.GetValueOrDefault(r.ReporterId)?.DisplayName ?? "?",
                r.Reason, r.Status, r.Resolution, r.CreatedAt, r.ResolvedAt)).ToList();
        });

        admin.MapPost("/{reportId:guid}/resolve", async (Guid reportId, ResolveRequest req, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<OrderingDbContext> outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var report = await outbox.DbContext.Reports.FirstOrDefaultAsync(r => r.Id == reportId && r.PlantId == plant.PlantId, ct)
                         ?? throw new NotFoundException("Report", reportId);
            report.Resolve(user.Id, req.Resolution, clock.GetUtcNow());
            await outbox.PublishAsync(new OrderReportResolved(report.PlantId, report.OrderId, report.Id, report.ReporterId, report.Resolution!));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return Results.NoContent();
        });
    }
}
