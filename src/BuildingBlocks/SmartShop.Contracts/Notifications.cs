namespace SmartShop.Contracts.Notifications;

public sealed record ChannelStatus(Guid UserId, bool LineFriend, int WebPushDevices, int MobileDevices)
{
    public bool HasPush => LineFriend || WebPushDevices > 0 || MobileDevices > 0;
}

public interface INotificationChannelDirectory
{
    Task<IReadOnlyList<ChannelStatus>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
}

public sealed record LineFriendshipChanged(Guid UserId, bool IsFriend) : IntegrationEvent(Guid.Empty);
