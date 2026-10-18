using System.Net;
using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record CustomCartLineDto(Guid Id, string Name, int Quantity, decimal LineTotal, bool IsCustom, bool IsOrderable);
public sealed record CustomCartDto(List<CustomCartLineDto> Lines, decimal Subtotal, bool CanCheckout, bool NeedsQuote);
public sealed record QuoteLineViewDto(Guid Id, string Name, int Quantity, decimal UnitPrice, decimal LineTotal, bool IsCustom, bool Unavailable, string? ShopNote);
public sealed record QuoteOrderDto(Guid Id, string Status, List<QuoteLineViewDto> Lines, decimal Subtotal, decimal Total, bool NeedsQuote,
    string? QuoteMessage, DateTimeOffset? ExpiresAt, string? AcceptedByName);

/// <summary>"ตามสั่ง": the customer writes a dish, the shop prices it, the customer agrees before the shop cooks.</summary>
public class CustomOrderTests(SmartShopFactory factory)
{
    private sealed record Setup(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid CashId, TestClient Customer);

    private async Task<Setup> SetupAsync(bool acceptsCustom = true)
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync("ร้านตามสั่ง");
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        if (acceptsCustom) await owner.PutOkAsync($"/api/merchant/shops/{shopId}/custom-orders", new { enabled = true });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var customer = await factory.JoinAsync(plant, admin);
        return new Setup(plant, admin, owner, shopId, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id, customer);
    }

    private static async Task<HttpResponseMessage> CheckoutRawAsync(TestClient c, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(body, options: TestClient.Json) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.Http.SendAsync(request);
    }

    private static async Task<QuoteOrderDto> CheckoutAsync(Setup s) =>
        await TestClient.ReadAsync<QuoteOrderDto>(await CheckoutRawAsync(s.Customer, new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }));

    [Fact]
    public async Task Shop_prices_custom_dishes_and_the_order_goes_ahead_once_the_customer_agrees()
    {
        var s = await SetupAsync();
        var water = await s.Owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{s.ShopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "น้ำเปล่า", price = 10 });
        await s.Customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = water.Item.Id, quantity = 1 });
        var cart = await s.Customer.PostAsync<CustomCartDto>("/api/cart/custom", new { shopId = s.ShopId, text = "ผัดกะเพราหมูสับ ไข่เยี่ยวม้า", quantity = 2 });
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ต้มยำกุ้งน้ำข้น" });
        cart = await s.Customer.GetAsync<CustomCartDto>("/api/cart");
        cart.NeedsQuote.ShouldBeTrue();
        cart.CanCheckout.ShouldBeTrue();
        cart.Subtotal.ShouldBe(10); // custom dishes are priced later

        var order = await CheckoutAsync(s);
        order.Status.ShouldBe("PendingAcceptance");
        order.NeedsQuote.ShouldBeTrue();

        // No payment and no plain "accept" before the prices are agreed.
        (await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/accept", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await s.Customer.GetRawAsync($"/api/orders/{order.Id}/payment")).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // payment_pending

        var kaprao = order.Lines.Single(l => l.Name.StartsWith("ผัดกะเพรา"));
        var tomyum = order.Lines.Single(l => l.Name.StartsWith("ต้มยำ"));
        var quoted = await s.Owner.PostAsync<QuoteOrderDto>($"/api/merchant/orders/{order.Id}/quote", new
        {
            lines = new object[]
            {
                new { lineId = kaprao.Id, unitPrice = 60 },
                new { lineId = tomyum.Id, unavailable = true, note = "กุ้งหมด" },
            },
            message = "ไข่เยี่ยวม้าเหลือฟองเดียว ใส่ไข่ดาวแทนได้ไหม",
        });
        quoted.Status.ShouldBe("AwaitingPriceApproval");
        quoted.Total.ShouldBe(10 + 2 * 60);
        quoted.Lines.Single(l => l.Id == tomyum.Id).ShouldSatisfyAllConditions(l => l.Unavailable.ShouldBeTrue(), l => l.ShopNote.ShouldBe("กุ้งหมด"));
        quoted.ExpiresAt.ShouldNotBeNull();

        var seen = await s.Customer.GetAsync<QuoteOrderDto>($"/api/orders/{order.Id}");
        seen.QuoteMessage.ShouldBe("ไข่เยี่ยวม้าเหลือฟองเดียว ใส่ไข่ดาวแทนได้ไหม");

        var accepted = await s.Customer.PostAsync<QuoteOrderDto>($"/api/orders/{order.Id}/accept-quote");
        accepted.Status.ShouldBe("Accepted");
        accepted.AcceptedByName.ShouldNotBeNull();

        // Payment for the agreed amount, then the normal flow.
        PaymentDto? payment = null;
        await FactoryExtensions.EventuallyAsync(async () => payment = await s.Customer.GetAsync<PaymentDto>($"/api/orders/{order.Id}/payment"));
        payment!.Amount.ShouldBe(130);
        (await s.Owner.PostAsync<QuoteOrderDto>($"/api/merchant/orders/{order.Id}/ready")).Status.ShouldBe("Ready");
    }

    [Fact]
    public async Task Customer_can_decline_the_price_and_shop_can_send_new_prices_first()
    {
        var s = await SetupAsync();
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ข้าวผัดปู" });
        var order = await CheckoutAsync(s);
        var line = order.Lines.Single();

        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/quote", new { lines = new[] { new { lineId = line.Id, unitPrice = 120 } } });
        var requoted = await s.Owner.PostAsync<QuoteOrderDto>($"/api/merchant/orders/{order.Id}/quote",
            new { lines = new[] { new { lineId = line.Id, unitPrice = 100 } }, message = "ลดให้" });
        requoted.Total.ShouldBe(100);

        var declined = await s.Customer.PostAsync<QuoteOrderDto>($"/api/orders/{order.Id}/cancel", new { reason = "แพงไป" });
        declined.Status.ShouldBe("Cancelled");
        (await s.Customer.PostRawAsync($"/api/orders/{order.Id}/accept-quote", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Quote_must_price_every_dish_and_leave_something_to_make()
    {
        var s = await SetupAsync();
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ผัดซีอิ๊ว" });
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ราดหน้า" });
        var order = await CheckoutAsync(s);
        var (a, b) = (order.Lines[0], order.Lines[1]);

        (await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/quote", new { lines = new[] { new { lineId = a.Id, unitPrice = 50 } } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/quote", new
        {
            lines = new[] { new { lineId = a.Id, unavailable = true }, new { lineId = b.Id, unavailable = true } },
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Custom_dishes_need_a_shop_that_takes_them_and_no_coupon()
    {
        var closed = await SetupAsync(acceptsCustom: false);
        (await closed.Customer.PostRawAsync("/api/cart/custom", new { shopId = closed.ShopId, text = "ผัดกะเพรา" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var s = await SetupAsync();
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ผัดกะเพรา" });
        (await CheckoutRawAsync(s.Customer, new { fulfillmentType = "Pickup", paymentMethodId = s.CashId, couponCode = "SAVE10" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>The shop's ordering settings are cached; every merchant change must still apply to the very next request.</summary>
    [Fact]
    public async Task Shop_setting_changes_apply_to_the_next_cart_and_checkout_despite_caching()
    {
        var s = await SetupAsync(acceptsCustom: false);
        (await s.Customer.PostRawAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ผัดกะเพรา" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/custom-orders", new { enabled = true });
        await s.Customer.PostOkAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ผัดกะเพรา" });

        await s.Owner.PostOkAsync($"/api/merchant/shops/{s.ShopId}/payment-methods",
            new { type = "PromptPayQr", displayName = "พร้อมเพย์", promptPayId = "081-234-5678" });
        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/payment-methods/{s.CashId}", new { type = "Cash", displayName = "เงินสด", enabled = false });
        (await TestClient.ProblemCodeAsync(await CheckoutRawAsync(s.Customer, new { fulfillmentType = "Pickup", paymentMethodId = s.CashId })))
            .ShouldBe("payment_method_invalid");

        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/custom-orders", new { enabled = false });
        (await s.Customer.PostRawAsync("/api/cart/custom", new { shopId = s.ShopId, text = "ต้มยำ" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
