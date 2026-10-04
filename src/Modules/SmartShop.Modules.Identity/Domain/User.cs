using SmartShop.SharedKernel;

namespace SmartShop.Modules.Identity.Domain;

public sealed class User
{
    private User() { }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string? PictureUrl { get; private set; }
    public string? Phone { get; private set; }
    public string Locale { get; private set; } = "th";
    public bool IsSystemAdmin { get; private set; }
    public string? ConsentVersion { get; private set; }
    public DateTimeOffset? ConsentAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastLoginAt { get; private set; }
    public DateTimeOffset? ErasedAt { get; private set; }

    public List<ExternalLogin> Logins { get; private set; } = [];

    public static User Register(string provider, string providerKey, string displayName, string? pictureUrl, DateTimeOffset now)
    {
        var user = new User
        {
            Id = Ids.New(),
            DisplayName = Guard.NotEmpty(displayName, "Display name", 100),
            PictureUrl = pictureUrl,
            CreatedAt = now,
            LastLoginAt = now,
        };
        user.Logins.Add(new ExternalLogin(user.Id, provider, providerKey, now));
        return user;
    }

    public string? LineUserId => Logins.FirstOrDefault(l => l.Provider == LoginProviders.Line)?.ProviderKey;

    public void RecordLogin(string? displayName, string? pictureUrl, DateTimeOffset now)
    {
        EnsureNotErased();
        // Keep the LINE profile fresh unless the user customised their name in SmartShop.
        if (!string.IsNullOrWhiteSpace(displayName) && !NameCustomised) DisplayName = displayName.Trim();
        if (pictureUrl is not null) PictureUrl = pictureUrl;
        LastLoginAt = now;
    }

    public bool NameCustomised { get; private set; }

    public void UpdateProfile(string displayName, string? phone, string? locale)
    {
        EnsureNotErased();
        var name = Guard.NotEmpty(displayName, "Display name", 100);
        if (name != DisplayName) NameCustomised = true;
        DisplayName = name;
        Phone = NormalizePhone(phone);
        if (locale is "th" or "en") Locale = locale;
    }

    public void GiveConsent(string version, DateTimeOffset now)
    {
        ConsentVersion = Guard.NotEmpty(version, "Consent version", 20);
        ConsentAt = now;
    }

    public void SetSystemAdmin(bool value) => IsSystemAdmin = value;

    public void LinkLogin(string provider, string providerKey, DateTimeOffset now)
    {
        EnsureNotErased();
        if (Logins.Any(l => l.Provider == provider))
            throw new ConflictException("login_already_linked", $"A {provider} login is already linked.");
        Logins.Add(new ExternalLogin(Id, provider, providerKey, now));
    }

    /// <summary>PDPA erasure: drop personal data but keep the row so historic orders stay consistent.</summary>
    public void Erase(DateTimeOffset now)
    {
        DisplayName = "ผู้ใช้ที่ลบบัญชีแล้ว";
        PictureUrl = null;
        Phone = null;
        ErasedAt = now;
        Logins.Clear();
    }

    private void EnsureNotErased()
    {
        if (ErasedAt is not null) throw new DomainException("user_erased", "This account has been deleted.");
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length is < 9 or > 15) throw new DomainException("validation", "Phone number is invalid.");
        return digits;
    }
}

public sealed class ExternalLogin
{
    private ExternalLogin() { }

    public ExternalLogin(Guid userId, string provider, string providerKey, DateTimeOffset now)
    {
        UserId = userId;
        Provider = provider;
        ProviderKey = providerKey;
        LinkedAt = now;
    }

    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = "";
    public string ProviderKey { get; private set; } = "";
    public DateTimeOffset LinkedAt { get; private set; }
}

public static class LoginProviders
{
    public const string Line = "line";
    public const string Google = "google";
    public const string Phone = "phone";
    public const string Dev = "dev";
}

public sealed class RefreshToken
{
    private RefreshToken() { }

    public RefreshToken(Guid userId, string tokenHash, DateTimeOffset expiresAt, string? device, DateTimeOffset now)
    {
        Id = Ids.New();
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        Device = device;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public string? Device { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedById { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, Guid? replacedBy = null)
    {
        RevokedAt ??= now;
        ReplacedById ??= replacedBy;
    }
}

/// <summary>One-time code for phone sign-in (stored hashed, short-lived, limited attempts).</summary>
public sealed class PhoneOtp
{
    private PhoneOtp() { }

    public PhoneOtp(string phone, string codeHash, DateTimeOffset now)
    {
        Id = Ids.New();
        Phone = phone;
        CodeHash = codeHash;
        CreatedAt = now;
        ExpiresAt = now.AddMinutes(5);
    }

    public Guid Id { get; private set; }
    public string Phone { get; private set; } = "";
    public string CodeHash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public bool TryUse(string codeHash, DateTimeOffset now)
    {
        if (UsedAt is not null || now > ExpiresAt || Attempts >= 5) return false;
        Attempts++;
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(codeHash), System.Text.Encoding.ASCII.GetBytes(CodeHash))) return false;
        UsedAt = now;
        return true;
    }
}
