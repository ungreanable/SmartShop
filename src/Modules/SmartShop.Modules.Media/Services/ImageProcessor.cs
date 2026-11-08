using System.Buffers.Binary;
using SkiaSharp;

namespace SmartShop.Modules.Media.Services;

public sealed record ProcessedImage(byte[] Original, string OriginalContentType, byte[] Medium, byte[] Small, int Width, int Height);

/// <summary>
/// Normalises uploaded images: applies EXIF orientation, re-encodes (which strips EXIF/GPS metadata)
/// and stores everything as WebP (original and the resized variants) for fast loading on mobile networks.
/// </summary>
internal static class ImageProcessor
{
    /// <summary>Matches the size the app shrinks photos to on the phone (ImageUploader), so those need no resize here.</summary>
    public const int OriginalMax = 2048;
    private const int MediumMax = 1200;
    private const int SmallMax = 400;

    private const int OriginalQuality = 82;
    /// <summary>Payment slips keep more detail: their QR code is read from the original.</summary>
    private const int DetailedOriginalQuality = 90;
    private const int MediumQuality = 75;
    private const int SmallQuality = 70;

    /// <summary>Refuse decompression bombs: a 100 MP bitmap alone is 400 MB of native memory.</summary>
    private const long MaxPixels = 100_000_000;

    public static ProcessedImage Process(byte[] data, bool keepDetail = false) => Process(data, data.Length, keepDetail);

    /// <summary>Processes the first <paramref name="length"/> bytes of a (pooled) buffer without copying it.</summary>
    public static ProcessedImage Process(byte[] data, int length, bool keepDetail = false)
    {
        using var codec = SKCodec.Create(new MemoryStream(data, 0, length, writable: false))
                          ?? throw new InvalidDataException("The file is not a supported image.");
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
            throw new InvalidDataException("The image is too large.");

        // Bitmaps live in native memory the GC limits do not cover; a 48 MP phone photo is ~190 MB fully decoded.
        // Decode only as large as the biggest variant needs (JPEG decodes at 1/2, 1/4, 1/8 for free).
        using var decoded = SKBitmap.Decode(codec, DecodeInfo(codec)) ?? throw new InvalidDataException("The image could not be decoded.");
        var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        try
        {
            using var original = ResizeToFit(oriented, OriginalMax);
            using var medium = ResizeToFit(original, MediumMax);
            using var small = ResizeToFit(medium, SmallMax);

            // A photo the app already shrank to WebP on the phone is kept byte for byte: encoding the biggest image is
            // most of the work, and on a 1 vCPU server that time is taken from every other request.
            var keep = ReferenceEquals(oriented, decoded) && original.Width == codec.Info.Width && original.Height == codec.Info.Height
                       && WebpContainer.IsPlainStill(data.AsSpan(0, length));
            return new ProcessedImage(
                keep ? data.AsSpan(0, length).ToArray() : Encode(original, SKEncodedImageFormat.Webp, keepDetail ? DetailedOriginalQuality : OriginalQuality),
                "image/webp",
                Encode(medium, SKEncodedImageFormat.Webp, MediumQuality),
                Encode(small, SKEncodedImageFormat.Webp, SmallQuality),
                original.Width, original.Height);
        }
        finally
        {
            if (!ReferenceEquals(oriented, decoded)) oriented.Dispose();
        }
    }

    /// <summary>The smallest size the codec can decode directly that still covers <see cref="OriginalMax"/>.</summary>
    private static SKImageInfo DecodeInfo(SKCodec codec)
    {
        var full = codec.Info;
        foreach (var scale in new[] { 0.125f, 0.25f, 0.5f })
        {
            var size = codec.GetScaledDimensions(scale);
            if (Math.Max(size.Width, size.Height) >= OriginalMax && size.Width < full.Width)
                return full.WithSize(size.Width, size.Height);
        }
        return full;
    }

    private static SKBitmap ResizeToFit(SKBitmap source, int max)
    {
        // Already small enough: a plain copy instead of resampling at the same size.
        if (Math.Max(source.Width, source.Height) <= max && source.Copy(SKColorType.Rgba8888) is { } copy) return copy;
        var scale = Math.Min(1.0, (double)max / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new InvalidDataException("The image could not be resized.");
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Applies all eight EXIF orientations (rotations and the mirrored variants some front cameras write).</summary>
    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bitmap;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        float w = bitmap.Width, h = bitmap.Height;
        // Maps stored pixels to display pixels (same matrices as Skia's SkEncodedOriginToMatrix).
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var oriented = new SKBitmap(new SKImageInfo(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height,
            bitmap.ColorType, bitmap.AlphaType));
        using var canvas = new SKCanvas(oriented);
        canvas.SetMatrix(matrix);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return oriented;
    }
}

/// <summary>Reads the chunk list of a WebP file (RIFF container) without decoding it.</summary>
internal static class WebpContainer
{
    /// <summary>
    /// True for a still WebP made only of picture data: no EXIF/XMP (which can hold GPS), no ICC profile, no animation and
    /// nothing hidden after the last chunk. Such a file can be stored as it is without leaking anything about the phone.
    /// </summary>
    public static bool IsPlainStill(ReadOnlySpan<byte> file)
    {
        if (file.Length < 20 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WEBP"u8)) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file[4..8]) + 8L != file.Length) return false;

        var position = 12;
        var hasPicture = false;
        while (position < file.Length)
        {
            if (file.Length - position < 8) return false;
            var type = file.Slice(position, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(position + 4, 4));
            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8)) hasPicture = true;
            else if (!type.SequenceEqual("VP8X"u8) && !type.SequenceEqual("ALPH"u8)) return false;
            var next = position + 8L + size + (size & 1);
            if (next > file.Length) return false;
            position = (int)next;
        }
        return hasPicture;
    }
}

/// <summary>Detects the real file type from magic bytes (never trust the client's Content-Type).</summary>
internal static class FileSignature
{
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "image/jpeg";
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (header.Length >= 5 && header[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        return null;
    }
}
