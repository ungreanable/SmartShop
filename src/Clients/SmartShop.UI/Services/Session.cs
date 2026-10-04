using System.Net.Http.Json;
using System.Text.Json;

namespace SmartShop.UI.Services;

public sealed record AuthUser(Guid Id, string DisplayName, string? PictureUrl, bool IsSystemAdmin, bool HasConsent, string Locale);
public sealed record AuthResult(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AuthUser User);

/// <summary>Holds the signed-in user and tokens; refreshes the access token transparently (rotating refresh tokens).</summary>
public sealed class Session : IDisposable
{
    private const string StorageKey = "smartshop.session";
    private readonly IAppStorage _storage;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthResult? _auth;

    public Session(IAppStorage storage, AppConfig config)
    {
        _storage = storage;
        _http = new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) };
    }

    public AuthUser? User => _auth?.User;
    public bool IsAuthenticated => _auth is not null;
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var json = await _storage.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return;
        try { _auth = JsonSerializer.Deserialize<AuthResult>(json, ApiJson.Options); }
        catch (JsonException) { _auth = null; }
    }

    public Task<bool> SignInAsync(ExternalLoginResult login, string device) => login.Kind switch
    {
        "idToken" => PostAuthAsync("api/auth/line", new { idToken = login.Token, device }),
        _ => PostAuthAsync("api/auth/line/code", new { code = login.Token, redirectUri = login.RedirectUri, codeVerifier = login.CodeVerifier, device }),
    };

    public Task<bool> DevSignInAsync(string key, string displayName, bool systemAdmin) =>
        PostAuthAsync("api/auth/dev", new { key, displayName, systemAdmin });

    public async Task UpdateUserAsync(Func<AuthUser, AuthUser> update)
    {
        if (_auth is null) return;
        _auth = _auth with { User = update(_auth.User) };
        await SaveAsync();
        Changed?.Invoke();
    }

    /// <summary>Returns a valid access token, refreshing it shortly before it expires. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (_auth is null) return null;
        if (!forceRefresh && _auth.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return _auth.AccessToken;

        await _refreshLock.WaitAsync();
        try
        {
            if (_auth is null) return null;
            if (!forceRefresh && _auth.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return _auth.AccessToken;
            using var response = await _http.PostAsJsonAsync("api/auth/refresh", new { refreshToken = _auth.RefreshToken });
            if (!response.IsSuccessStatusCode)
            {
                await ClearAsync();
                return null;
            }
            _auth = await response.Content.ReadFromJsonAsync<AuthResult>(ApiJson.Options);
            await SaveAsync();
            return _auth?.AccessToken;
        }
        catch (HttpRequestException)
        {
            return _auth?.AccessToken; // offline: keep the old token, the API call will tell
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task SignOutAsync()
    {
        if (_auth is not null)
        {
            try { await _http.PostAsJsonAsync("api/auth/logout", new { refreshToken = _auth.RefreshToken }); }
            catch (HttpRequestException) { }
        }
        await ClearAsync();
    }

    private async Task<bool> PostAuthAsync(string url, object body)
    {
        using var response = await _http.PostAsJsonAsync(url, body, ApiJson.Options);
        if (!response.IsSuccessStatusCode)
        {
            LastError = await ApiProblem.ReadAsync(response);
            return false;
        }
        _auth = await response.Content.ReadFromJsonAsync<AuthResult>(ApiJson.Options);
        await SaveAsync();
        Changed?.Invoke();
        return true;
    }

    public ApiProblem? LastError { get; private set; }

    private async Task ClearAsync()
    {
        _auth = null;
        await _storage.SetAsync(StorageKey, null);
        Changed?.Invoke();
    }

    private ValueTask SaveAsync() => _storage.SetAsync(StorageKey, JsonSerializer.Serialize(_auth, ApiJson.Options));

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}
