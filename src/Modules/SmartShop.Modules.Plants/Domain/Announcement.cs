using SmartShop.SharedKernel;

namespace SmartShop.Modules.Plants.Domain;

/// <summary>Village notice on the home page: "no water tomorrow 9-12", "market on Saturday".</summary>
public sealed class Announcement
{
    private Announcement() { }

    public Guid Id { get; private set; }
    public Guid PlantId { get; private set; }
    public string Title { get; private set; } = "";
    public string? Body { get; private set; }
    public Guid? ImageId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public bool IsPinned { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Announcement Create(Guid plantId, string title, string? body, Guid? imageId, DateTimeOffset startsAt,
        DateTimeOffset? endsAt, bool pinned, Guid createdBy, DateTimeOffset now)
    {
        if (endsAt is { } end && end <= startsAt) throw new DomainException("validation", "The end must be after the start.");
        return new Announcement
        {
            Id = Ids.New(),
            PlantId = plantId,
            Title = Guard.NotEmpty(title, "Title", 120),
            Body = Guard.Optional(body, "Details", 2000),
            ImageId = imageId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            IsPinned = pinned,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
    }

    public bool IsActive(DateTimeOffset now) => StartsAt <= now && (EndsAt is null || EndsAt > now);
}
