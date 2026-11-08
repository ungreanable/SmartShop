using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartShop.Contracts.Ordering;
using SmartShop.IntegrationTests.Infrastructure;
using Wolverine;

namespace SmartShop.IntegrationTests;

public sealed record RoundViewDto(Guid Id, int? MinTotalQuantity, int OrderedQuantity, string Status);
public sealed record RoundItemDto(Guid Id, RoundViewDto? Round);
public sealed record RoundCategoryDto(List<RoundItemDto> Items);
public sealed record RoundMenuDto(List<RoundCategoryDto> Categories);

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

    [Fact]
    public async Task Cancelled_order_no_longer_shows_its_payment_as_open()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var order = await PlaceOrderAsync(customer, s);
        await FactoryExtensions.EventuallyAsync(async () =>
            (await customer.GetAsync<OrderDto>($"/api/orders/{order.Id}")).PaymentStatus.ShouldBe("Unpaid"));

        await customer.PostOkAsync($"/api/orders/{order.Id}/cancel", new { reason = "สั่งผิด" });

        await FactoryExtensions.EventuallyAsync(async () =>
            (await customer.GetAsync<OrderDto>($"/api/orders/{order.Id}")).PaymentStatus.ShouldBe("Voided"));
    }

    [Fact]
    public async Task Shop_members_hear_when_their_suspended_shop_is_reinstated()
    {
        var s = await OpenShopAsync();
        await s.Admin.PostOkAsync($"/api/plant/admin/shops/{s.ShopId}/suspend", new { reason = "ตรวจสอบ" });
        await s.Admin.PostOkAsync($"/api/plant/admin/shops/{s.ShopId}/reinstate");

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var inbox = await InboxAsync(s.Owner);
            inbox.ShouldContain(n => n.Type == "shop.suspended");
            inbox.ShouldContain(n => n.Type == "shop.reinstated" && n.Link == $"/merchant/{s.ShopId}");
        });
    }

    [Fact]
    public async Task Shop_hears_about_an_order_that_expired_because_nobody_accepted_it()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var order = await PlaceOrderAsync(customer, s);

        await factory.Services.GetRequiredService<IMessageBus>().InvokeAsync(new ExpireOrderIfNotAccepted(order.Id));

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            (await InboxAsync(customer)).ShouldContain(n => n.Type == "order.expired");
            (await InboxAsync(s.Owner)).ShouldContain(n => n.Type == "order.expired" && n.Title.Contains(order.OrderNo)
                                                          && n.Link == $"/merchant/{s.ShopId}/orders/{order.Id}");
        });
    }

    [Fact]
    public async Task Reminder_for_a_custom_order_asks_for_prices_not_for_a_yes_or_no()
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

        await factory.Services.GetRequiredService<IMessageBus>().InvokeAsync(new RemindShopPendingOrder(order.Id, 1));

        await FactoryExtensions.EventuallyAsync(async () =>
            (await InboxAsync(s.Owner)).ShouldContain(n => n.Type == "order.reminder" && n.Title.Contains("ยังไม่ได้ใส่ราคา")
                                                          && n.Body.Contains("ใส่ราคา")));
    }

    [Fact]
    public async Task Member_who_left_the_village_stops_getting_the_shops_order_alerts()
    {
        var s = await OpenShopAsync();
        var manager = await factory.JoinAsync(s.Plant, s.Admin, "ผู้จัดการ");
        var invite = await s.Owner.PostAsync<InviteDto>($"/api/merchant/shops/{s.ShopId}/invites", new { role = "Manager" });
        await manager.PostOkAsync($"/api/shop-invites/{invite.Code}/accept");
        await manager.PostOkAsync($"/api/plants/{s.Plant.Id}/leave");

        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var order = await PlaceOrderAsync(customer, s);

        await FactoryExtensions.EventuallyAsync(async () =>
            (await InboxAsync(s.Owner)).ShouldContain(n => n.Type == "order.new" && n.Title.Contains(order.OrderNo)));
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await InboxAsync(manager)).ShouldNotContain(n => n.Type == "order.new");
    }

    [Fact]
    public async Task Group_buy_total_is_live_on_the_menu_and_counts_each_order_once()
    {
        var s = await OpenShopAsync();
        var now = DateTimeOffset.UtcNow;
        var round = await s.Owner.PostAsync<RoundViewDto>($"/api/merchant/shops/{s.ShopId}/catalog/rounds", new
        {
            title = "ทุเรียนรอบนี้", orderCutoff = now.AddDays(1), fulfillFrom = now.AddDays(2), fulfillTo = now.AddDays(2).AddHours(3), minTotalQuantity = 10,
        });
        var item = await s.Owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{s.ShopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "ทุเรียน", price = 300, preOrderRoundId = round.Id });
        var customer = await factory.JoinAsync(s.Plant, s.Admin);

        async Task<int> OrderedAsync() => (await customer.GetAsync<RoundMenuDto>($"/api/shops/{s.ShopId}/menu"))
            .Categories.SelectMany(c => c.Items).Single(i => i.Id == item.Item.Id).Round!.OrderedQuantity;

        (await OrderedAsync()).ShouldBe(0); // the menu is now cached

        async Task<OrderDto> BuyAsync(TestClient who, int quantity)
        {
            await who.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            {
                Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }, options: TestClient.Json),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return await TestClient.ReadAsync<OrderDto>(await who.Http.SendAsync(request));
        }

        var first = await BuyAsync(customer, 2);
        var neighbour = await factory.JoinAsync(s.Plant, s.Admin);
        await BuyAsync(neighbour, 3);
        // Within seconds, not when the cached menu runs out.
        await FactoryExtensions.EventuallyAsync(async () => (await OrderedAsync()).ShouldBe(5), timeoutMs: 10_000);

        await customer.PostOkAsync($"/api/orders/{first.Id}/cancel", new { reason = "ไม่อยู่" });
        await FactoryExtensions.EventuallyAsync(async () => (await OrderedAsync()).ShouldBe(3), timeoutMs: 10_000);

        // At-least-once delivery: the same cancellation arriving again must not take the order back twice.
        await factory.Services.GetRequiredService<IMessageBus>().PublishAsync(new OrderCancelled(s.Plant.Id, first.Id, first.OrderNo, s.ShopId, "ร้าน",
            customer.UserId, customer.UserId, true, "ไม่อยู่", null, [new OrderLineInfo(item.Item.Id, "ทุเรียน", 2, 300, null)]));
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await OrderedAsync()).ShouldBe(3);
    }
}
