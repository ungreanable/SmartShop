using System.Security.Cryptography;
using SmartShop.Contracts.Plants;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Plants.Domain;

/// <summary>A village. Every shop, order and member belongs to exactly one plant.</summary>
public sealed class Plant
{
    private Plant() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>
    /// Code shared as a link/QR so people can <em>request</em> to join. It never grants access by itself:
    /// a plant administrator must approve every membership.
    /// </summary>
    public string JoinCode { get; private set; } = "";

    public string TimeZoneId { get; private set; } = "Asia/Bangkok";
    public Guid? PictureId { get; private set; }
    public string? Description { get; private set; }
    public PushRequirement PushRequirement { get; private set; } = PushRequirement.Warn;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public static Plant Create(string name, string? timeZoneId, Guid createdBy, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        Name = Guard.NotEmpty(name, "Village name", 120),
        TimeZoneId = ValidTimeZone(timeZoneId),
        JoinCode = NewJoinCode(),
        CreatedAt = now,
        CreatedBy = createdBy,
    };

    public void Update(string name, string? description, Guid? pictureId, PushRequirement pushRequirement, string? timeZoneId)
    {
        Name = Guard.NotEmpty(name, "Village name", 120);
        Description = Guard.Optional(description, "Description", 1000);
        PictureId = pictureId;
        PushRequirement = pushRequirement;
        TimeZoneId = ValidTimeZone(timeZoneId ?? TimeZoneId);
    }

    public string ResetJoinCode() => JoinCode = NewJoinCode();

    private static string ValidTimeZone(string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? "Asia/Bangkok" : id.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            throw new DomainException("validation", $"Unknown time zone '{id}'.");
        return id;
    }

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

    private static string NewJoinCode() =>
        string.Create(8, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
}
