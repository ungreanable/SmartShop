using Microsoft.EntityFrameworkCore;
using SmartShop.Contracts.Identity;
using SmartShop.Modules.Plants.Data;

namespace SmartShop.Modules.Plants.Handlers;

public static class UserErasedHandler
{
    public static async Task Handle(UserErased e, PlantsDbContext db, CancellationToken ct)
    {
        var memberships = await db.Memberships.Where(m => m.UserId == e.UserId).ToListAsync(ct);
        foreach (var membership in memberships) membership.Anonymise();
    }
}
