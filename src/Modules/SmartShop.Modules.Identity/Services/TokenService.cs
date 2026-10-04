using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartShop.Infrastructure.Auth;
using SmartShop.Modules.Identity.Data;
using SmartShop.Modules.Identity.Domain;

namespace SmartShop.Modules.Identity.Services;

public sealed record AuthUser(Guid Id, string DisplayName, string? PictureUrl, bool IsSystemAdmin, bool HasConsent, string Locale);

public sealed record AuthResult(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, AuthUser User);

internal sealed class TokenService(IdentityDbContext db, IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _jwt = options.Value;

    public async Task<AuthResult> IssueAsync(User user, string? device, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var refresh = NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken(user.Id, Hash(refresh), now.AddDays(_jwt.RefreshTokenDays), device, now));
        await db.SaveChangesAsync(ct);
        return Build(user, refresh, now);
    }

    /// <summary>
    /// Rotates a refresh token. Presenting an already-rotated token is treated as theft:
    /// every session of that user is revoked.
    /// </summary>
    public async Task<AuthResult?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return null;

        if (!token.IsActive(now))
        {
            if (token.ReplacedById is not null)
                await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId && u.ErasedAt == null, ct);
        if (user is null) return null;

        var next = NewRefreshToken();
        var replacement = new RefreshToken(user.Id, Hash(next), now.AddDays(_jwt.RefreshTokenDays), token.Device, now);
        db.RefreshTokens.Add(replacement);
        token.Revoke(now, replacement.Id);
        await db.SaveChangesAsync(ct);
        return Build(user, next, now);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var now = clock.GetUtcNow();
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private AuthResult Build(User user, string refresh, DateTimeOffset now)
    {
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(SmartShopClaims.UserId, user.Id.ToString()),
            new(SmartShopClaims.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (user.IsSystemAdmin) claims.Add(new Claim(SmartShopClaims.SystemAdmin, "true"));

        var token = new JwtSecurityToken(
            _jwt.Issuer, _jwt.Audience, claims,
            notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_jwt.GetKey(), SecurityAlgorithms.HmacSha256));

        return new AuthResult(
            new JwtSecurityTokenHandler().WriteToken(token), expires, refresh,
            new AuthUser(user.Id, user.DisplayName, user.PictureUrl, user.IsSystemAdmin, user.ConsentVersion is not null, user.Locale));
    }

    private static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
