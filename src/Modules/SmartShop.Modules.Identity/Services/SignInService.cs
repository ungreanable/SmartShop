using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartShop.Contracts.Identity;
using SmartShop.Modules.Identity.Data;
using SmartShop.Modules.Identity.Domain;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Identity.Services;

/// <summary>Finds or registers the user behind an external login and issues SmartShop tokens.</summary>
internal sealed class SignInService(
    IDbContextOutbox<IdentityDbContext> outbox,
    TokenService tokens,
    IConfiguration configuration,
    TimeProvider clock)
{
    public async Task<AuthResult> SignInAsync(
        string provider, string providerKey, string displayName, string? pictureUrl, string? device, CancellationToken ct,
        bool grantSystemAdmin = false)
    {
        var db = outbox.DbContext;
        var now = clock.GetUtcNow();

        var userId = await db.ExternalLogins
            .Where(l => l.Provider == provider && l.ProviderKey == providerKey)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(ct);

        User user;
        if (userId is { } id)
        {
            user = await db.Users.FirstAsync(u => u.Id == id, ct);
            user.RecordLogin(displayName, pictureUrl, now);
        }
        else
        {
            user = User.Register(provider, providerKey, displayName, pictureUrl, now);
            db.Users.Add(user);
            await outbox.PublishAsync(new UserRegistered(user.Id, user.DisplayName, provider));
        }

        if (grantSystemAdmin || IsBootstrapAdmin(provider, providerKey)) user.SetSystemAdmin(true);

        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return await tokens.IssueAsync(user, device, ct);
    }

    /// <summary>System administrators are configured by LINE user id (Identity:SystemAdminLineUserIds).</summary>
    private bool IsBootstrapAdmin(string provider, string providerKey)
    {
        var admins = configuration.GetSection("Identity:SystemAdminLineUserIds").Get<string[]>() ?? [];
        return provider == LoginProviders.Line && admins.Contains(providerKey, StringComparer.Ordinal);
    }
}
