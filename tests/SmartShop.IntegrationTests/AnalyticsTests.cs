using System.Net;
using SmartShop.IntegrationTests.Infrastructure;
using SmartShop.Modules.Analytics.Data;

namespace SmartShop.IntegrationTests;

public sealed record AnalyticsTotalsDto(int Visitors, int ShopViews, int ItemViewers, int CartAdders, int CartViewers, int Buyers,
    int Orders, decimal Revenue, int CompletedOrders, decimal CompletedRevenue, int LostOrders, int AbandonedCarts, int RepeatOrders);
public sealed record AnalyticsDayDto(DateOnly Day, int Visitors, int Orders, decimal Revenue);
public sealed record AnalyticsItemDto(Guid ItemId, string Name, int Views, int Viewers, int AddToCart, int Buyers, int OrderedQuantity,
    decimal Conversion, bool LowConversion);
public sealed record AnalyticsDashboardDto(DateOnly From, DateOnly To, AnalyticsTotalsDto Totals, List<AnalyticsDayDto> Daily,
    List<AnalyticsItemDto> Items, int[][] Heatmap, DateTimeOffset? UpdatedAt);
public sealed record PresenceDto(int Count);
public sealed record AcceptedDto(int Accepted);

public class AnalyticsTests(SmartShopFactory factory)
{
    private sealed record Shop(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid ItemId, Guid CashId);

