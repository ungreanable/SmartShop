namespace SmartShop.Contracts.Payments;

public enum PaymentStatus { Unpaid, PendingVerification, Paid, Rejected, Voided }

public interface IPaymentDirectory
{
    Task<PaymentStatus?> GetStatusAsync(Guid orderId, CancellationToken ct = default);
}

public sealed record PaymentCreated(Guid PlantId, Guid PaymentId, Guid OrderId, Guid ShopId, Guid CustomerId) : IntegrationEvent(PlantId);
public sealed record PaymentProofUploaded(Guid PlantId, Guid PaymentId, Guid OrderId, string OrderNo, Guid ShopId, Guid CustomerId) : IntegrationEvent(PlantId);
public sealed record PaymentVerified(Guid PlantId, Guid PaymentId, Guid OrderId, string OrderNo, Guid ShopId, Guid CustomerId, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record PaymentRejected(Guid PlantId, Guid PaymentId, Guid OrderId, string OrderNo, Guid ShopId, Guid CustomerId, Guid ActorId, string Reason) : IntegrationEvent(PlantId);
public sealed record PaymentDuplicateSlipDetected(Guid PlantId, Guid PaymentId, Guid OrderId, string OrderNo, Guid ShopId, Guid DuplicateOfOrderId) : IntegrationEvent(PlantId);
