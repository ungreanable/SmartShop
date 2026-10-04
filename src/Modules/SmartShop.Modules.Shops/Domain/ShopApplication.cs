using SmartShop.SharedKernel;

namespace SmartShop.Modules.Shops.Domain;

public enum ApplicationStatus { Submitted, ChangesRequested, Approved, Rejected, Withdrawn }

/// <summary>A member's request to open a shop. A plant administrator must review it.</summary>
public sealed class ShopApplication
{
    private ShopApplication() { }

    public Guid Id { get; private set; }
    public Guid PlantId { get; private set; }
    public Guid ApplicantId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Category { get; private set; }
    public string? Description { get; private set; }
    public string? HouseNo { get; private set; }
    public string? Phone { get; private set; }
    public List<Guid> SampleImageIds { get; private set; } = [];
    public ApplicationStatus Status { get; private set; }
    public string? ReviewNote { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ShopId { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public uint Version { get; private set; }

    public static ShopApplication Submit(Guid plantId, Guid applicantId, ApplicationDetails details, DateTimeOffset now)
    {
        var application = new ShopApplication
        {
            Id = Ids.New(),
            PlantId = plantId,
            ApplicantId = applicantId,
            Status = ApplicationStatus.Submitted,
            SubmittedAt = now,
        };
        application.Apply(details);
        return application;
    }

    public void Resubmit(ApplicationDetails details, DateTimeOffset now)
    {
        if (Status != ApplicationStatus.ChangesRequested)
            throw new ConflictException("application_state", "Only applications with requested changes can be resubmitted.");
        Apply(details);
        Status = ApplicationStatus.Submitted;
        SubmittedAt = now;
    }

    public void Withdraw()
    {
        if (Status is not (ApplicationStatus.Submitted or ApplicationStatus.ChangesRequested))
            throw new ConflictException("application_state", "This application can no longer be withdrawn.");
        Status = ApplicationStatus.Withdrawn;
    }

    public void RequestChanges(Guid reviewer, string note, DateTimeOffset now)
    {
        RequireSubmitted();
        Status = ApplicationStatus.ChangesRequested;
        Review(reviewer, Guard.NotEmpty(note, "Note", 1000), now);
    }

    public void Reject(Guid reviewer, string reason, DateTimeOffset now)
    {
        RequireSubmitted();
        Status = ApplicationStatus.Rejected;
        Review(reviewer, Guard.NotEmpty(reason, "Reason", 1000), now);
    }

    public void Approve(Guid reviewer, Guid shopId, string? note, DateTimeOffset now)
    {
        RequireSubmitted();
        Status = ApplicationStatus.Approved;
        ShopId = shopId;
        Review(reviewer, Guard.Optional(note, "Note", 1000), now);
    }

    private void Apply(ApplicationDetails d)
    {
        Name = Guard.NotEmpty(d.Name, "Shop name", 80);
        Category = Guard.Optional(d.Category, "Category", 60);
        Description = Guard.Optional(d.Description, "Description", 2000);
        HouseNo = Guard.NotEmpty(d.HouseNo, "House number", 30);
        Phone = Guard.Optional(d.Phone, "Phone", 20);
        if (d.SampleImageIds.Count > 6) throw new DomainException("validation", "At most 6 sample images.");
        SampleImageIds = [.. d.SampleImageIds.Distinct()];
    }

    private void Review(Guid reviewer, string? note, DateTimeOffset now)
    {
        ReviewedBy = reviewer;
        ReviewedAt = now;
        ReviewNote = note;
    }

    private void RequireSubmitted()
    {
        if (Status != ApplicationStatus.Submitted)
            throw new ConflictException("application_state", "Only submitted applications can be reviewed.");
    }
}

public sealed record ApplicationDetails(string Name, string? Category, string? Description, string? HouseNo, string? Phone, IReadOnlyList<Guid> SampleImageIds);
