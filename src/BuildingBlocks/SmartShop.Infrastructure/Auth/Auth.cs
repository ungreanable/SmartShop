using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace SmartShop.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string Section = "Auth:Jwt";

    public string Issuer { get; set; } = "smartshop";
    public string Audience { get; set; } = "smartshop";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Must come from a secret in production.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    public SymmetricSecurityKey GetKey()
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < 32)
            throw new InvalidOperationException("Auth:Jwt:SigningKey must be at least 32 bytes.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
    }
}

public static class SmartShopClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string SystemAdmin = "sys_admin";
}

public static class Policies
{
    public const string SystemAdmin = "SystemAdmin";
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's id. Throws when anonymous.</summary>
    Guid Id { get; }

    bool IsSystemAdmin { get; }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid Id =>
        Guid.TryParse(Principal?.FindFirstValue(SmartShopClaims.UserId), out var id)
            ? id
            : throw new UnauthorizedAccessException("Authentication required.");

    public bool IsSystemAdmin => Principal?.HasClaim(SmartShopClaims.SystemAdmin, "true") == true;
}

public static class AuthSetup
{
    public static IServiceCollection AddSmartShopAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = jwt.GetKey(),
                    NameClaimType = SmartShopClaims.Name,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                // SignalR sends the token as a query string parameter for WebSockets/SSE.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SystemAdmin, p => p.RequireClaim(SmartShopClaims.SystemAdmin, "true"));

        return services;
    }
}
