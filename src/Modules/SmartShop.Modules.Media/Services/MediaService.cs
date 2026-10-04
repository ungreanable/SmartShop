using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Media;
using SmartShop.Infrastructure.Storage;
using SmartShop.Modules.Media.Data;
using SmartShop.Modules.Media.Domain;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Media.Services;

internal sealed class MediaService(MediaDbContext db, IObjectStorage storage, TimeProvider clock) : IMediaService
{
    public const long MaxImageBytes = 15 * 1024 * 1024;
    public const long MaxPdfBytes = 5 * 1024 * 1024;

    private static readonly HashSet<MediaPurpose> PdfAllowed = [MediaPurpose.PaymentSlip];

    public async Task<MediaInfo?> GetAsync(Guid mediaId, CancellationToken ct = default) =>
        (await db.Objects.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId, ct))?.ToInfo();

    public async Task<MediaInfo> RequireOwnedAsync(Guid mediaId, Guid ownerId, MediaPurpose purpose, CancellationToken ct = default)
    {
        var media = await GetAsync(mediaId, ct);
        if (media is null || media.OwnerId != ownerId)
            throw new DomainException("media_invalid", "The uploaded file was not found. Please upload it again.");
        if (media.Purpose != purpose)
            throw new DomainException("media_invalid", $"The uploaded file was uploaded for {media.Purpose}, not {purpose}.");
        return media;
    }

    public async Task<Stream?> OpenOriginalAsync(Guid mediaId, CancellationToken ct = default)
    {
        var media = await db.Objects.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId, ct);
        if (media is null) return null;
        return (await storage.GetAsync(media.OriginalKey, ct))?.Content;
    }

    public async Task<MediaObject> UploadAsync(Guid ownerId, Guid? plantId, MediaPurpose purpose, Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) throw new DomainException("media_empty", "The file is empty.");

        var type = FileSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 16)))
                   ?? throw new DomainException("media_type", "Only JPEG, PNG, WebP, GIF images (and PDF for slips) are allowed.");

        var now = clock.GetUtcNow();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        MediaObject media;
        if (type == "application/pdf")
        {
            if (!PdfAllowed.Contains(purpose)) throw new DomainException("media_type", "PDF files are only allowed for payment slips.");
            if (bytes.Length > MaxPdfBytes) throw new DomainException("media_too_large", "PDF files must be 5 MB or smaller.");
            media = MediaObject.Create(ownerId, plantId, purpose, type, bytes.Length, hash, null, null, false, now);
            await storage.PutAsync(media.OriginalKey, new MemoryStream(bytes), type, ct);
        }
        else
        {
            if (bytes.Length > MaxImageBytes) throw new DomainException("media_too_large", "Images must be 15 MB or smaller.");
            ProcessedImage processed;
            try { processed = ImageProcessor.Process(bytes); }
            catch (InvalidDataException) { throw new DomainException("media_corrupt", "The image could not be read."); }

            media = MediaObject.Create(ownerId, plantId, purpose, processed.OriginalContentType, processed.Original.Length, hash,
                processed.Width, processed.Height, true, now);
            await storage.PutAsync(media.OriginalKey, new MemoryStream(processed.Original), processed.OriginalContentType, ct);
            await storage.PutAsync(media.VariantKey("md"), new MemoryStream(processed.Medium), "image/webp", ct);
            await storage.PutAsync(media.VariantKey("sm"), new MemoryStream(processed.Small), "image/webp", ct);
        }

        db.Objects.Add(media);
        await db.SaveChangesAsync(ct);
        return media;
    }
}
