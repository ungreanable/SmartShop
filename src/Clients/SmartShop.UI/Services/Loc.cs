using System.Globalization;

namespace SmartShop.UI.Services;

/// <summary>
/// Tiny bilingual helper: every UI string is written inline in Thai and English, e.g. <c>@L["หน้าแรก", "Home"]</c>.
/// No resource keys to maintain, and contributors see both languages side by side.
/// </summary>
public sealed class Loc(IAppStorage storage)
{
    private const string Key = "smartshop.lang";

    public string Language { get; private set; } = "th";
    public bool IsThai => Language == "th";
    public event Action? Changed;

    public string this[string th, string en] => IsThai ? ThaiText.KeepLoanwords(th) : en;

    public async Task InitializeAsync()
    {
        Language = await storage.GetAsync(Key) is "en" ? "en" : "th";
        Apply();
    }

    public async Task SetAsync(string language)
    {
        Language = language == "en" ? "en" : "th";
        await storage.SetAsync(Key, Language);
        Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        var culture = CultureInfo.GetCultureInfo(IsThai ? "th-TH" : "en-GB");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

public static class Fmt
{
    public static string ShopRole(string role, Loc l) => role switch
    {
        "Owner" => l["เจ้าของร้าน", "Owner"],
        "Manager" => l["ผู้จัดการ", "Manager"],
        _ => l["พนักงาน", "Staff"],
    };

    public static string Baht(decimal amount) => "฿" + amount.ToString("#,0.##", CultureInfo.InvariantCulture);

    public static DateTimeOffset Local(DateTimeOffset value) => value.ToLocalTime();

    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public static string Day(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = System.DateTime.Now.Date;
        if (local.Date == today) return CultureInfo.CurrentCulture.Name.StartsWith("th", StringComparison.Ordinal) ? "วันนี้" : "Today";
        if (local.Date == today.AddDays(1)) return CultureInfo.CurrentCulture.Name.StartsWith("th", StringComparison.Ordinal) ? "พรุ่งนี้" : "Tomorrow";
        return local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }

    public static string Window(DateTimeOffset from, DateTimeOffset to) => $"{Day(from)} {Time(from)}–{Time(to)}";

    public static string Ago(DateTimeOffset value, Loc l)
    {
        var span = DateTimeOffset.UtcNow - value;
        if (span.TotalMinutes < 1) return l["เมื่อสักครู่", "just now"];
        if (span.TotalMinutes < 60) return l[$"{(int)span.TotalMinutes} นาทีที่แล้ว", $"{(int)span.TotalMinutes} min ago"];
        if (span.TotalHours < 24) return l[$"{(int)span.TotalHours} ชม.ที่แล้ว", $"{(int)span.TotalHours} h ago"];
        return DateTime(value);
    }
}

/// <summary>
/// Browsers split Thai lines with a dictionary that lacks many loanwords, so "ออเดอร์" can wrap as "ออเดอ|ร์".
/// Joining their letters with U+2060 WORD JOINER (invisible) keeps them on one line.
/// </summary>
public static class ThaiText
{
    private const char Joiner = '⁠';

    private static readonly (string Word, string Joined)[] Loanwords =
        new[] { "ออเดอร์", "โปรโมชั่น", "สต็อก", "เซิร์ฟเวอร์", "แอดมิน", "ออนไลน์", "เมนู", "สลิป", "คิวอาร์", "ล็อกอิน", "แอป", "ไลน์", "เช็ค" }
            .Select(w => (w, string.Join(Joiner, w.ToCharArray()))).ToArray();

    public static string KeepLoanwords(string text)
    {
        foreach (var (word, joined) in Loanwords)
            if (text.Contains(word, StringComparison.Ordinal)) text = text.Replace(word, joined, StringComparison.Ordinal);
        return text;
    }
}
