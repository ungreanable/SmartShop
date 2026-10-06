namespace SmartShop.Contracts.Identity;

public sealed record UserProfile(Guid Id, string DisplayName, string? PictureUrl, string? Phone);

/// <summary>Read-only access to users for other modules (display names, LINE ids for push).</summary>
public interface IUserDirectory
{
    Task<IReadOnlyDictionary<Guid, UserProfile>> GetProfilesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, string>> GetLineUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
    Task<Guid?> FindByLineUserIdAsync(string lineUserId, CancellationToken ct = default);

    /// <summary>Users among <paramref name="withinUserIds"/> whose display name contains <paramref name="text"/> (case-insensitive).</summary>
    Task<IReadOnlyList<Guid>> SearchByNameAsync(string text, IEnumerable<Guid> withinUserIds, CancellationToken ct = default);
}

public sealed record UserRegistered(Guid UserId, string DisplayName, string Provider) : IntegrationEvent(Guid.Empty);

/// <summary>PDPA erasure: personal data must be removed or anonymised by every module.</summary>
public sealed record UserErased(Guid UserId) : IntegrationEvent(Guid.Empty);
