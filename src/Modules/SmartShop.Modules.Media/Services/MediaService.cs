using System.Buffers;
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

    // At most two images decoded at once, so simultaneous uploads cannot push the API past its memory limit.
    private static readonly SemaphoreSlim ProcessingGate = new(2);
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    public async Task<MediaObject> UploadAsync(Guid ownerId, Guid? plantId, MediaPurpose purpose, Stream content, long length, CancellationToken ct)
    {
        // Reject before reading a byte: the declared size is enough to know.
        if (length <= 0) throw new DomainException("media_empty", "The file is empty.");
        if (length > MaxImageBytes) throw new TooLargeException("media_too_large", "Images must be 15 MB or smaller (PDF 5 MB).");

        // Pooled buffer instead of MemoryStream + ToArray (two full copies per upload, all on the large object heap).
        var size = (int)length;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            await content.ReadExactlyAsync(buffer.AsMemory(0, size), ct);
            return await StoreAsync(ownerId, plantId, purpose, buffer, size, ct);
        }
        catch (EndOfStreamException)
        {
            throw new DomainException("media_incomplete", "The upload was interrupted. Please try again.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<MediaObject> StoreAsync(Guid ownerId, Guid? plantId, MediaPurpose purpose, byte[] buffer, int size, CancellationToken ct)
    {
        var bytes = buffer.AsMemory(0, size);
        var type = FileSignature.Detect(bytes.Span[..Math.Min(size, 16)])
                   ?? throw new DomainException("media_type", "Only JPEG, PNG, WebP, GIF images (and PDF for slips) are allowed.");

        var now = clock.GetUtcNow();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));

        MediaObject media;
        if (type == "application/pdf")
        {
            if (!PdfAllowed.Contains(purpose)) throw new DomainException("media_type", "PDF files are only allowed for payment slips.");
            if (size > MaxPdfBytes) throw new TooLargeException("media_too_large", "PDF files must be 5 MB or smaller.");
            media = MediaObject.Create(ownerId, plantId, purpose, type, size, hash, null, null, false, now);
            await storage.PutAsync(media.OriginalKey, new MemoryStream(buffer, 0, size, writable: false), type, ct);
        }
        else
        {
            if (!await ProcessingGate.WaitAsync(GateTimeout, ct))
                throw new UnavailableException("media_busy", "Many pictures are being processed right now. Please try again in a moment.");
            ProcessedImage processed;
            try { processed = ImageProcessor.Process(buffer, size); }
            catch (InvalidDataException) { throw new DomainException("media_corrupt", "The image could not be read."); }
            finally { ProcessingGate.Release(); }

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
