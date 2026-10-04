using Npgsql;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

/// <summary>
/// Canary for the event-driven pipeline: a failing handler (e.g. a code-generation problem) moves messages to the
/// dead-letter queue silently, so make that loud.
/// </summary>
public class EventPipelineTests(SmartShopFactory factory)
{
    [Fact]
    public async Task No_message_ends_up_in_the_dead_letter_queue()
    {
        // Drive a realistic flow first so every module's handlers are exercised.
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Tracked", name = "x", price = 5, initialStock = 1, lowStockThreshold = 0 });
        var customer = await factory.JoinAsync(plant, admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId, itemId = item.Item.Id, quantity = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId = settings.Shop.PaymentMethods[0].Id }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
        await owner.PostOkAsync($"/api/merchant/orders/{order.Id}/reject", new { reason = "test" });

        await Task.Delay(TimeSpan.FromSeconds(3));
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("select message_type || ' @ ' || coalesce(exception_message, '') from wolverine.wolverine_dead_letters", connection);
        var dead = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) dead.Add(reader.GetString(0));

        dead.ShouldBeEmpty();
    }
}
