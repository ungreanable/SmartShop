using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Contracts.Shops;

/// <summary>Shop member roles. Numeric order matters: a higher value includes the permissions of lower ones.</summary>
public enum ShopRole { Staff = 1, Manager = 2, Owner = 3 }

public enum FulfillmentType { Pickup, Delivery }

public enum PaymentMethodType { Cash, PromptPayQr, BankTransfer, Custom }

public enum AcceptMode { Manual, Auto }

public sealed record PaymentMethodInfo(
    Guid Id, PaymentMethodType Type, string DisplayName,
    string? PromptPayId, Guid? QrImageId,
    string? BankName, string? AccountNumber, string? AccountName,
    string? Instructions, Guid? ImageId, bool RequiresProof);

/// <summary>Everything Ordering needs to validate and route a checkout.</summary>
public sealed record ShopOrderingInfo(
    Guid ShopId, Guid PlantId, string Name, string Code, bool IsActive,
    ShopScheduleSnapshot Schedule,
    AcceptMode AcceptMode, int AcceptTimeoutMinutes, int ReminderAfterMinutes, int AutoCompleteHours,
    bool AllowPreorderWhenClosed, int PrepTimeMinutes, int SlotIntervalMinutes,
    bool PickupEnabled, bool DeliveryEnabled, decimal? DeliveryMinOrder,
    bool RequirePaymentBeforePreparing,
    IReadOnlyList<PaymentMethodInfo> PaymentMethods);

public sealed record ShopMemberInfo(Guid UserId, ShopRole Role, bool ReceiveOrderNotifications, string? DisplayLabel);

/// <summary>Denormalisation source for the Search read model.</summary>
public sealed record ShopListingSnapshot(
    Guid ShopId, Guid PlantId, string Name, string? Description, string? Category,
    Guid? LogoId, Guid? CoverId, string? HouseNo,
    bool PickupEnabled, bool DeliveryEnabled, bool AllowPreorderWhenClosed, int PrepTimeMinutes,
    decimal RatingAverage, int RatingCount, bool IsActive,
    ShopScheduleSnapshot Schedule, DateTimeOffset CreatedAt);

public interface IShopDirectory
{
    Task<ShopRole?> GetRoleAsync(Guid shopId, Guid userId, CancellationToken ct = default);
    Task<ShopOrderingInfo?> GetOrderingInfoAsync(Guid shopId, CancellationToken ct = default);
    Task<IReadOnlyList<ShopMemberInfo>> GetMembersAsync(Guid shopId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetOrderNotificationRecipientsAsync(Guid shopId, CancellationToken ct = default);
    Task<ShopListingSnapshot?> GetListingSnapshotAsync(Guid shopId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetFavoritersAsync(Guid shopId, CancellationToken ct = default);
}

public sealed record ShopApplicationSubmitted(Guid PlantId, Guid ApplicationId, Guid ApplicantId, string ShopName) : IntegrationEvent(PlantId);
public sealed record ShopApplicationChangesRequested(Guid PlantId, Guid ApplicationId, Guid ApplicantId, string ShopName, string Note, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record ShopApplicationApproved(Guid PlantId, Guid ApplicationId, Guid ApplicantId, Guid ShopId, string ShopName, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record ShopApplicationRejected(Guid PlantId, Guid ApplicationId, Guid ApplicantId, string ShopName, string Reason, Guid ActorId) : IntegrationEvent(PlantId);

public sealed record ShopCreated(Guid PlantId, Guid ShopId, Guid OwnerId, string Name) : IntegrationEvent(PlantId);
public sealed record ShopProfileUpdated(Guid PlantId, Guid ShopId) : IntegrationEvent(PlantId);
public sealed record OpeningHoursChanged(Guid PlantId, Guid ShopId) : IntegrationEvent(PlantId);
public sealed record ShopStatusChanged(Guid PlantId, Guid ShopId, string ShopName, ShopOpenState State, bool BecameOpen) : IntegrationEvent(PlantId);
public sealed record ShopSuspended(Guid PlantId, Guid ShopId, Guid ActorId, string? Reason) : IntegrationEvent(PlantId);
public sealed record ShopReinstated(Guid PlantId, Guid ShopId, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record PaymentMethodsUpdated(Guid PlantId, Guid ShopId) : IntegrationEvent(PlantId);
public sealed record DeliveryOptionsUpdated(Guid PlantId, Guid ShopId) : IntegrationEvent(PlantId);
public sealed record ShopMemberAdded(Guid PlantId, Guid ShopId, Guid UserId, ShopRole Role, string ShopName) : IntegrationEvent(PlantId);
public sealed record ShopMemberRemoved(Guid PlantId, Guid ShopId, Guid UserId, string ShopName) : IntegrationEvent(PlantId);
public sealed record ShopMemberRoleChanged(Guid PlantId, Guid ShopId, Guid UserId, ShopRole Role) : IntegrationEvent(PlantId);
public sealed record ShopMemberNotificationToggled(Guid PlantId, Guid ShopId, Guid UserId, bool Enabled) : IntegrationEvent(PlantId);
public sealed record ShopOwnershipTransferred(Guid PlantId, Guid ShopId, Guid FromUserId, Guid ToUserId) : IntegrationEvent(PlantId);
public sealed record ShopRatingChanged(Guid PlantId, Guid ShopId, decimal Average, int Count) : IntegrationEvent(PlantId);

/// <summary>Re-evaluates a shop's open/closed status at a schedule boundary.</summary>
public sealed record EvaluateShopStatus(Guid ShopId) : IScheduledCommand;
