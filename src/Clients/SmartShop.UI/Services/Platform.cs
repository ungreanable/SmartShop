namespace SmartShop.UI.Services;

/// <summary>Runtime configuration delivered by the host (web: /app-config.json, mobile: app settings).</summary>
public sealed class AppConfig
{
    public string ApiBaseUrl { get; set; } = "";
    public string LineChannelId { get; set; } = "";
    public string LiffId { get; set; } = "";
    public string? LineAddFriendUrl { get; set; }
    public bool DevLoginEnabled { get; set; }
    public bool WebhooksEnabled { get; set; }
    public string AppName { get; set; } = "SmartShop";
    public string Platform { get; set; } = "web";
}

/// <summary>Key-value persistence (browser localStorage on the web, Preferences on mobile).</summary>
public interface IAppStorage
{
    ValueTask<string?> GetAsync(string key);
    ValueTask SetAsync(string key, string? value);
}

public sealed record ExternalLoginResult(string Kind, string Token, string? RedirectUri = null, string? CodeVerifier = null);

/// <summary>Platform specific LINE login (LIFF / browser redirect / mobile system browser).</summary>
public interface ILoginLauncher
{
    /// <summary>Returns an id token immediately when running inside LINE (LIFF), otherwise null.</summary>
    Task<ExternalLoginResult?> TryAutoLoginAsync();

    /// <summary>Starts the interactive LINE login. Web navigates away; mobile returns the result.</summary>
    Task<ExternalLoginResult?> LoginAsync(string returnUrl);

    /// <summary>Completes a browser redirect login (web only). Returns the result and the URL to go back to.</summary>
    Task<(ExternalLoginResult? Result, string ReturnUrl)> CompleteRedirectAsync(string code, string state);

    bool IsInsideLine { get; }
}

/// <summary>Registers this device for push notifications (Web Push or FCM).</summary>
public interface IPushRegistrar
{
    string Kind { get; }
    Task<bool> IsSupportedAsync();
    Task<string> PermissionAsync();
    Task<PushSubscriptionInfo?> SubscribeAsync(string? vapidPublicKey);
}

public sealed record PushSubscriptionInfo(string Endpoint, string? P256dh, string? Auth, string Label);
