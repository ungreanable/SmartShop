using Microsoft.JSInterop;

namespace SmartShop.UI.Services;

/// <summary>
/// Light / dark / follow the phone, chosen per device ("smartshop.theme"). Read before the first render, together with
/// the phone's own setting, so the app never flashes light before turning dark. "system" keeps following live changes.
/// </summary>
public sealed class ThemeSettings(IAppStorage storage, IJSRuntime js) : IDisposable
{
    private const string Key = "smartshop.theme";
    private DotNetObjectReference<ThemeSettings>? _self;

    public string Mode { get; private set; } = "system";
    public bool SystemDark { get; private set; }
    public bool IsDark => Mode == "dark" || (Mode == "system" && SystemDark);
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        Mode = Normalize(await storage.GetAsync(Key));
        try
        {
            _self ??= DotNetObjectReference.Create(this);
            SystemDark = await js.InvokeAsync<bool>("smartshop.watchDarkMode", _self);
        }
        catch (JSException) { } // an older smartshop.js right after an update: light until it is reloaded
        await ApplyStatusBarAsync();
    }

    public async Task SetAsync(string mode)
    {
        Mode = Normalize(mode);
        await storage.SetAsync(Key, Mode);
        await ApplyStatusBarAsync();
        Changed?.Invoke();
    }

    /// <summary>Called from smartshop.js when the phone switches between light and dark.</summary>
    [JSInvokable]
    public async Task OnSystemDarkChanged(bool dark)
    {
        SystemDark = dark;
        if (Mode != "system") return;
        await ApplyStatusBarAsync();
        Changed?.Invoke();
    }

    // The browser's status bar / address bar colour (theme-color) follows the app, not only the phone.
    private async Task ApplyStatusBarAsync()
    {
        try { await js.InvokeVoidAsync("smartshop.setThemeColor", IsDark ? "#1A1D20" : "#00803E", IsDark); }
        catch (JSException) { }
    }

    private static string Normalize(string? mode) => mode is "light" or "dark" ? mode : "system";

    public void Dispose() => _self?.Dispose();
}
