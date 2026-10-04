namespace SmartShop.Contracts.Plants;

public enum PlantRole { Member, PlantAdmin }

public enum MembershipStatus { Pending, Active, Rejected, Suspended, Left }

public sealed record MembershipInfo(
    Guid MembershipId, Guid PlantId, Guid UserId, PlantRole Role, MembershipStatus Status,
    string? HouseNo, string? Soi, string? Nickname);

public sealed record PlantInfo(Guid Id, string Name, string TimeZoneId, PushRequirement PushRequirement);

/// <summary>What happens when a shop wants to open but none of its members can receive push notifications.</summary>
public enum PushRequirement { Warn, Block }

public interface IPlantDirectory
{
    Task<MembershipInfo?> GetMembershipAsync(Guid plantId, Guid userId, CancellationToken ct = default);
    Task<PlantInfo?> GetPlantAsync(Guid plantId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetActiveAdminIdsAsync(Guid plantId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetActiveMemberIdsAsync(Guid plantId, CancellationToken ct = default);
}

public sealed record PlantCreated(Guid PlantId, string Name) : IntegrationEvent(PlantId);
public sealed record MembershipRequested(Guid PlantId, Guid MembershipId, Guid UserId, string? Nickname, string? HouseNo) : IntegrationEvent(PlantId);
public sealed record MembershipApproved(Guid PlantId, Guid MembershipId, Guid UserId, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record MembershipRejected(Guid PlantId, Guid MembershipId, Guid UserId, Guid ActorId, string? Reason) : IntegrationEvent(PlantId);
public sealed record MembershipSuspended(Guid PlantId, Guid MembershipId, Guid UserId, Guid ActorId, string? Reason) : IntegrationEvent(PlantId);
public sealed record MembershipReinstated(Guid PlantId, Guid MembershipId, Guid UserId, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record MembershipLeft(Guid PlantId, Guid MembershipId, Guid UserId) : IntegrationEvent(PlantId);
public sealed record MembershipRoleChanged(Guid PlantId, Guid UserId, PlantRole Role, Guid ActorId) : IntegrationEvent(PlantId);
public sealed record AnnouncementPublished(Guid PlantId, Guid AnnouncementId, string Title, bool Notify, Guid ActorId) : IntegrationEvent(PlantId);
