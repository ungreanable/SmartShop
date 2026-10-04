using SmartShop.SharedKernel;

namespace SmartShop.Modules.Ordering.Domain;

/// <summary>Chat message attached to an order, so conversations no longer get lost in a group chat.</summary>
public sealed class OrderMessage
{
    private OrderMessage() { }

    public OrderMessage(Guid orderId, Guid senderId, bool fromShop, string? body, Guid? imageId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body) && imageId is null) throw new DomainException("validation", "Write a message or attach a picture.");
        Id = Ids.New();
        OrderId = orderId;
        SenderId = senderId;
        FromShop = fromShop;
        Body = Guard.Optional(body, "Message", 1000);
        ImageId = imageId;
        SentAt = now;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid SenderId { get; private set; }
    public bool FromShop { get; private set; }
    public string? Body { get; private set; }
    public Guid? ImageId { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
}

public enum ReportStatus { Open, Resolved }

/// <summary>A problem with an order escalated to the village administrators.</summary>
public sealed class OrderReport
{
    private OrderReport() { }

    public OrderReport(Guid orderId, Guid plantId, Guid shopId, Guid reporterId, string reason, DateTimeOffset now)
    {
        Id = Ids.New();
        OrderId = orderId;
        PlantId = plantId;
        ShopId = shopId;
        ReporterId = reporterId;
        Reason = Guard.NotEmpty(reason, "Reason", 1000);
        Status = ReportStatus.Open;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid ReporterId { get; private set; }
    public string Reason { get; private set; } = "";
    public ReportStatus Status { get; private set; }
    public string? Resolution { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public void Resolve(Guid adminId, string resolution, DateTimeOffset now)
    {
        if (Status == ReportStatus.Resolved) throw new ConflictException("report_resolved", "This report is already resolved.");
        Status = ReportStatus.Resolved;
        Resolution = Guard.NotEmpty(resolution, "Resolution", 1000);
        ResolvedBy = adminId;
        ResolvedAt = now;
    }
}
