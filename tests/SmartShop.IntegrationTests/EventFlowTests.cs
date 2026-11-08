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

    [Fact]
    public async Task Shop_member_ordering_from_their_own_shop_is_not_alerted_about_their_own_order()
    {
        var s = await OpenShopAsync();
        var manager = await factory.JoinAsync(s.Plant, s.Admin, "ผู้จัดการ");
        var invite = await s.Owner.PostAsync<InviteDto>($"/api/merchant/shops/{s.ShopId}/invites", new { role = "Manager" });
        await manager.PostOkAsync($"/api/shop-invites/{invite.Code}/accept");

        var order = await PlaceOrderAsync(s.Owner, s);
        await s.Owner.PostOkAsync($"/api/orders/{order.Id}/cancel", new { reason = "ลองระบบ" });

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var inbox = await InboxAsync(manager);
            inbox.ShouldContain(n => n.Type == "order.new" && n.Title.Contains(order.OrderNo));
            inbox.ShouldContain(n => n.Type == "order.cancelled" && n.Title.Contains(order.OrderNo));
        });
        // Same events, other queues: give them the moment the manager's copies needed before checking for an absence.
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await InboxAsync(s.Owner)).ShouldNotContain(n => n.Link != null && n.Link.Contains(order.Id.ToString()));
    }

    [Fact]
    public async Task Customer_who_agrees_to_the_shops_prices_is_not_told_that_the_shop_accepted()
    {
        var s = await OpenShopAsync();
        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/custom-orders", new { enabled = true });
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ข้าวผัดกุ้ง" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = await TestClient.ReadAsync<QuoteOrderDto>(await customer.Http.SendAsync(request));
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/quote", new { lines = new[] { new { lineId = order.Lines.Single().Id, unitPrice = 70 } } });

        await customer.PostOkAsync($"/api/orders/{order.Id}/accept-quote");

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            (await InboxAsync(customer)).ShouldContain(n => n.Type == "order.quoted");
            (await InboxAsync(s.Owner)).ShouldContain(n => n.Type == "order.quote_accepted");
        });
        // Published together with the shop's "customer accepted the price": give its queue the same moment.
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await InboxAsync(customer)).ShouldNotContain(n => n.Type == "order.accepted");
    }
}
