using System.Data.Common;

namespace SmartShop.Contracts.Reviews
{
    public sealed record ReviewSubmitted(Guid PlantId, Guid ReviewId, Guid OrderId, Guid ShopId, Guid CustomerId, int Rating, decimal Average, int Count) : IntegrationEvent(PlantId);
    public sealed record ReviewModerated(Guid PlantId, Guid ReviewId, Guid ShopId, Guid ActorId, bool Hidden, decimal Average, int Count) : IntegrationEvent(PlantId);
    public sealed record ReviewReplied(Guid PlantId, Guid ReviewId, Guid ShopId, Guid CustomerId) : IntegrationEvent(PlantId);
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
