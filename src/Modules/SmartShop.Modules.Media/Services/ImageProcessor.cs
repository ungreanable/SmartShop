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

    public static ProcessedImage Process(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
                          ?? throw new InvalidDataException("The file is not a supported image.");
        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("The image could not be decoded.");
        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);

        using var original = ResizeToFit(oriented, OriginalMax);
        using var medium = ResizeToFit(oriented, MediumMax);
        using var small = ResizeToFit(oriented, SmallMax);

        return new ProcessedImage(
            Encode(original, SKEncodedImageFormat.Jpeg, 90), "image/jpeg",
            Encode(medium, SKEncodedImageFormat.Webp, 80),
            Encode(small, SKEncodedImageFormat.Webp, 75),
            original.Width, original.Height);
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

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var (rotate, swap) = origin switch
        {
            SKEncodedOrigin.BottomRight => (180f, false),
            SKEncodedOrigin.RightTop => (90f, true),
            SKEncodedOrigin.LeftBottom => (270f, true),
            _ => (0f, false),
        };
        if (rotate == 0f) return bitmap.Copy();

        var rotated = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(rotate);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
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
