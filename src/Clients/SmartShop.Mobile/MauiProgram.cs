using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartShop.UI.Services;

namespace SmartShop.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        var config = LoadConfig();
        builder.Services.AddSmartShopUi(config);
        builder.Services.AddScoped<IAppStorage, PreferencesStorage>();
        builder.Services.AddScoped<ILoginLauncher, MobileLoginLauncher>();
        builder.Services.AddScoped<IPushRegistrar, FcmPushRegistrar>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }

    /// <summary>
    /// The server URL comes from the bundled appsettings.json; everything else (LINE channel, feature flags) from the
    /// server's /app-config.json so one app build works with any deployment's settings.
    /// </summary>
    private static AppConfig LoadConfig()
    {
        var server = ReadServerUrl();
        if (!server.EndsWith('/')) server += "/";

        AppConfig config;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(8) };
            config = Task.Run(() => http.GetFromJsonAsync<AppConfig>("app-config.json", ApiJson.Options)).GetAwaiter().GetResult() ?? new AppConfig();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline at start-up: the UI shows its own connection errors and retries.
            config = new AppConfig();
        }

        config.ApiBaseUrl = server;
        config.Platform = "mobile";
        config.LiffId = ""; // LIFF only exists inside the LINE app's browser
        return config;
    }

    private static string ReadServerUrl()
    {
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("ServerUrl", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
                return url.GetString()!;
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException)
        {
        }

        // Development: the web host on this machine (the Android emulator reaches the host through 10.0.2.2).
        return DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5100/" : "http://localhost:5100/";
    }
}