    private async Task<Shop> OpenShopAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "ข้าวมันไก่", price = 40 });
        return new Shop(plant, admin, owner, shopId, item.Item.Id, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id);
    }

    private static async Task<OrderDto> OrderAsync(Shop s, TestClient customer, int quantity = 1)
    {
        await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = s.ShopId, itemId = s.ItemId, quantity });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
    }

    private static Task<AcceptedDto> SendAsync(TestClient client, params object[] events) => client.PostAsync<AcceptedDto>("/api/analytics/events", events);

    private async Task<AnalyticsDashboardDto> DashboardAsync(Shop s)
    {
        await factory.RunJobAsync("analytics.rollup");
        return await s.Owner.GetAsync<AnalyticsDashboardDto>($"/api/merchant/shops/{s.ShopId}/analytics?days=7");
    }

    [Fact]
    public async Task Browsing_and_orders_are_rolled_up_into_anonymous_dashboard_numbers()
    {
        var s = await OpenShopAsync();
        var buyer = await factory.JoinAsync(s.Plant, s.Admin);
        var browser = await factory.JoinAsync(s.Plant, s.Admin);

        // A shop view sent twice in one batch (a page rendering twice) counts once; unknown types are dropped.
        (await SendAsync(buyer,
            new { type = "shop_view", shopId = s.ShopId },
            new { type = "shop_view", shopId = s.ShopId },
            new { type = "item_view", shopId = s.ShopId, itemId = s.ItemId },
            new { type = "add_to_cart", shopId = s.ShopId, itemId = s.ItemId, quantity = 2 },
            new { type = "add_to_cart", shopId = s.ShopId, quantity = 1, custom = true },
            new { type = "cart_view", shopId = s.ShopId },
            new { type = "who_is_this", shopId = s.ShopId })).Accepted.ShouldBe(5);
        (await SendAsync(browser,
            new { type = "shop_view", shopId = s.ShopId },
            new { type = "item_view", shopId = s.ShopId, itemId = s.ItemId },
            new { type = "add_to_cart", shopId = s.ShopId, itemId = s.ItemId, quantity = 1 })).Accepted.ShouldBe(3);

        var first = await OrderAsync(s, buyer, 2);
        await buyer.PostOkAsync($"/api/orders/{first.Id}/cancel", new { reason = "สั่งผิด" });
        await OrderAsync(s, buyer, 1);

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var d = await DashboardAsync(s);
            d.Totals.Orders.ShouldBe(2);
            d.Totals.LostOrders.ShouldBe(1);
        });
        var dashboard = await DashboardAsync(s);
        var t = dashboard.Totals;
        t.Visitors.ShouldBe(2);
        t.ShopViews.ShouldBe(2);
        t.ItemViewers.ShouldBe(2);
        t.CartAdders.ShouldBe(2);
        t.CartViewers.ShouldBe(1);
        t.Buyers.ShouldBe(1);
        t.AbandonedCarts.ShouldBe(1); // the browser added but did not order
        t.Revenue.ShouldBe(120);
        t.RepeatOrders.ShouldBe(1); // the second order of the same customer at this shop

        var item = dashboard.Items.Single();
        item.Name.ShouldBe("ข้าวมันไก่");
        item.Views.ShouldBe(2);
        item.Viewers.ShouldBe(2);
        item.AddToCart.ShouldBe(2);
        item.Buyers.ShouldBe(1);
        item.OrderedQuantity.ShouldBe(3);
        item.Conversion.ShouldBe(0.5m);

        dashboard.Daily.Count.ShouldBe(7);
        dashboard.Daily[^1].Visitors.ShouldBe(2);
        dashboard.Heatmap.SelectMany(r => r).Sum().ShouldBe(2);
        dashboard.UpdatedAt.ShouldNotBeNull();

        // Nothing in the raw table can name a person: only day-scoped keys.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        var visitors = await db.Events.Where(e => e.ShopId == s.ShopId).Select(e => e.Visitor).Distinct().ToListAsync();
        visitors.Count.ShouldBe(2);
        visitors.ShouldAllBe(v => v.Length == 16 && !v.Contains(buyer.UserId.ToString("N").Substring(0, 8)));
    }

    [Fact]
    public async Task The_shops_own_people_are_not_counted()
    {
        var s = await OpenShopAsync();
        (await SendAsync(s.Owner, new { type = "shop_view", shopId = s.ShopId }, new { type = "item_view", shopId = s.ShopId, itemId = s.ItemId }))
            .Accepted.ShouldBe(0);
        (await s.Owner.PostAsync<PresenceDto>($"/api/analytics/presence/{s.ShopId}")).Count.ShouldBe(0);

        var dashboard = await DashboardAsync(s);
        dashboard.Totals.Visitors.ShouldBe(0);
        dashboard.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shops_of_another_village_are_rejected()
    {
        var s = await OpenShopAsync();
        var (otherPlant, otherAdmin) = await factory.CreatePlantAsync();
        var outsider = await factory.JoinAsync(otherPlant, otherAdmin);

        (await outsider.PostRawAsync("/api/analytics/events", new[] { new { type = "shop_view", shopId = s.ShopId } }))
            .ShouldHaveStatus(HttpStatusCode.NotFound);
        (await outsider.PostRawAsync($"/api/analytics/presence/{s.ShopId}")).ShouldHaveStatus(HttpStatusCode.NotFound);

        // ... and a later visitor from the shop's own village is not blocked by that miss.
        var villager = await factory.JoinAsync(s.Plant, s.Admin);
        (await SendAsync(villager, new { type = "shop_view", shopId = s.ShopId })).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task Batches_are_capped_and_oversized_bodies_refused()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var many = Enumerable.Range(0, 80).Select(_ => (object)new { type = "add_to_cart", shopId = s.ShopId, itemId = s.ItemId, quantity = 1 }).ToArray();
        (await SendAsync(customer, many)).Accepted.ShouldBe(50);

        var huge = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 400).Select(_ => new { type = "shop_view", shopId = s.ShopId, note = new string('x', 100) }));
        using var content = new StringContent(huge, System.Text.Encoding.UTF8, "application/json");
        (await customer.Http.PostAsync("/api/analytics/events", content)).ShouldHaveStatus(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task People_viewing_now_counts_distinct_customers_and_drops_those_who_leave()
    {
        var s = await OpenShopAsync();
        var a = await factory.JoinAsync(s.Plant, s.Admin);
        var b = await factory.JoinAsync(s.Plant, s.Admin);

        (await a.PostAsync<PresenceDto>($"/api/analytics/presence/{s.ShopId}")).Count.ShouldBe(1);
        (await a.PostAsync<PresenceDto>($"/api/analytics/presence/{s.ShopId}")).Count.ShouldBe(1); // heartbeat again: same person
        (await b.PostAsync<PresenceDto>($"/api/analytics/presence/{s.ShopId}")).Count.ShouldBe(2);
        (await s.Owner.GetAsync<PresenceDto>($"/api/merchant/shops/{s.ShopId}/analytics/live")).Count.ShouldBe(2);

        await a.DeleteOkAsync($"/api/analytics/presence/{s.ShopId}");
        (await s.Owner.GetAsync<PresenceDto>($"/api/merchant/shops/{s.ShopId}/analytics/live")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Merchant_statistics_need_a_role_in_the_shop()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        (await customer.GetRawAsync($"/api/merchant/shops/{s.ShopId}/analytics/live")).ShouldHaveStatus(HttpStatusCode.Forbidden);
        (await customer.GetRawAsync($"/api/merchant/shops/{s.ShopId}/analytics?days=30")).ShouldHaveStatus(HttpStatusCode.Forbidden);
        (await s.Owner.GetRawAsync($"/api/merchant/shops/{s.ShopId}/analytics?days=90")).ShouldHaveStatus(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Raw_events_older_than_90_days_are_deleted_and_aggregates_kept()
    {
        var s = await OpenShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await SendAsync(customer, new { type = "shop_view", shopId = s.ShopId });
        await factory.RunJobAsync("analytics.rollup");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
            db.Events.Add(new AnalyticsEvent
            {
                PlantId = s.Plant.Id,
                ShopId = s.ShopId,
                Type = AnalyticsEventType.ShopView,
                Visitor = "0123456789abcdef",
                OccurredAt = DateTimeOffset.UtcNow.AddDays(-91),
            });
            await db.SaveChangesAsync();
        }

        await factory.RunJobAsync("analytics.retention");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
            var left = await db.Events.Where(e => e.ShopId == s.ShopId).ToListAsync();
            left.Count.ShouldBe(1);
            left.Single().OccurredAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(-1));
            (await db.ShopDaily.CountAsync(d => d.ShopId == s.ShopId)).ShouldBe(1);
        }
    }
}
