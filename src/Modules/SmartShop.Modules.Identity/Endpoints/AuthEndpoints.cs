using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartShop.Modules.Identity.Domain;
using SmartShop.Modules.Identity.Services;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Identity.Endpoints;

public sealed record LineIdTokenRequest(string IdToken, string? Device);
public sealed record LineCodeRequest(string Code, string RedirectUri, string? CodeVerifier, string? Device);
public sealed record RefreshRequest(string RefreshToken);
public sealed record DevLoginRequest(string Key, string DisplayName, bool SystemAdmin = false);
public sealed record LineConfigResponse(string ChannelId, string LiffId, string AuthorizeUrl);

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").AllowAnonymous().RequireRateLimiting("auth");

        auth.MapGet("/line/config", (IOptions<LineLoginOptions> options) =>
            new LineConfigResponse(options.Value.ChannelId, options.Value.LiffId, "https://access.line.me/oauth2/v2.1/authorize"));

        // LIFF / web: the client already has an id_token from LINE.
        auth.MapPost("/line", async (LineIdTokenRequest req, ILineLoginClient line, SignInService signIn, CancellationToken ct) =>
        {
            var profile = await line.VerifyIdTokenAsync(Guard.NotEmpty(req.IdToken, "idToken", 4000), ct);
            return await signIn.SignInAsync(LoginProviders.Line, profile.UserId, profile.DisplayName, profile.PictureUrl, req.Device, ct);
        });

        // OAuth authorization code (+PKCE) for the mobile app and browsers outside LINE.
        auth.MapPost("/line/code", async (LineCodeRequest req, ILineLoginClient line, SignInService signIn, CancellationToken ct) =>
        {
            var profile = await line.ExchangeCodeAsync(req.Code, req.RedirectUri, req.CodeVerifier, ct);
            return await signIn.SignInAsync(LoginProviders.Line, profile.UserId, profile.DisplayName, profile.PictureUrl, req.Device, ct);
        });

        // LINE only redirects to http(s) URLs; bounce the code to the mobile app's custom scheme.
        auth.MapGet("/line/mobile-callback", (string? code, string? state, string? error, IOptions<LineLoginOptions> options) =>
        {
            var query = QueryString.Create(new Dictionary<string, string?> { ["code"] = code, ["state"] = state, ["error"] = error });
            return Results.Redirect($"{options.Value.MobileRedirectScheme}://auth{query}");
        });

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
            await tokens.RefreshAsync(req.RefreshToken, ct) is { } result
                ? Results.Ok(result)
                : Results.Problem(statusCode: 401, title: "Refresh token is invalid or expired.", extensions: new Dictionary<string, object?> { ["code"] = "refresh_invalid" }));

        auth.MapPost("/logout", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken, ct);
            return Results.NoContent();
        });

        // Development / demo only: sign in without LINE. Never enabled in Production.
        auth.MapPost("/dev", async (DevLoginRequest req, SignInService signIn, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
        {
            if (!config.GetValue("Auth:DevLogin:Enabled", false) || env.IsProduction())
                return Results.NotFound();

            return Results.Ok(await signIn.SignInAsync(LoginProviders.Dev, Guard.NotEmpty(req.Key, "key", 100),
                Guard.NotEmpty(req.DisplayName, "displayName", 100), null, "dev", ct, grantSystemAdmin: req.SystemAdmin));
        }).ExcludeFromDescription();
    }
}
