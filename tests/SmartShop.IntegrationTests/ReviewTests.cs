using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record ReviewDto(Guid Id, Guid OrderId, Guid CustomerId, string CustomerName, string? CustomerPictureUrl, int Rating, string? Comment,
    string? Reply, DateTimeOffset CreatedAt, DateTimeOffset? RepliedAt, bool Hidden);
public sealed record ReviewSummaryDto(decimal Average, int Count, Dictionary<int, int> Distribution, List<ReviewDto> Reviews);

public class ReviewTests(SmartShopFactory factory)
{
    private async Task<(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, TestClient Customer, Guid OrderId)> CompletedOrderAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var cashId = settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id;
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "ขนมครก", price = 20 });

        var customer = await factory.JoinAsync(plant, admin);
        await customer.PostAsync<CartDto>("/api/cart/items", new { shopId, itemId = item.Item.Id, quantity = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = cashId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
        return (plant, admin, owner, shopId, customer, order.Id);
    }

    private static async Task CompleteAsync(TestClient owner, TestClient customer, Guid orderId)
    {
        await owner.PostOkAsync($"/api/merchant/orders/{orderId}/accept");
        await owner.PostOkAsync($"/api/merchant/orders/{orderId}/start");
        await owner.PostOkAsync($"/api/merchant/orders/{orderId}/ready");
        await owner.PostOkAsync($"/api/merchant/orders/{orderId}/deliver", new { });
        await customer.PostOkAsync($"/api/orders/{orderId}/confirm-received");
    }

    [Fact]
    public async Task Customer_reviews_completed_order_once_and_shop_rating_updates()
    {
        var (_, _, owner, shopId, customer, orderId) = await CompletedOrderAsync();

        var early = await customer.PostRawAsync($"/api/orders/{orderId}/review", new { rating = 5 });
        (await TestClient.ProblemCodeAsync(early)).ShouldBe("order_not_completed");

        await CompleteAsync(owner, customer, orderId);
        await customer.PostOkAsync($"/api/orders/{orderId}/review", new { rating = 4, comment = "อร่อยมาก" });

        var again = await customer.PostRawAsync($"/api/orders/{orderId}/review", new { rating = 1 });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestClient.ProblemCodeAsync(again)).ShouldBe("already_reviewed");

        var summary = await customer.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews");
        summary.Count.ShouldBe(1);
        summary.Average.ShouldBe(4);
        summary.Distribution[4].ShouldBe(1);
        summary.Reviews.Single().CustomerName.ShouldBe(customer.DisplayName);

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var shop = (await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}")).Shop;
            shop.RatingCount.ShouldBe(1);
            shop.RatingAverage.ShouldBe(4);
        });
    }

    [Fact]
    public async Task Only_the_customer_can_review_and_shop_replies_once()
    {
        var (plant, admin, owner, shopId, customer, orderId) = await CompletedOrderAsync();
        await CompleteAsync(owner, customer, orderId);

        var stranger = await factory.JoinAsync(plant, admin, "คนอื่น");
        (await stranger.PostRawAsync($"/api/orders/{orderId}/review", new { rating = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await customer.PostOkAsync($"/api/orders/{orderId}/review", new { rating = 5 });
        var reviewId = (await owner.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews")).Reviews.Single().Id;

        (await customer.PostRawAsync($"/api/merchant/reviews/{reviewId}/reply", new { reply = "ขอบคุณ" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await owner.PostOkAsync($"/api/merchant/reviews/{reviewId}/reply", new { reply = "ขอบคุณครับ" });
        (await TestClient.ProblemCodeAsync(await owner.PostRawAsync($"/api/merchant/reviews/{reviewId}/reply", new { reply = "อีกครั้ง" })))
            .ShouldBe("already_replied");

        (await customer.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews")).Reviews.Single().Reply.ShouldBe("ขอบคุณครับ");
    }

    [Fact]
    public async Task Admin_hides_review_from_customers_and_rating()
    {
        var (_, admin, owner, shopId, customer, orderId) = await CompletedOrderAsync();
        await CompleteAsync(owner, customer, orderId);
        await customer.PostOkAsync($"/api/orders/{orderId}/review", new { rating = 1, comment = "spam" });
        var reviewId = (await customer.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews")).Reviews.Single().Id;

        (await customer.PutRawAsync($"/api/plant/admin/reviews/{reviewId}/hidden", new { hidden = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await admin.PutOkAsync($"/api/plant/admin/reviews/{reviewId}/hidden", new { hidden = true });

        var publicView = await customer.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews");
        publicView.Count.ShouldBe(0);
        publicView.Reviews.ShouldBeEmpty();
        (await owner.GetAsync<ReviewSummaryDto>($"/api/shops/{shopId}/reviews")).Reviews.Single().Hidden.ShouldBeTrue();

        await FactoryExtensions.EventuallyAsync(async () =>
            (await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}")).Shop.RatingCount.ShouldBe(0));
    }
}
