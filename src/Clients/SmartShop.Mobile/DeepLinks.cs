namespace SmartShop.Mobile;

/// <summary>In-app links from push notifications. Kept until the Blazor view exists when the app was cold-started.</summary>
public static class DeepLinks
{
    private static string? _pending;

    public static event Action<string>? Requested;

    public static void Open(string url)
    {
        // Only relative app routes: a notification must never navigate the web view to another site.
        if (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal)) return;
        if (Requested is { } handler) handler(url);
        else _pending = url;
    }

    public static string? TakePending()
    {
        var url = _pending;
        _pending = null;
        return url;
    }
}
