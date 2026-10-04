using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Identity.Services;

public sealed class LineLoginOptions
{
    public const string Section = "Auth:Line";

    /// <summary>LINE Login channel id (also the id_token audience).</summary>
    public string ChannelId { get; set; } = "";

    public string ChannelSecret { get; set; } = "";

    /// <summary>LIFF app id used by the web client when opened inside the LINE app.</summary>
    public string LiffId { get; set; } = "";

    /// <summary>Deep link the mobile app listens to after LINE redirects back to the API.</summary>
    public string MobileRedirectScheme { get; set; } = "smartshop";
}

public sealed record LineProfile(string UserId, string DisplayName, string? PictureUrl);

/// <summary>Verifies LINE Login id tokens and exchanges authorization codes (OAuth 2.0 + PKCE).</summary>
public interface ILineLoginClient
{
    Task<LineProfile> VerifyIdTokenAsync(string idToken, CancellationToken ct);

    Task<LineProfile> ExchangeCodeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct);
}

internal sealed class LineLoginClient(HttpClient http, IOptions<LineLoginOptions> options) : ILineLoginClient
{
    private readonly LineLoginOptions _options = options.Value;

    public async Task<LineProfile> VerifyIdTokenAsync(string idToken, CancellationToken ct)
    {
        EnsureConfigured();
        using var response = await http.PostAsync("https://api.line.me/oauth2/v2.1/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken,
            ["client_id"] = _options.ChannelId,
        }), ct);

        if (!response.IsSuccessStatusCode)
            throw new DomainException("line_token_invalid", "LINE login token is invalid or expired.");

        var payload = await response.Content.ReadFromJsonAsync<VerifyResponse>(ct)
                      ?? throw new DomainException("line_token_invalid", "LINE login token is invalid.");
        return new LineProfile(payload.Sub, string.IsNullOrWhiteSpace(payload.Name) ? "LINE User" : payload.Name, payload.Picture);
    }

    public async Task<LineProfile> ExchangeCodeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ChannelId,
            ["client_secret"] = _options.ChannelSecret,
        };
        if (!string.IsNullOrEmpty(codeVerifier)) form["code_verifier"] = codeVerifier;

        using var response = await http.PostAsync("https://api.line.me/oauth2/v2.1/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw new DomainException("line_code_invalid", "LINE authorization code is invalid or expired.");

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrEmpty(token?.IdToken))
            throw new DomainException("line_code_invalid", "LINE did not return an id_token. Is the 'openid' scope enabled?");
        return await VerifyIdTokenAsync(token.IdToken, ct);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ChannelId))
            throw new DomainException("line_not_configured", "LINE Login is not configured on this server (Auth:Line:ChannelId).");
    }

    private sealed record VerifyResponse(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picture")] string? Picture);

    private sealed record TokenResponse([property: JsonPropertyName("id_token")] string? IdToken);
}
