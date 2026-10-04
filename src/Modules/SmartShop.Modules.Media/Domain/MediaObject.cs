using SmartShop.Contracts.Media;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Media.Domain;

public sealed class MediaObject
{
    private MediaObject() { }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid? PlantId { get; private set; }
    public MediaPurpose Purpose { get; private set; }
    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }
    public string Sha256 { get; private set; } = "";
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public bool HasVariants { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static MediaObject Create(Guid ownerId, Guid? plantId, MediaPurpose purpose, string contentType, long size,
        string sha256, int? width, int? height, bool hasVariants, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        OwnerId = ownerId,
        PlantId = plantId,
        Purpose = purpose,
        ContentType = contentType,
        Size = size,
        Sha256 = sha256,
        Width = width,
        Height = height,
        HasVariants = hasVariants,
        CreatedAt = now,
    };

    public string OriginalKey => $"{Id:N}/orig";
    public string VariantKey(string variant) => $"{Id:N}/{variant}.webp";

    public MediaInfo ToInfo() => new(Id, OwnerId, PlantId, Purpose, ContentType, Size, Sha256);
}
