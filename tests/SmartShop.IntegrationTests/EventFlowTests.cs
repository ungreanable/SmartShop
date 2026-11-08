using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

/// <summary>Every state change reaches the screens and people that must react to it, exactly once and only them.</summary>
public class EventFlowTests(SmartShopFactory factory)
{
    private sealed record Shop(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid CashId, Guid ItemId);

    private async Task<Shop> OpenShopAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "ข้าวมันไก่", price = 45 });
        return new Shop(plant, admin, owner, shopId, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id, item.Item.Id);
    }

    private static async Task<OrderDto> PlaceOrderAsync(TestClient customer, Shop s)
    {
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = s.ItemId, quantity = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
    }

    private static async Task<List<NotificationDto>> InboxAsync(TestClient client) =>
        (await client.GetAsync<PagedDto<NotificationDto>>("/api/notifications?size=100")).Items;

    [Fact]
    public async Task Customer_hears_when_the_shop_declines_their_cancellation_request()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var order = await PlaceOrderAsync(customer, s);
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");
        await customer.PostOkAsync($"/api/orders/{order.Id}/request-cancel", new { reason = "ไม่อยู่บ้าน" });

        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/decline-cancel", new { reason = "ทำเสร็จแล้วค่ะ" });

        await FactoryExtensions.EventuallyAsync(async () =>
            (await InboxAsync(customer)).ShouldContain(n => n.Type == "order.cancel_declined" && n.Body == "ทำเสร็จแล้วค่ะ"
                                                            && n.Link == $"/orders/{order.Id}"));
    }
}
