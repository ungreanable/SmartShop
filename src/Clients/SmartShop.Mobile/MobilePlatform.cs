using System.Security.Cryptography;
using System.Text;
using SmartShop.UI.Services;

namespace SmartShop.Mobile;

internal sealed class PreferencesStorage : IAppStorage
{
    public ValueTask<string?> GetAsync(string key) => ValueTask.FromResult(Preferences.Default.Get<string?>(key, null));

    public ValueTask SetAsync(string key, string? value)
    {
        if (value is null) Preferences.Default.Remove(key);
        else Preferences.Default.Set(key, value);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// LINE Login with OAuth 2.0 authorization code + PKCE in the system browser. LINE only redirects to https URLs,
/// so it returns to the API's /api/auth/line/mobile-callback which bounces to the app's <c>smartshop://auth</c> scheme.
/// </summary>
internal sealed class MobileLoginLauncher(AppConfig config) : ILoginLauncher
{
    public const string CallbackScheme = "smartshop";

    public bool IsInsideLine => false;

    public Task<ExternalLoginResult?> TryAutoLoginAsync() => Task.FromResult<ExternalLoginResult?>(null);

    public async Task<ExternalLoginResult?> LoginAsync(string returnUrl)
    {
        if (string.IsNullOrEmpty(config.LineChannelId))
            throw new InvalidOperationException("LINE Login is not configured on the server (Auth:Line:ChannelId).");
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
            throw new NotSupportedException("LINE login on Windows: please use the web app (or dev login).");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(12));
        var redirectUri = new Uri(new Uri(config.ApiBaseUrl), "api/auth/line/mobile-callback").ToString();

        var authorize = "https://access.line.me/oauth2/v2.1/authorize?response_type=code"
                        + $"&client_id={Uri.EscapeDataString(config.LineChannelId)}"
                        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                        + $"&state={state}&scope=profile%20openid&bot_prompt=aggressive"
                        + $"&code_challenge={challenge}&code_challenge_method=S256";

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(authorize),
                CallbackUrl = new Uri($"{CallbackScheme}://auth"),
                PrefersEphemeralWebBrowserSession = false,
            });
            if (result.Properties.GetValueOrDefault("state") != state) return null;
            return result.Properties.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code)
                ? new ExternalLoginResult("code", code, redirectUri, verifier)
                : null;
        }
        catch (TaskCanceledException)
        {
            return null; // the user closed the browser
        }
    }

    public Task<(ExternalLoginResult? Result, string ReturnUrl)> CompleteRedirectAsync(string code, string state) =>
        Task.FromResult<(ExternalLoginResult?, string)>((null, "/"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Firebase Cloud Messaging device token (Android). The server sends to it when LINE push is unavailable.</summary>
internal sealed partial class FcmPushRegistrar : IPushRegistrar
{
    public string Kind => "Fcm";

    public Task<bool> IsSupportedAsync() => Task.FromResult(PlatformSupported());

    public async Task<string> PermissionAsync()
    {
        if (!PlatformSupported()) return "denied";
        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        return status switch
        {
            PermissionStatus.Granted => "granted",
            PermissionStatus.Denied => "denied",
            _ => "default",
        };
    }

    public async Task<PushSubscriptionInfo?> SubscribeAsync(string? vapidPublicKey)
    {
        if (!PlatformSupported()) return null;
        if (await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted) return null;
        var token = await GetTokenAsync();
        return token is null ? null : new PushSubscriptionInfo(token, null, null, $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}".Trim());
    }

    private static partial bool PlatformSupported();

    private static partial Task<string?> GetTokenAsync();
}
