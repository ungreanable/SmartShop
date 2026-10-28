using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SmartShop.Modules.Analytics.Services;

/// <summary>
/// Pseudonymous keys so analytics never stores a user id (PDPA).
/// <list type="bullet">
/// <item><see cref="Visitor"/>: changes every Bangkok day, so unique visitors can be counted per day but one person
/// cannot be followed across days, and without the secret the key cannot be turned back into a user.</item>
/// <item><see cref="Customer"/>: stable but per shop, used only to count repeat orders (<c>shop_customers</c>).</item>
/// </list>
/// Each purpose gets its own key derived from the secret, so the fallback to the JWT/media signing key never reuses that key
/// as-is and the two hashes cannot be correlated. The secret is never logged.
/// </summary>
public sealed class VisitorKeys
{
    public static readonly TimeSpan BangkokOffset = TimeSpan.FromHours(7);

    private readonly byte[] _visitorKey;
    private readonly byte[] _customerKey;

    public VisitorKeys(IConfiguration configuration)
        : this(configuration["Analytics:Secret"] is { Length: > 0 } own ? own
               : configuration["Media:SigningKey"] is { Length: > 0 } media ? media
               : configuration["Auth:Jwt:SigningKey"] is { Length: > 0 } jwt ? jwt
               : throw new InvalidOperationException("Analytics:Secret (or Auth:Jwt:SigningKey) is not configured."))
    {
    }

    public VisitorKeys(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        _visitorKey = HMACSHA256.HashData(bytes, "smartshop.analytics.visitor"u8);
        _customerKey = HMACSHA256.HashData(bytes, "smartshop.analytics.repeat-customer"u8);
    }

    /// <summary>Thailand has no daylight saving: a fixed +7 matches what the SQL rollup uses.</summary>
    public static DateOnly BangkokDay(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(BangkokOffset).DateTime);

    public static DateTimeOffset StartOf(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), BangkokOffset).ToUniversalTime();

    /// <summary>First 16 hex of HMAC-SHA256(key, "{userId}:{yyyy-MM-dd}"). Invariant culture: the Thai culture would format a Buddhist year.</summary>
    public string Visitor(Guid userId, DateOnly day) =>
        Hash(_visitorKey, string.Create(CultureInfo.InvariantCulture, $"{userId}:{day:yyyy-MM-dd}"), 16);

    public string Customer(Guid shopId, Guid userId) => Hash(_customerKey, $"{shopId}:{userId}", 32);

    private static string Hash(byte[] key, string value, int length) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)))[..length];
}
