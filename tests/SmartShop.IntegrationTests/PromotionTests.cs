using System.Net;
using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record PromotionDto(Guid Id, string? Code, string Title, string Type, decimal Value, decimal? MinOrder, decimal? MaxDiscount,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UsageLimit, int? PerUserLimit, int UsedCount, bool AutoApply, bool Active);
public sealed record DiscountDto(Guid PromotionId, string? Code, decimal Discount, string Description);

public class PromotionTests(SmartShopFactory factory)
{
    private sealed record Ctx(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid CashId, Guid ItemId);

    private async Task<Ctx> ShopAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "ส้มตำ", price = 100 });
        return new Ctx(plant, admin, owner, shopId, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id, item.Item.Id);
    }

    private static async Task<HttpResponseMessage> OrderAsync(Ctx c, TestClient customer, int quantity, string? coupon)
    {
        await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = c.ShopId, itemId = c.ItemId, quantity });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = c.CashId, couponCode = coupon }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await customer.Http.SendAsync(request);
    }

    [Fact]
    public async Task Coupon_discounts_order_respects_limits_and_is_released_on_cancel()
    {
        var c = await ShopAsync();
        var promo = await c.Owner.PostAsync<PromotionDto>($"/api/merchant/shops/{c.ShopId}/promotions", new
        {
            code = "lucky10",
            title = "ลด 10%",
            type = "Percent",
            value = 10,
            minOrder = 150,
            maxDiscount = 30,
            usageLimit = 1,
            perUserLimit = 1,
            autoApply = false,
            active = true,
        });
        promo.Code.ShouldBe("LUCKY10");
        promo.AutoApply.ShouldBeFalse();

        var customer = await factory.JoinAsync(c.Plant, c.Admin);
        (await customer.PostAsync<DiscountDto>($"/api/shops/{c.ShopId}/promotions/preview", new { code = "Lucky10", subtotal = 200 })).Discount.ShouldBe(20);
        (await customer.PostAsync<DiscountDto>($"/api/shops/{c.ShopId}/promotions/preview", new { code = "LUCKY10", subtotal = 1000 })).Discount.ShouldBe(30);
        (await TestClient.ProblemCodeAsync(await customer.PostRawAsync($"/api/shops/{c.ShopId}/promotions/preview", new { code = "LUCKY10", subtotal = 100 })))
            .ShouldBe("coupon_invalid");

        var order = await TestClient.ReadAsync<OrderDto>(await OrderAsync(c, customer, 2, "lucky10"));
        order.Subtotal.ShouldBe(200);
        order.Total.ShouldBe(180);

        // Usage limit reached: another customer cannot use it.
        var other = await factory.JoinAsync(c.Plant, c.Admin, "ลูกค้าคนที่สอง");
        (await TestClient.ProblemCodeAsync(await OrderAsync(c, other, 2, "LUCKY10"))).ShouldBe("coupon_invalid");
        (await c.Owner.GetAsync<List<PromotionDto>>($"/api/merchant/shops/{c.ShopId}/promotions")).Single().UsedCount.ShouldBe(1);

        // Cancelling gives the use back.
        await customer.PostOkAsync($"/api/orders/{order.Id}/cancel", new { reason = "สั่งผิด" });
        await FactoryExtensions.EventuallyAsync(async () =>
            (await c.Owner.GetAsync<List<PromotionDto>>($"/api/merchant/shops/{c.ShopId}/promotions")).Single().UsedCount.ShouldBe(0));
        // The failed checkout kept the cart, so it now holds 4 items (400): 10% capped at 30.
        var retry = await TestClient.ReadAsync<OrderDto>(await OrderAsync(c, other, 2, "LUCKY10"));
        retry.Subtotal.ShouldBe(400);
        retry.Total.ShouldBe(370);
    }

    [Fact]
    public async Task Preview_without_code_or_promotions_returns_no_content()
    {
        // The cart page previews the automatic discount on load; "nothing applies" must not be an empty 200 body.
        var c = await ShopAsync();
        var customer = await factory.JoinAsync(c.Plant, c.Admin);
        var response = await customer.PostRawAsync($"/api/shops/{c.ShopId}/promotions/preview", new { code = (string?)null, subtotal = 100 });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Best_automatic_promotion_applies_without_code_and_codes_are_unique_per_shop()
    {
        var c = await ShopAsync();
        await c.Owner.PostOkAsync($"/api/merchant/shops/{c.ShopId}/promotions", new { title = "ลด 15 บาท", type = "Amount", value = 15, active = true });
        await c.Owner.PostOkAsync($"/api/merchant/shops/{c.ShopId}/promotions", new { title = "ลด 20%", type = "Percent", value = 20, minOrder = 300, active = true });
        await c.Owner.PostOkAsync($"/api/merchant/shops/{c.ShopId}/promotions", new { code = "SECRET", title = "ลับ", type = "Amount", value = 50, active = true });

        var customer = await factory.JoinAsync(c.Plant, c.Admin);
        var badges = await customer.GetAsync<List<DiscountDto>>($"/api/shops/{c.ShopId}/promotions");
        badges.Count.ShouldBe(2); // coupon codes are not advertised

        (await TestClient.ReadAsync<OrderDto>(await OrderAsync(c, customer, 1, null))).Total.ShouldBe(85);
        (await TestClient.ReadAsync<OrderDto>(await OrderAsync(c, customer, 3, null))).Total.ShouldBe(240);

        var duplicate = await c.Owner.PostRawAsync($"/api/merchant/shops/{c.ShopId}/promotions", new { code = "secret", title = "ซ้ำ", type = "Amount", value = 1, active = true });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await customer.PostRawAsync($"/api/merchant/shops/{c.ShopId}/promotions", new { title = "x", type = "Amount", value = 1, active = true }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
