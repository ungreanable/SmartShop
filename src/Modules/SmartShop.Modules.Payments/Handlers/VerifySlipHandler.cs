using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartShop.Contracts.Payments;
using SmartShop.Modules.Payments.Data;
using SmartShop.Modules.Payments.Domain;
using SmartShop.Modules.Payments.Services;
using Wolverine;

namespace SmartShop.Modules.Payments.Handlers;

/// <summary>
/// Records the external verifier's verdict on the slip. With <see cref="SlipVerifierOptions.AutoConfirm"/> a confirmed
/// slip also settles the payment, so the shop does not have to check the bank app for every order.
/// </summary>
public static class VerifySlipHandler
{
    public static async Task Handle(VerifySlip command, PaymentsDbContext db, ISlipVerifier verifier, IOptions<SlipVerifierOptions> options,
        IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var proof = await db.Proofs.FirstOrDefaultAsync(p => p.Id == command.ProofId, ct);
        if (proof?.SlipReference is null || proof.SystemVerified is not null) return;
        var payment = await db.Payments.Include(p => p.Proofs).FirstOrDefaultAsync(p => p.Id == proof.PaymentId, ct);
        if (payment is null || payment.Status is PaymentStatus.Paid or PaymentStatus.Voided) return;

        var result = await verifier.VerifyAsync(proof.SlipReference, payment.Amount, ct);
        if (!result.Checked) return;
        var now = clock.GetUtcNow();
        proof.RecordVerification(result.Valid, result.Message, now);

        if (result.Valid && options.Value.AutoConfirm)
        {
            payment.Verify(Guid.Empty, now);
            await bus.PublishAsync(new PaymentVerified(payment.PlantId, payment.Id, payment.OrderId, payment.OrderNo, payment.ShopId, payment.CustomerId, Guid.Empty));
        }
    }
}
