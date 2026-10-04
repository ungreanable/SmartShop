using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace SmartShop.Modules.Payments.Services;

/// <summary>
/// Thai banking apps print a QR code on every transfer slip containing the transaction reference.
/// Reading it lets us detect the same slip being reused for several orders.
/// </summary>
public static class SlipReader
{
    public static string? TryReadReference(Stream image)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(image);
            if (bitmap is null) return null;
            var rgb = new byte[bitmap.Width * bitmap.Height * 3];
            var i = 0;
            foreach (var pixel in bitmap.Pixels)
            {
                rgb[i++] = pixel.Red;
                rgb[i++] = pixel.Green;
                rgb[i++] = pixel.Blue;
            }
            var source = new RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGB24);
            var reader = new MultiFormatReader();
            var result = reader.decode(new BinaryBitmap(new HybridBinarizer(source)), new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                [DecodeHintType.TRY_HARDER] = true,
            });
            var text = result?.Text?.Trim();
            return string.IsNullOrEmpty(text) || text.Length > 500 ? null : text;
        }
        catch (Exception e) when (e is ReaderException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Optional plug-in point for third-party slip verification services (usually paid). The default does nothing;
/// the shop verifies slips manually.
/// </summary>
public interface ISlipVerifier
{
    Task<SlipVerification> VerifyAsync(string slipReference, decimal expectedAmount, CancellationToken ct);
}

public sealed record SlipVerification(bool Checked, bool Valid, string? Message);

internal sealed class ManualSlipVerifier : ISlipVerifier
{
    public Task<SlipVerification> VerifyAsync(string slipReference, decimal expectedAmount, CancellationToken ct) =>
        Task.FromResult(new SlipVerification(false, false, null));
}
