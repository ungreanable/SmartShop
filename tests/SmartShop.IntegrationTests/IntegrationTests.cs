using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, List<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);
public sealed record WebhookDto(Guid Id, string Url, List<string> Events, bool Active, int ConsecutiveFailures, string? DisabledReason, string Secret);
public sealed record DeliveryDto(Guid Id, string EventType, string Status, int Attempts, int? ResponseStatus, string? Error);
public sealed record PublicOrderDto(PublicOrder Order, string? CustomerName);
public sealed record PublicOrder(Guid OrderId, string OrderNo, string Status, decimal Total, List<PublicOrderLine> Lines);
public sealed record PublicOrderLine(string Name, int Quantity, decimal LineTotal);
public sealed record PublicItemDto(Guid ItemId, string Name, decimal Price, bool IsSoldOut, int? Available);

public class IntegrationTests(SmartShopFactory factory)
{
    private async Task<(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid CashId, Guid ItemId)> ShopAsync()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Tracked", name = "ข้าวเหนียวมะม่วง", price = 60, initialStock = 5 });
        return (plant, admin, owner, shopId, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id, item.Item.Id);
    }

    private static async Task<OrderDto> OrderAsync(TestClient customer, Guid shopId, Guid itemId, Guid cashId)
    {
        await customer.PostOkAsync("/api/cart/items", new { shopId, itemId, quantity = 2 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = cashId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
    }

    private HttpClient ApiClient(string? key)
    {
        var http = factory.CreateClient();
        if (key is not null) http.DefaultRequestHeaders.Add("X-Api-Key", key);
        return http;
    }

    [Fact]
    public async Task Api_key_reads_orders_and_menu_within_its_scopes_until_revoked()
    {
        var s = await ShopAsync();
        var customer = await factory.JoinAsync(s.Plant, s.Admin, "ป้าศรี");
        var order = await OrderAsync(customer, s.ShopId, s.ItemId, s.CashId);

        (await customer.PostRawAsync($"/api/merchant/shops/{s.ShopId}/api-keys", new { name = "x", scopes = new[] { "orders:read" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var created = await s.Owner.PostAsync<CreatedApiKeyDto>($"/api/merchant/shops/{s.ShopId}/api-keys", new { name = "เครื่องพิมพ์", scopes = new[] { "orders:read" } });
        created.Secret.ShouldStartWith("ssk_" + created.Key.Prefix + "_");

        using var api = ApiClient(created.Secret);
        var orders = await api.GetFromJsonAsync<List<PublicOrderDto>>("/api/public/v1/orders", TestClient.Json);
        var exported = orders!.Single();
        exported.Order.OrderNo.ShouldBe(order.OrderNo);
        exported.Order.Total.ShouldBe(120);
        exported.Order.Lines.Single().Quantity.ShouldBe(2);
        exported.CustomerName.ShouldBe("ป้าศรี");
        (await api.GetAsync($"/api/public/v1/orders/{order.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await api.GetAsync("/api/public/v1/menu")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var anonymous = ApiClient(null)) (await anonymous.GetAsync("/api/public/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var forged = ApiClient("ssk_00000000_forged")) (await forged.GetAsync("/api/public/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var menuKey = await s.Owner.PostAsync<CreatedApiKeyDto>($"/api/merchant/shops/{s.ShopId}/api-keys", new { name = "สต็อก", scopes = new[] { "menu:read" } });
        using (var menuApi = ApiClient(menuKey.Secret))
        {
            var menu = await menuApi.GetFromJsonAsync<List<PublicItemDto>>("/api/public/v1/menu", TestClient.Json);
            menu!.Single().Available.ShouldBe(3); // 5 on hand, 2 reserved by the pending order
        }

        await s.Owner.DeleteOkAsync($"/api/merchant/api-keys/{created.Key.Id}");
        (await api.GetAsync("/api/public/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var keys = await s.Owner.GetAsync<List<ApiKeyDto>>($"/api/merchant/shops/{s.ShopId}/api-keys");
        keys.Single(k => k.Id == created.Key.Id).Revoked.ShouldBeTrue();
        keys.Single(k => k.Id == created.Key.Id).LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Webhooks_deliver_signed_order_events_and_record_failures()
    {
        var s = await ShopAsync();
        var url = $"http://hooks.test/{Guid.NewGuid():N}";
        var hook = await s.Owner.PostAsync<WebhookDto>($"/api/merchant/shops/{s.ShopId}/webhooks",
            new { url, events = new[] { "order.placed", "order.accepted" } });
        hook.Secret.ShouldStartWith("whsec_");

        var ping = await s.Owner.PostAsync<DeliveryDto>($"/api/merchant/webhooks/{hook.Id}/test");
        ping.Status.ShouldBe("Delivered");

        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var order = await OrderAsync(customer, s.ShopId, s.ItemId, s.CashId);
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");

        await FactoryExtensions.EventuallyAsync(() =>
        {
            var received = FakeWebhookReceiver.For(url);
            received.Select(r => r.Event).ShouldBe(["ping", "order.placed", "order.accepted"], ignoreOrder: true);
            foreach (var r in received)
            {
                // Receivers verify: v1 = hex(HMAC-SHA256(secret, "{t}.{body}"))
                var parts = r.Signature.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hook.Secret), Encoding.UTF8.GetBytes($"{parts["t"]}.{r.Body}"))).ToLowerInvariant();
                parts["v1"].ShouldBe(expected);
            }
            using var placed = JsonDocument.Parse(received.Single(r => r.Event == "order.placed").Body);
            placed.RootElement.GetProperty("data").GetProperty("order").GetProperty("orderNo").GetString().ShouldBe(order.OrderNo);
            return Task.CompletedTask;
        });

        var failing = await s.Owner.PostAsync<WebhookDto>($"/api/merchant/shops/{s.ShopId}/webhooks", new { url = url + "/fail", events = new[] { "order.ready" } });
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/start");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/ready");
        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var deliveries = await s.Owner.GetAsync<List<DeliveryDto>>($"/api/merchant/webhooks/{failing.Id}/deliveries");
            var d = deliveries.Single();
            d.EventType.ShouldBe("order.ready");
            d.Attempts.ShouldBe(1);
            d.ResponseStatus.ShouldBe(500);
            d.Status.ShouldBe("Pending"); // retried later with backoff
        });
        (await s.Owner.GetAsync<List<WebhookDto>>($"/api/merchant/shops/{s.ShopId}/webhooks")).Single(w => w.Id == failing.Id).ConsecutiveFailures.ShouldBe(1);
    }
}
