using System.Globalization;
using System.Text;
using QRCoder;

namespace SmartShop.Modules.Payments.Services;

/// <summary>
/// Builds Thai PromptPay QR payloads (EMVCo merchant-presented mode, BOT standard) so the customer's banking app
/// pre-fills the receiver and the exact order amount.
/// </summary>
public static class PromptPay
{
    private const string Aid = "A000000677010111";

    public static string Payload(string promptPayId, decimal? amount)
    {
        var digits = new string(promptPayId.Where(char.IsDigit).ToArray());
        var account = digits.Length switch
        {
            // Mobile: 0812345678 -> 0066812345678 (13 digits, country code 66 without the leading 0)
            10 => Tag("01", ("66" + digits[1..]).PadLeft(13, '0')),
            13 => Tag("02", digits), // citizen / tax id
            15 => Tag("03", digits), // e-wallet id
            _ => throw new ArgumentException("PromptPay ID must have 10, 13 or 15 digits.", nameof(promptPayId)),
        };

        var payload = new StringBuilder()
            .Append(Tag("00", "01"))
            .Append(Tag("01", amount is > 0 ? "12" : "11")) // 12 = dynamic (single use, with amount)
            .Append(Tag("29", Tag("00", Aid) + account))
            .Append(Tag("58", "TH"))
            .Append(Tag("53", "764")) // THB
            .Append(amount is > 0 ? Tag("54", amount.Value.ToString("0.00", CultureInfo.InvariantCulture)) : "")
            .Append("6304");
        return payload + Crc16(payload.ToString()).ToString("X4", CultureInfo.InvariantCulture);
    }

    public static string PngDataUrl(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string Tag(string id, string value) => id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) as required by EMVCo.</summary>
    internal static ushort Crc16(string data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(data))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
