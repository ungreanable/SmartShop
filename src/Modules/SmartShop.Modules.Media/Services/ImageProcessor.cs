using SkiaSharp;

namespace SmartShop.Modules.Media.Services;

public sealed record ProcessedImage(byte[] Original, string OriginalContentType, byte[] Medium, byte[] Small, int Width, int Height);

/// <summary>
/// Normalises uploaded images: applies EXIF orientation, re-encodes (which strips EXIF/GPS metadata)
/// and produces WebP variants for fast loading on mobile networks.
/// </summary>
internal static class ImageProcessor
{
    private const int OriginalMax = 2400;
    private const int MediumMax = 1200;
    private const int SmallMax = 400;

    /// <summary>Refuse decompression bombs: a 100 MP bitmap alone is 400 MB of native memory.</summary>
    private const long MaxPixels = 100_000_000;

    public static ProcessedImage Process(byte[] data) => Process(data, data.Length);

    /// <summary>Processes the first <paramref name="length"/> bytes of a (pooled) buffer without copying it.</summary>
    public static ProcessedImage Process(byte[] data, int length)
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

            return new ProcessedImage(
                Encode(original, SKEncodedImageFormat.Jpeg, 90), "image/jpeg",
                Encode(medium, SKEncodedImageFormat.Webp, 80),
                Encode(small, SKEncodedImageFormat.Webp, 75),
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
