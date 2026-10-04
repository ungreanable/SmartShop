using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SmartShop.UI.Services;

namespace SmartShop.Web.Client;

internal sealed class BrowserStorage(IJSRuntime js) : IAppStorage
{
    public ValueTask<string?> GetAsync(string key) => js.InvokeAsync<string?>("smartshop.storage.get", key);
    public ValueTask SetAsync(string key, string? value) => js.InvokeVoidAsync("smartshop.storage.set", key, value);
}

/// <summary>
/// LINE login for the web: silent LIFF login inside the LINE app (e.g. opened from the village OpenChat),
/// OAuth 2.0 authorization code + PKCE everywhere else.
/// </summary>
internal sealed class WebLoginLauncher(IJSRuntime js, AppConfig config, NavigationManager nav) : ILoginLauncher
{
    public bool IsInsideLine { get; private set; }

    public async Task<ExternalLoginResult?> TryAutoLoginAsync()
    {
        if (string.IsNullOrEmpty(config.LiffId)) return null;
        var state = await js.InvokeAsync<JsonElement>("smartshop.liff.init", config.LiffId);
        if (!state.GetProperty("ready").GetBoolean()) return null;
        IsInsideLine = state.GetProperty("inClient").GetBoolean();
        if (!state.GetProperty("loggedIn").GetBoolean()) return null;
        var token = await js.InvokeAsync<string?>("smartshop.liff.idToken");
        return token is null ? null : new ExternalLoginResult("idToken", token);
    }

    public async Task<ExternalLoginResult?> LoginAsync(string returnUrl)
    {
        if (string.IsNullOrEmpty(config.LineChannelId))
            throw new InvalidOperationException("LINE Login is not configured (Auth:Line:ChannelId).");

        if (!string.IsNullOrEmpty(config.LiffId) && await js.InvokeAsync<bool>("smartshop.liff.isLineBrowser"))
        {
            await js.InvokeVoidAsync("smartshop.liff.login", nav.ToAbsoluteUri(returnUrl).ToString());
            return null;
        }

        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)) + "|" + returnUrl;
        await js.InvokeVoidAsync("smartshop.lineLogin", config.LineChannelId, RedirectUri, state);
        return null; // the browser navigates to LINE and comes back to /auth/callback
    }

    public async Task<(ExternalLoginResult? Result, string ReturnUrl)> CompleteRedirectAsync(string code, string state)
    {
        var pkce = await js.InvokeAsync<JsonElement?>("smartshop.takePkce");
        if (pkce is not { } saved || saved.GetProperty("state").GetString() != state) return (null, "/");
        var returnUrl = state.Split('|', 2) is [_, var url] ? url : "/";
        return (new ExternalLoginResult("code", code, RedirectUri, saved.GetProperty("verifier").GetString()), returnUrl);
    }

    private string RedirectUri => nav.ToAbsoluteUri("/auth/callback").ToString();
}

internal sealed class WebPushRegistrar(IJSRuntime js) : IPushRegistrar
{
    public string Kind => "WebPush";

    public async Task<bool> IsSupportedAsync() => await js.InvokeAsync<bool>("smartshop.push.supported");

    public async Task<string> PermissionAsync() => await js.InvokeAsync<string>("smartshop.push.permission");

    public async Task<PushSubscriptionInfo?> SubscribeAsync(string? vapidPublicKey)
    {
        if (string.IsNullOrEmpty(vapidPublicKey)) return null;
        var result = await js.InvokeAsync<JsonElement?>("smartshop.push.subscribe", vapidPublicKey);
        if (result is not { ValueKind: JsonValueKind.Object } sub) return null;
        var label = await js.InvokeAsync<string>("smartshop.deviceLabel");
        return new PushSubscriptionInfo(sub.GetProperty("endpoint").GetString()!, sub.GetProperty("p256dh").GetString(), sub.GetProperty("auth").GetString(), label);
    }
}
