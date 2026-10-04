using SmartShop.Contracts.Plants;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Plants.Domain;

/// <summary>
/// A user's membership in a plant. Lifecycle: Pending -> Active -> Suspended/Left, or Pending -> Rejected.
/// Only Active members can see shops or order.
/// </summary>
public sealed class Membership
{
    private Membership() { }

    public Guid Id { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid UserId { get; private set; }
    public PlantRole Role { get; private set; }
    public MembershipStatus Status { get; private set; }
    public string? HouseNo { get; private set; }
    public string? Soi { get; private set; }
    public string? Nickname { get; private set; }
    public string? RequestMessage { get; private set; }
    public string? DecisionReason { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public uint Version { get; private set; }

    public static Membership Request(Guid plantId, Guid userId, string? houseNo, string? soi, string? nickname, string? message, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        PlantId = plantId,
        UserId = userId,
        Role = PlantRole.Member,
        Status = MembershipStatus.Pending,
        HouseNo = Guard.NotEmpty(houseNo, "House number", 30),
        Soi = Guard.Optional(soi, "Soi", 60),
        Nickname = Guard.Optional(nickname, "Nickname", 60),
        RequestMessage = Guard.Optional(message, "Message", 500),
        RequestedAt = now,
    };

    /// <summary>Plant founders/appointed admins skip the queue.</summary>
    public static Membership CreateAdmin(Guid plantId, Guid userId, Guid actorId, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        PlantId = plantId,
        UserId = userId,
        Role = PlantRole.PlantAdmin,
        Status = MembershipStatus.Active,
        RequestedAt = now,
        ReviewedAt = now,
        ReviewedBy = actorId,
    };

    /// <summary>Someone who was rejected or left may ask again; a suspended member may not.</summary>
    public void RequestAgain(string? houseNo, string? soi, string? nickname, string? message, DateTimeOffset now)
    {
        switch (Status)
        {
            case MembershipStatus.Active:
                throw new ConflictException("already_member", "You are already a member of this village.");
            case MembershipStatus.Pending:
                throw new ConflictException("already_requested", "Your request is waiting for approval.");
            case MembershipStatus.Suspended:
                throw new DomainException("membership_suspended", "Your membership is suspended. Please contact the village administrator.");
        }
        Status = MembershipStatus.Pending;
        Role = PlantRole.Member;
        HouseNo = Guard.NotEmpty(houseNo, "House number", 30);
        Soi = Guard.Optional(soi, "Soi", 60);
        Nickname = Guard.Optional(nickname, "Nickname", 60);
        RequestMessage = Guard.Optional(message, "Message", 500);
        DecisionReason = null;
        RequestedAt = now;
    }

    public void Approve(Guid actorId, DateTimeOffset now)
    {
        Require(MembershipStatus.Pending, "Only pending requests can be approved.");
        Decide(MembershipStatus.Active, actorId, null, now);
    }

    public void Reject(Guid actorId, string? reason, DateTimeOffset now)
    {
        Require(MembershipStatus.Pending, "Only pending requests can be rejected.");
        Decide(MembershipStatus.Rejected, actorId, reason, now);
    }

    public void Suspend(Guid actorId, string? reason, DateTimeOffset now)
    {
        Require(MembershipStatus.Active, "Only active members can be suspended.");
        Decide(MembershipStatus.Suspended, actorId, reason, now);
    }

    public void Reinstate(Guid actorId, DateTimeOffset now)
    {
        Require(MembershipStatus.Suspended, "Only suspended members can be reinstated.");
        Decide(MembershipStatus.Active, actorId, null, now);
    }

    public void Leave()
    {
        Require(MembershipStatus.Active, "Only active members can leave.");
        Status = MembershipStatus.Left;
        Role = PlantRole.Member;
    }

    public void ChangeRole(PlantRole role)
    {
        Require(MembershipStatus.Active, "Only active members can change role.");
        Role = role;
    }

    public void PromoteToAdmin(Guid actorId, DateTimeOffset now)
    {
        if (Status != MembershipStatus.Active) Decide(MembershipStatus.Active, actorId, null, now);
        Role = PlantRole.PlantAdmin;
    }

    public void UpdateAddress(string? houseNo, string? soi, string? nickname)
    {
        HouseNo = Guard.NotEmpty(houseNo, "House number", 30);
        Soi = Guard.Optional(soi, "Soi", 60);
        Nickname = Guard.Optional(nickname, "Nickname", 60);
    }

    public void Anonymise()
    {
        HouseNo = null;
        Soi = null;
        Nickname = null;
        RequestMessage = null;
        if (Status is MembershipStatus.Active or MembershipStatus.Pending) Status = MembershipStatus.Left;
    }

    private void Decide(MembershipStatus status, Guid actorId, string? reason, DateTimeOffset now)
    {
        Status = status;
        ReviewedBy = actorId;
        ReviewedAt = now;
        DecisionReason = Guard.Optional(reason, "Reason", 500);
    }

    private void Require(MembershipStatus expected, string message)
    {
        if (Status != expected) throw new ConflictException("invalid_membership_state", message);
    }

    public MembershipInfo ToInfo() => new(Id, PlantId, UserId, Role, Status, HouseNo, Soi, Nickname);
}
