using SmartShop.SharedKernel;

namespace SmartShop.Modules.Notifications.Domain;

public enum NotificationPriority { Low, Medium, High }

public enum Channel { SignalR, Line, WebPush, Fcm }

public enum DeliveryStatus { Pending, Sent, Failed, Skipped }

/// <summary>In-app inbox entry. Always written, whatever happens with push channels.</summary>
public sealed class Notification
{
    private Notification() { }

    public Notification(Guid userId, Guid? plantId, string type, NotificationPriority priority, string title, string body,
        string? link, Guid sourceEventId, DateTimeOffset now)
    {
        Id = Ids.New();
        UserId = userId;
        PlantId = plantId;
        Type = type;
        Priority = priority;
        Title = title.Length > 120 ? title[..120] : title;
        Body = body.Length > 500 ? body[..500] : body;
        Link = link;
        SourceEventId = sourceEventId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? PlantId { get; private set; }
    public string Type { get; private set; } = "";
    public NotificationPriority Priority { get; private set; }
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string? Link { get; private set; }
    public Guid SourceEventId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

/// <summary>Result of sending one notification through one channel. (notification, channel) is unique: retries never double-send.</summary>
public sealed class NotificationDelivery
{
    private NotificationDelivery() { }

    public NotificationDelivery(Guid notificationId, Guid userId, Channel channel, DateTimeOffset now)
    {
        Id = Ids.New();
        NotificationId = notificationId;
        UserId = userId;
        Channel = channel;
        Status = DeliveryStatus.Pending;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid UserId { get; private set; }
    public Channel Channel { get; private set; }
    public DeliveryStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Succeeded(DateTimeOffset now) { Attempts++; Status = DeliveryStatus.Sent; FailureReason = null; CompletedAt = now; }
    public void Failed(string reason, DateTimeOffset now) { Attempts++; Status = DeliveryStatus.Failed; FailureReason = reason; CompletedAt = now; }
    public void Skip(string reason, DateTimeOffset now) { Status = DeliveryStatus.Skipped; FailureReason = reason; CompletedAt = now; }
}

public enum DeviceKind { WebPush, Fcm }

/// <summary>A device that can receive push notifications. A user may have many (2 phones, a tablet ...).</summary>
public sealed class UserDevice
{
    private UserDevice() { }

    public UserDevice(Guid userId, DeviceKind kind, string endpoint, string? p256dh, string? auth, string? label, DateTimeOffset now)
    {
        Id = Ids.New();
        UserId = userId;
        Kind = kind;
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
        Label = label;
        CreatedAt = now;
        LastSeenAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DeviceKind Kind { get; private set; }

    /// <summary>Web Push endpoint URL or FCM registration token.</summary>
    public string Endpoint { get; private set; } = "";

    public string? P256dh { get; private set; }
    public string? Auth { get; private set; }
    public string? Label { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public void Refresh(Guid userId, string? p256dh, string? auth, string? label, DateTimeOffset now)
    {
        UserId = userId;
        P256dh = p256dh ?? P256dh;
        Auth = auth ?? Auth;
        Label = label ?? Label;
        LastSeenAt = now;
    }
}

/// <summary>Whether the user follows the LINE Official Account (push messages only reach friends).</summary>
public sealed class LineFriendship
{
    private LineFriendship() { }

    public LineFriendship(Guid userId, bool isFriend, DateTimeOffset now)
    {
        UserId = userId;
        IsFriend = isFriend;
        ChangedAt = now;
    }

    public Guid UserId { get; private set; }
    public bool IsFriend { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    public bool Set(bool isFriend, DateTimeOffset now)
    {
        if (IsFriend == isFriend) return false;
        IsFriend = isFriend;
        ChangedAt = now;
        return true;
    }
}

public enum PushMode { Always, OnlyWhenLineFails }

/// <summary>Per-user notification settings. Muted types still appear in the in-app inbox.</summary>
public sealed class NotificationSettings
{
    private NotificationSettings() { }

    public NotificationSettings(Guid userId) => UserId = userId;

    public Guid UserId { get; private set; }
    public PushMode PushMode { get; private set; } = PushMode.Always;
    public List<string> LineMuted { get; private set; } = [];
    public List<string> PushMuted { get; private set; } = [];
    public TimeOnly? QuietFrom { get; private set; }
    public TimeOnly? QuietTo { get; private set; }

    public void Update(PushMode mode, IEnumerable<string> lineMuted, IEnumerable<string> pushMuted, TimeOnly? quietFrom, TimeOnly? quietTo)
    {
        if ((quietFrom is null) != (quietTo is null)) throw new DomainException("validation", "Set both start and end of quiet hours, or neither.");
        PushMode = mode;
        LineMuted = lineMuted.Distinct().Take(50).ToList();
        PushMuted = pushMuted.Distinct().Take(50).ToList();
        QuietFrom = quietFrom;
        QuietTo = quietTo;
    }

    public bool InQuietHours(TimeOnly localTime) =>
        QuietFrom is { } from && QuietTo is { } to &&
        (from < to ? localTime >= from && localTime < to : localTime >= from || localTime < to);
}

/// <summary>LINE OA messages sent per month (the free plan has a small quota).</summary>
public sealed class LineQuotaUsage
{
    public string Month { get; set; } = "";
    public int Sent { get; set; }
    public bool Exhausted { get; set; }
}
