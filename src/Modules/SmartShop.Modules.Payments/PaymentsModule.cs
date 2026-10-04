using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Payments.Data;
using SmartShop.Modules.Payments.Domain;
using SmartShop.Modules.Payments.Services;
using SmartShop.SharedKernel;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Payments;

public sealed record ProofDto(Guid Id, string? Url, string? ThumbnailUrl, string ContentType, DateTimeOffset UploadedAt, string? DuplicateOfOrderNo, bool HasSlipReference);
public sealed record PaymentDto(
    Guid Id, Guid OrderId, string OrderNo, decimal Amount, PaymentMethodType MethodType, string DisplayName,
    string? PromptPayId, string? PromptPayQrDataUrl, string? QrImageUrl, string? BankName, string? AccountNumber, string? AccountName,
    string? Instructions, string? ImageUrl, bool RequiresProof, PaymentStatus Status, string? RejectReason, DateTimeOffset? VerifiedAt,
    bool RefundRequired, List<ProofDto> Proofs, bool CanUploadProof, bool CanVerify);
public sealed record ProofRequest(Guid MediaId);
public sealed record RejectPaymentRequest(string Reason);

public sealed class PaymentsModule : IModule
{
    public string Name => "Payments";

    public IReadOnlyList<Type> DbContexts => [typeof(PaymentsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<PaymentsDbContext>(PaymentsDbContext.SchemaName);
        builder.Services.AddScoped<IPaymentDirectory, PaymentDirectory>();
        builder.Services.AddSingleton<ISlipVerifier, ManualSlipVerifier>();
        builder.Services.AddScoped<Access>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var customer = app.MapGroup("/orders/{orderId:guid}/payment").WithTags("Payments").RequirePlantMember();

        customer.MapGet("/", async (Guid orderId, Access access, IMediaUrls media, IMediaService mediaService, CancellationToken ct) =>
        {
            var (payment, role) = await access.LoadAsync(orderId, ct);
            return await ToDtoAsync(payment, role, access.Db, media, mediaService, ct);
        });

        customer.MapPost("/proofs", async (Guid orderId, ProofRequest req, Access access, IMediaService mediaService, IMediaUrls media,
            IDbContextOutbox<PaymentsDbContext> outbox, TimeProvider clock, CancellationToken ct) =>
        {
            var (payment, role) = await access.LoadAsync(orderId, ct, tracked: true);
            if (role != "customer") throw new ForbiddenException("Only the customer attaches payment slips.");
            var file = await mediaService.RequireOwnedAsync(req.MediaId, payment.CustomerId, MediaPurpose.PaymentSlip, ct);

            string? reference = null;
            if (file.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                await using var stream = await mediaService.OpenOriginalAsync(file.Id, ct);
                if (stream is not null) reference = SlipReader.TryReadReference(stream);
            }

            var proof = payment.AddProof(file.Id, file.Sha256, reference, clock.GetUtcNow());
            await outbox.PublishAsync(new PaymentProofUploaded(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, payment.CustomerId));

            // Same image or same bank transaction already used for another order?
            var duplicate = await outbox.DbContext.Proofs.AsNoTracking()
                .Where(p => p.PaymentId != payment.Id && (p.Sha256 == file.Sha256 || (reference != null && p.SlipReference == reference)))
                .Select(p => (Guid?)p.PaymentId).FirstOrDefaultAsync(ct);
            if (duplicate is { } other)
            {
                proof.MarkDuplicateOf(other);
                var otherOrder = await outbox.DbContext.Payments.AsNoTracking().Where(p => p.Id == other).Select(p => p.OrderId).FirstAsync(ct);
                await outbox.PublishAsync(new PaymentDuplicateSlipDetected(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, otherOrder));
            }

            outbox.DbContext.Proofs.Add(proof);
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return await ToDtoAsync(payment, role, outbox.DbContext, media, mediaService, ct);
        });

        var merchant = app.MapGroup("/merchant/orders/{orderId:guid}/payment").WithTags("Payments").RequirePlantMember();

        merchant.MapPost("/verify", async (Guid orderId, Access access, IDbContextOutbox<PaymentsDbContext> outbox, ICurrentUser user,
            IMediaUrls media, IMediaService mediaService, TimeProvider clock, CancellationToken ct) =>
        {
            var payment = await access.LoadForShopAsync(orderId, ct);
            payment.Verify(user.Id, clock.GetUtcNow());
            await outbox.PublishAsync(new PaymentVerified(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, payment.CustomerId, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return await ToDtoAsync(payment, "shop", outbox.DbContext, media, mediaService, ct);
        });

        merchant.MapPost("/received", async (Guid orderId, Access access, IDbContextOutbox<PaymentsDbContext> outbox, ICurrentUser user,
            IMediaUrls media, IMediaService mediaService, TimeProvider clock, CancellationToken ct) =>
        {
            var payment = await access.LoadForShopAsync(orderId, ct);
            payment.MarkReceived(user.Id, clock.GetUtcNow());
            await outbox.PublishAsync(new PaymentVerified(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, payment.CustomerId, user.Id));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return await ToDtoAsync(payment, "shop", outbox.DbContext, media, mediaService, ct);
        });

        merchant.MapPost("/reject", async (Guid orderId, RejectPaymentRequest req, Access access, IDbContextOutbox<PaymentsDbContext> outbox,
            ICurrentUser user, IMediaUrls media, IMediaService mediaService, CancellationToken ct) =>
        {
            var payment = await access.LoadForShopAsync(orderId, ct);
            payment.Reject(req.Reason);
            await outbox.PublishAsync(new PaymentRejected(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, payment.CustomerId, user.Id, req.Reason));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            return await ToDtoAsync(payment, "shop", outbox.DbContext, media, mediaService, ct);
        });
    }

    private static async Task<PaymentDto> ToDtoAsync(Payment p, string role, PaymentsDbContext db, IMediaUrls media, IMediaService mediaService, CancellationToken ct)
    {
        var duplicateIds = p.Proofs.Where(x => x.DuplicateOfPaymentId is not null).Select(x => x.DuplicateOfPaymentId!.Value).ToList();
        var duplicateOrders = await db.Payments.AsNoTracking().Where(x => duplicateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.OrderNo, ct);
        var proofs = new List<ProofDto>();
        foreach (var proof in p.Proofs.OrderBy(x => x.UploadedAt))
        {
            var info = await mediaService.GetAsync(proof.MediaId, ct);
            var isImage = info?.ContentType.StartsWith("image/", StringComparison.Ordinal) == true;
            proofs.Add(new ProofDto(proof.Id, media.For(proof.MediaId, MediaVariant.Original), isImage ? media.For(proof.MediaId, MediaVariant.Small) : null,
                info?.ContentType ?? "application/octet-stream", proof.UploadedAt,
                proof.DuplicateOfPaymentId is { } d ? duplicateOrders.GetValueOrDefault(d) : null, proof.SlipReference is not null));
        }

        string? qr = null;
        if (p.MethodType == PaymentMethodType.PromptPayQr && p.Method.PromptPayId is { } id && p.Status is not (PaymentStatus.Paid or PaymentStatus.Voided))
            qr = PromptPay.PngDataUrl(PromptPay.Payload(id, p.Amount));

        var open = p.Status is PaymentStatus.Unpaid or PaymentStatus.PendingVerification or PaymentStatus.Rejected;
        return new PaymentDto(p.Id, p.OrderId, p.OrderNo, p.Amount, p.MethodType, p.Method.DisplayName, p.Method.PromptPayId, qr,
            media.For(p.Method.QrImageId), p.Method.BankName, p.Method.AccountNumber, p.Method.AccountName, p.Method.Instructions,
            media.For(p.Method.ImageId), p.Method.RequiresProof, p.Status, p.RejectReason, p.VerifiedAt, p.RefundRequired, proofs,
            role == "customer" && open && p.MethodType != PaymentMethodType.Cash,
            role == "shop" && open);
    }

    /// <summary>Resolves the payment of an order and the caller's relation to it.</summary>
    internal sealed class Access(ICurrentPlant plant, ICurrentUser user, IShopAccess shopAccess, IOrderDirectory orders, PaymentsDbContext db)
    {
        public PaymentsDbContext Db => db;

        public async Task<(Payment Payment, string Role)> LoadAsync(Guid orderId, CancellationToken ct, bool tracked = false)
        {
            var order = await orders.GetAsync(orderId, ct);
            if (order is null || order.PlantId != plant.PlantId) throw new NotFoundException("Order", orderId);
            var role = order.CustomerId == user.Id ? "customer"
                : await shopAccess.GetRoleAsync(order.ShopId, ct) is not null ? "shop"
                : plant.Membership.Role == PlantRole.PlantAdmin ? "admin"
                : throw new NotFoundException("Order", orderId);
            var query = tracked ? db.Payments : db.Payments.AsNoTracking();
            var payment = await query.FirstOrDefaultAsync(p => p.OrderId == orderId, ct)
                          ?? throw new DomainException("payment_pending", "The payment is being prepared. Please try again in a moment.");
            return (payment, role);
        }

        public async Task<Payment> LoadForShopAsync(Guid orderId, CancellationToken ct)
        {
            var order = await orders.GetAsync(orderId, ct);
            if (order is null || order.PlantId != plant.PlantId) throw new NotFoundException("Order", orderId);
            await shopAccess.RequireAsync(order.ShopId, ShopRole.Staff, ct);
            return await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct) ?? throw new NotFoundException("Payment", orderId);
        }
    }
}

internal sealed class PaymentDirectory(PaymentsDbContext db) : IPaymentDirectory
{
    public async Task<PaymentStatus?> GetStatusAsync(Guid orderId, CancellationToken ct = default) =>
        await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).Select(p => (PaymentStatus?)p.Status).FirstOrDefaultAsync(ct);
}
