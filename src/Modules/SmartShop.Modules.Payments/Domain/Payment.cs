using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Shops;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Payments.Domain;

/// <summary>Payment of one order. Created when the order is placed, with a snapshot of the chosen method.</summary>
public sealed class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string OrderNo { get; private set; } = "";
    public Guid PlantId { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentMethodType MethodType { get; private set; }
    public MethodSnapshot Method { get; private set; } = new();
    public PaymentStatus Status { get; private set; }
    public string? RejectReason { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public bool RefundRequired { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public List<PaymentProof> Proofs { get; private set; } = [];
    public uint Version { get; private set; }

    public static Payment Create(Guid orderId, string orderNo, Guid plantId, Guid shopId, Guid customerId, decimal amount,
        PaymentMethodInfo method, DateTimeOffset now) => new()
        {
            Id = Ids.New(),
            OrderId = orderId,
            OrderNo = orderNo,
            PlantId = plantId,
            ShopId = shopId,
            CustomerId = customerId,
            Amount = amount,
            MethodType = method.Type,
            Method = new MethodSnapshot
            {
                DisplayName = method.DisplayName,
                PromptPayId = method.PromptPayId,
                QrImageId = method.QrImageId,
                BankName = method.BankName,
                AccountNumber = method.AccountNumber,
                AccountName = method.AccountName,
                Instructions = method.Instructions,
                ImageId = method.ImageId,
                RequiresProof = method.RequiresProof,
            },
            Status = amount == 0 ? PaymentStatus.Paid : PaymentStatus.Unpaid,
            CreatedAt = now,
        };

    public PaymentProof AddProof(Guid mediaId, string sha256, string? slipReference, DateTimeOffset now)
    {
        if (MethodType == PaymentMethodType.Cash) throw new DomainException("proof_not_needed", "Cash payments do not need a slip.");
        if (Status is PaymentStatus.Paid or PaymentStatus.Voided)
            throw new ConflictException("payment_closed", "This payment is already settled.");
        if (Proofs.Count >= 5) throw new DomainException("too_many_proofs", "At most 5 attachments per payment.");
        var proof = new PaymentProof(Id, mediaId, sha256, slipReference, now);
        Proofs.Add(proof);
        Status = PaymentStatus.PendingVerification;
        RejectReason = null;
        return proof;
    }

    public void Verify(Guid actorId, DateTimeOffset now)
    {
        if (Status is PaymentStatus.Paid or PaymentStatus.Voided) throw new ConflictException("payment_closed", "This payment is already settled.");
        if (MethodType != PaymentMethodType.Cash && Method.RequiresProof && Proofs.Count == 0 && Status != PaymentStatus.PendingVerification)
            throw new DomainException("proof_missing", "The customer has not attached a payment slip yet.");
        Status = PaymentStatus.Paid;
        VerifiedBy = actorId;
        VerifiedAt = now;
    }

    /// <summary>Shop confirms it received cash (or any payment) directly.</summary>
    public void MarkReceived(Guid actorId, DateTimeOffset now)
    {
        if (Status is PaymentStatus.Paid or PaymentStatus.Voided) throw new ConflictException("payment_closed", "This payment is already settled.");
        Status = PaymentStatus.Paid;
        VerifiedBy = actorId;
        VerifiedAt = now;
    }

    public void Reject(string reason)
    {
        if (Status != PaymentStatus.PendingVerification) throw new ConflictException("payment_state", "There is no slip waiting for verification.");
        Status = PaymentStatus.Rejected;
        RejectReason = Guard.NotEmpty(reason, "Reason", 300);
    }

    /// <summary>The order did not happen. Paid payments stay paid but are flagged for a manual refund by the shop.</summary>
    public void Void()
    {
        if (Status == PaymentStatus.Paid) RefundRequired = true;
        else Status = PaymentStatus.Voided;
    }
}

public sealed class MethodSnapshot
{
    public string DisplayName { get; set; } = "";
    public string? PromptPayId { get; set; }
    public Guid? QrImageId { get; set; }
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountName { get; set; }
    public string? Instructions { get; set; }
    public Guid? ImageId { get; set; }
    public bool RequiresProof { get; set; }
}

public sealed class PaymentProof
{
    private PaymentProof() { }

    public PaymentProof(Guid paymentId, Guid mediaId, string sha256, string? slipReference, DateTimeOffset now)
    {
        Id = Ids.New();
        PaymentId = paymentId;
        MediaId = mediaId;
        Sha256 = sha256;
        SlipReference = slipReference;
        UploadedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid MediaId { get; private set; }
    public string Sha256 { get; private set; } = "";

    /// <summary>Transaction reference read from the QR code printed on Thai bank slips (when readable).</summary>
    public string? SlipReference { get; private set; }

    public Guid? DuplicateOfPaymentId { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>Verdict of the external slip verifier (null = not checked).</summary>
    public bool? SystemVerified { get; private set; }
    public string? VerificationMessage { get; private set; }
    public DateTimeOffset? SystemVerifiedAt { get; private set; }

    public void MarkDuplicateOf(Guid paymentId) => DuplicateOfPaymentId = paymentId;

    public void RecordVerification(bool valid, string? message, DateTimeOffset now)
    {
        SystemVerified = valid;
        VerificationMessage = message is { Length: > 300 } ? message[..300] : message;
        SystemVerifiedAt = now;
    }
}
