using System.Data.Common;

namespace SmartShop.Contracts.Reviews
{
    /// <summary><see cref="CustomerId"/> is for internal bookkeeping only: when <see cref="Anonymous"/>, nothing the shop or other
    /// customers can see (notifications, webhooks, statistics) may name or link the reviewer.</summary>
    public sealed record ReviewSubmitted(Guid PlantId, Guid ReviewId, Guid OrderId, Guid ShopId, Guid CustomerId, int Rating, decimal Average, int Count,
        bool Anonymous = false) : IntegrationEvent(PlantId);
    public sealed record ReviewModerated(Guid PlantId, Guid ReviewId, Guid ShopId, Guid ActorId, bool Hidden, decimal Average, int Count) : IntegrationEvent(PlantId);
    public sealed record ReviewReplied(Guid PlantId, Guid ReviewId, Guid ShopId, Guid CustomerId) : IntegrationEvent(PlantId);

    public sealed record ShopRating(decimal Average, int Count);

    /// <summary>The shop's rating as it is now; the figures inside review events can arrive out of order.</summary>
    public interface IShopRatingSource
    {
        Task<ShopRating> GetAsync(Guid shopId, CancellationToken ct = default);
    }
}

namespace SmartShop.Contracts.Promotions
{
    public sealed record DiscountResult(Guid PromotionId, string? Code, decimal Discount, string Description);

    public interface IPromotionService
    {
        /// <summary>Best applicable promotion: the coupon code when given, otherwise the best auto-applied shop promotion.</summary>
        Task<DiscountResult?> EvaluateAsync(Guid shopId, Guid userId, string? code, decimal subtotal, DateTimeOffset now, CancellationToken ct = default);

        /// <summary>Records a redemption inside the checkout transaction, enforcing usage limits atomically.</summary>
        Task RedeemAsync(DbTransaction transaction, Guid promotionId, Guid orderId, Guid userId, decimal discount, CancellationToken ct = default);
    }
}
