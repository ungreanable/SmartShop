using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartShop.Contracts.Promotions;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Promotions;

/// <summary>Called by Checkout (Ordering) through <see cref="IPromotionService"/>; redemption joins the checkout transaction.</summary>
internal sealed class PromotionService(PromotionsDbContext db) : IPromotionService
{
    public async Task<DiscountResult?> EvaluateAsync(Guid shopId, Guid userId, string? code, decimal subtotal, DateTimeOffset now, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        var candidates = await db.Promotions.AsNoTracking()
            .Where(p => p.ShopId == shopId && p.Active && (normalized != null ? p.Code == normalized : p.AutoApply))
            .ToListAsync(ct);
        candidates = candidates.Where(p => p.IsLive(now)).ToList();
        if (candidates.Count == 0) return null;

        var limited = candidates.Where(p => p.PerUserLimit is not null).Select(p => p.Id).ToList();
        var used = limited.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Redemptions.AsNoTracking()
                .Where(r => limited.Contains(r.PromotionId) && r.UserId == userId && r.Status == RedemptionStatus.Active)
                .GroupBy(r => r.PromotionId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var best = candidates
            .Where(p => p.PerUserLimit is null || used.GetValueOrDefault(p.Id) < p.PerUserLimit)
            .Select(p => (Promotion: p, Discount: p.DiscountFor(subtotal)))
            .Where(x => x.Discount > 0)
            .OrderByDescending(x => x.Discount)
            .FirstOrDefault();
        return best.Promotion is null ? null : new DiscountResult(best.Promotion.Id, best.Promotion.Code, best.Discount, best.Promotion.Describe());
    }

    public async Task RedeemAsync(DbTransaction transaction, Guid promotionId, Guid orderId, Guid userId, decimal discount, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)transaction.Connection!;

        // Claim one use atomically; the row lock also serialises the per-user check below.
        await using (var claim = new NpgsqlCommand("""
            UPDATE promotions.promotions SET used_count = used_count + 1
            WHERE id = @id AND active AND (usage_limit IS NULL OR used_count < usage_limit)
            RETURNING per_user_limit
            """, connection, (NpgsqlTransaction)transaction))
        {
            claim.Parameters.AddWithValue("id", promotionId);
            await using var reader = await claim.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ConflictException("coupon_exhausted", "This promotion has been fully used.");
            var perUser = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            await reader.CloseAsync();

            if (perUser is { } limit)
            {
                await using var count = new NpgsqlCommand(
                    "SELECT count(*) FROM promotions.redemptions WHERE promotion_id = @id AND user_id = @user AND status = 'Active'",
                    connection, (NpgsqlTransaction)transaction);
                count.Parameters.AddWithValue("id", promotionId);
                count.Parameters.AddWithValue("user", userId);
                if ((long)(await count.ExecuteScalarAsync(ct))! >= limit)
                    throw new ConflictException("coupon_used", "You have already used this promotion.");
            }
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO promotions.redemptions (id, promotion_id, order_id, user_id, discount, status, at)
            VALUES (@id, @promotion, @order, @user, @discount, 'Active', now())
            """, connection, (NpgsqlTransaction)transaction);
        insert.Parameters.AddWithValue("id", Ids.New());
        insert.Parameters.AddWithValue("promotion", promotionId);
        insert.Parameters.AddWithValue("order", orderId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("discount", discount);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Gives the use back when an order did not happen. Idempotent: only an active redemption is released.</summary>
    public static Task ReleaseAsync(PromotionsDbContext db, Guid orderId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH released AS (
                UPDATE promotions.redemptions SET status = 'Released', released_at = now()
                WHERE order_id = {orderId} AND status = 'Active'
                RETURNING promotion_id)
            UPDATE promotions.promotions p SET used_count = greatest(p.used_count - 1, 0)
            FROM released WHERE p.id = released.promotion_id
            """, ct);
}
