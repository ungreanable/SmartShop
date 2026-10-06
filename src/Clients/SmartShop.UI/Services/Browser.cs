using Microsoft.JSInterop;

namespace SmartShop.UI.Services;

/// <summary>Browser features used by the app (alarm sound, clipboard, share, files). Works in WASM and BlazorWebView.</summary>
public sealed class Browser(IJSRuntime js)
{
    public ValueTask StartAlarmAsync() => js.InvokeVoidAsync("smartshop.alarm.start");
    public ValueTask StopAlarmAsync() => js.InvokeVoidAsync("smartshop.alarm.stop");
    public ValueTask BeepAsync() => js.InvokeVoidAsync("smartshop.alarm.beep");
    public ValueTask<bool> CopyAsync(string text) => js.InvokeAsync<bool>("smartshop.copy", text);
    public ValueTask<bool> ShareAsync(string title, string text, string url) => js.InvokeAsync<bool>("smartshop.share", title, text, url);
    public ValueTask DownloadAsync(string fileName, byte[] content, string contentType) =>
        js.InvokeVoidAsync("smartshop.download", fileName, Convert.ToBase64String(content), contentType);
    /// <summary>Opens the share sheet with a file; returns "shared", "cancelled" or "unsupported".</summary>
    public async ValueTask<string> ShareFileAsync(string fileName, byte[] content, string contentType, string title)
    {
        try
        {
            return await js.InvokeAsync<string>("smartshop.shareFile", fileName, Convert.ToBase64String(content), contentType, title);
        }
        catch (JSException)
        {
            return "unsupported"; // e.g. an older smartshop.js still cached right after an update: fall back to download
        }
    }
    /// <summary>Saves a picture as PNG (share sheet on iPhone, download elsewhere); null when it could not be saved.</summary>
    public async ValueTask<string?> SaveImageAsync(string url, string fileName)
    {
        try
        {
            return await js.InvokeAsync<string>("smartshop.saveImage", url, fileName);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public ValueTask ScrollToAsync(string elementId) => js.InvokeVoidAsync("smartshop.scrollTo", elementId);
    public ValueTask SetBadgeAsync(int count) => js.InvokeVoidAsync("smartshop.setBadge", count);
    public ValueTask<string> UserAgentLabelAsync() => js.InvokeAsync<string>("smartshop.deviceLabel");
}
