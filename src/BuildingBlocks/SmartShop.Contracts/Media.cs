namespace SmartShop.Contracts.Media;

public enum MediaPurpose
{
    PlantPicture, ShopLogo, ShopCover, ItemImage, PaymentQr, ApplicationSample, Announcement,
    PaymentSlip, DeliveryPhoto, ChatImage,
}

public sealed record MediaInfo(Guid Id, Guid OwnerId, Guid? PlantId, MediaPurpose Purpose, string ContentType, long Size, string Sha256);

public interface IMediaService
{
    Task<MediaInfo?> GetAsync(Guid mediaId, CancellationToken ct = default);

    /// <summary>Throws when the media does not exist, belongs to another user, or has an unexpected purpose.</summary>
    Task<MediaInfo> RequireOwnedAsync(Guid mediaId, Guid ownerId, MediaPurpose purpose, CancellationToken ct = default);

    Task<Stream?> OpenOriginalAsync(Guid mediaId, CancellationToken ct = default);
}
