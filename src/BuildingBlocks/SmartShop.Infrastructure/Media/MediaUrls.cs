using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SmartShop.Infrastructure.Media;

/// <summary>
/// Issues short-lived signed URLs for media. Images cannot carry an Authorization header, so access control
/// happens when the URL is issued (only to authorised viewers) and the signature is checked when it is fetched.
/// Expiry is rounded to the hour so the same URL is reused (and browser-cached) within that hour.
/// </summary>
public interface IMediaUrls
{
    string? For(Guid? mediaId, MediaVariant variant = MediaVariant.Medium);

    bool Verify(Guid mediaId, MediaVariant variant, long expires, string signature);
}

public enum MediaVariant { Small, Medium, Original }

public sealed class MediaUrls : IMediaUrls
{
    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public MediaUrls(IConfiguration configuration, TimeProvider clock)
    {
        var key = configuration["Media:SigningKey"] ?? configuration["Auth:Jwt:SigningKey"]
                  ?? throw new InvalidOperationException("Media:SigningKey is not configured.");
        _key = Encoding.UTF8.GetBytes(key);
        _clock = clock;
    }

    public string? For(Guid? mediaId, MediaVariant variant = MediaVariant.Medium)
    {
        if (mediaId is not { } id) return null;
        var now = _clock.GetUtcNow();
        var expires = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(2).ToUnixTimeSeconds();
        return $"/api/media/{id}/{Name(variant)}?exp={expires}&sig={Sign(id, variant, expires)}";
    }

    public bool Verify(Guid mediaId, MediaVariant variant, long expires, string signature)
    {
        if (DateTimeOffset.FromUnixTimeSeconds(expires) < _clock.GetUtcNow()) return false;
        var expected = Sign(mediaId, variant, expires);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    public static string Name(MediaVariant variant) => variant switch
    {
        MediaVariant.Small => "sm",
        MediaVariant.Original => "orig",
        _ => "md",
    };

    public static MediaVariant? Parse(string name) => name switch
    {
        "sm" => MediaVariant.Small,
        "md" => MediaVariant.Medium,
        "orig" => MediaVariant.Original,
        _ => null,
    };

    private string Sign(Guid id, MediaVariant variant, long expires)
    {
        var data = Encoding.UTF8.GetBytes($"{id:N}|{(int)variant}|{expires}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, data))[..32];
    }
}
