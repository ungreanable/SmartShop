namespace SmartShop.UI.Services;

/// <summary>
/// Remembers a "join village" link across the LINE login round trip. LINE (especially LIFF inside the LINE app)
/// may return to the site root instead of the page that started the login, so the code is kept on the device and
/// the app goes back to it after sign-in.
/// </summary>
public sealed class PendingJoin(IAppStorage storage)
{
    private const string Key = "smartshop.pendingJoin";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    public ValueTask SaveAsync(string code) =>
        storage.SetAsync(Key, $"{code.Trim()}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

    public ValueTask ClearAsync() => storage.SetAsync(Key, null);

    public async Task<string?> GetAsync()
    {
        string? raw;
        try { raw = await storage.GetAsync(Key); }
        catch (Exception) { return null; }
        if (raw?.Split('|') is not [var code, var at] || !long.TryParse(at, out var seconds)) return null;
        return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) <= MaxAge && code.Length > 0 ? code : null;
    }
}
