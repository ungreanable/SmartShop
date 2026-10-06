using System.Net;
using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;
using Wolverine;

namespace SmartShop.IntegrationTests;

public sealed record CartLineDto(Guid Id, Guid ItemId, string Name, int Quantity, decimal UnitPrice, decimal LineTotal, bool IsOrderable, string? Problem);
public sealed record CartDto(Guid? ShopId, string? ShopName, List<CartLineDto> Lines, decimal Subtotal, int ItemCount, bool CanCheckout);
public sealed record OrderLineDto(Guid ItemId, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record TimelineDto(string Type, string? ActorName, string? Note, DateTimeOffset At);
public sealed record AddressDto(string? HouseNo, string? Soi, string? Note);
public sealed record OrderDto(Guid Id, string OrderNo, string Status, Guid ShopId, string FulfillmentType, AddressDto? DeliveryAddress,
    List<OrderLineDto> Lines, decimal Subtotal, decimal Total, string PaymentMethodType, string PaymentStatus, string? AcceptedByName,
    string? CancelReason, List<TimelineDto> Timeline, DateTimeOffset? ScheduledFrom);
public sealed record SalesReportDto(DateOnly From, DateOnly To, int Orders, decimal Revenue, decimal AverageOrder, int Cancelled);
public sealed record OrderViewDto(Guid Id, string Status, string ViewerRole);
public sealed record OrderSummaryDto(Guid Id, string OrderNo, string Status, string CustomerName, decimal Total, bool CancelRequested);

public class OrderingTests(SmartShopFactory factory)
{
    private sealed record Shop(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId, Guid CashId);

    private async Task<Shop> OpenShopAsync(Action<object>? _ = null, bool delivery = false, decimal? minOrder = null)
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        if (delivery)
            await owner.PutOkAsync($"/api/merchant/shops/{shopId}/delivery-options", new { pickupEnabled = true, deliveryEnabled = true, deliveryMinOrder = minOrder });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        return new Shop(plant, admin, owner, shopId, settings.Shop.PaymentMethods.Single(p => p.Type == "Cash").Id);
    }

    private static Task<MerchantItemDto> AddItemAsync(Shop s, string name, decimal price, int? stock = null) =>
        s.Owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{s.ShopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = stock is null ? "Untracked" : "Tracked",
            name,
            price,
            initialStock = stock,
        });

    private static async Task<HttpResponseMessage> CheckoutRawAsync(TestClient c, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(body, options: TestClient.Json) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await c.Http.SendAsync(request);
    }

    private static async Task<OrderDto> CheckoutAsync(TestClient c, Guid paymentMethodId, string fulfillment = "Pickup", string? key = null, object? address = null) =>
        await TestClient.ReadAsync<OrderDto>(await CheckoutRawAsync(c, new { fulfillmentType = fulfillment, paymentMethodId, deliveryAddress = address }, key));

    private async Task<StockResultDto> StockAsync(Shop s, Guid itemId)
    {
        var items = await s.Owner.GetAsync<List<MerchantItemDto>>($"/api/merchant/shops/{s.ShopId}/catalog/items");
        var item = items.Single(i => i.Item.Id == itemId);
        return new StockResultDto(item.OnHand!.Value, item.Reserved!.Value, item.OnHand.Value - item.Reserved.Value);
    }

    [Fact]
    public async Task Full_pickup_flow_reserves_then_commits_stock_on_completion()
    {
        var s = await OpenShopAsync();
        var rice = await AddItemAsync(s, "ข้าวมันไก่", 50, stock: 10);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);

        var cart = await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = s.ShopId, itemId = rice.Item.Id, quantity = 2 });
        cart.Subtotal.ShouldBe(100);
        cart.CanCheckout.ShouldBeTrue();

        var order = await CheckoutAsync(customer, s.CashId);
        order.Status.ShouldBe("PendingAcceptance");
        order.OrderNo.ShouldBe("A-001");
        order.Total.ShouldBe(100);
        (await StockAsync(s, rice.Item.Id)).ShouldBe(new StockResultDto(10, 2, 8));
        (await customer.GetAsync<CartDto>("/api/cart")).Lines.ShouldBeEmpty();

        var newTab = await s.Owner.GetAsync<List<OrderSummaryDto>>($"/api/merchant/shops/{s.ShopId}/orders?tab=new");
        newTab.Single().CustomerName.ShouldBe(customer.DisplayName);

        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/start");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/ready");
        var delivered = await TestClient.ReadAsync<OrderDto>(await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/deliver", new { }));
        delivered.Status.ShouldBe("Delivered");

        var completed = await customer.PostAsync<OrderDto>($"/api/orders/{order.Id}/confirm-received");
        completed.Status.ShouldBe("Completed");
        completed.Timeline.Select(t => t.Type).ShouldBe(["Placed", "Accepted", "Preparing", "Ready", "Delivered", "Received"]);
        completed.AcceptedByName.ShouldBe(s.Owner.DisplayName);

        await FactoryExtensions.EventuallyAsync(async () => (await StockAsync(s, rice.Item.Id)).ShouldBe(new StockResultDto(8, 0, 8)));
    }

    [Fact]
    public async Task Owner_ordering_from_own_shop_can_run_it_as_the_shop()
    {
        var s = await OpenShopAsync();
        var rice = await AddItemAsync(s, "ข้าวผัด", 45);
        await s.Owner.PostAsync<CartDto>("/api/cart/items", new { shopId = s.ShopId, itemId = rice.Item.Id, quantity = 1 });
        var order = await CheckoutAsync(s.Owner, s.CashId);

        // The customer screen shows the customer side; the merchant screen asks for the shop side.
        (await s.Owner.GetAsync<OrderViewDto>($"/api/orders/{order.Id}")).ViewerRole.ShouldBe("customer");
        (await s.Owner.GetAsync<OrderViewDto>($"/api/orders/{order.Id}?as=shop")).ViewerRole.ShouldBe("shop");
        (await s.Owner.GetAsync<PaymentDto>($"/api/orders/{order.Id}/payment")).CanVerify.ShouldBeFalse();
        (await s.Owner.GetAsync<PaymentDto>($"/api/orders/{order.Id}/payment?as=shop")).CanVerify.ShouldBeTrue();

        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/start");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/ready");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/deliver", new { });
        var completed = await s.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/confirm-received");
        completed.Status.ShouldBe("Completed");

        // Someone who is not in the shop cannot get the shop side by asking for it.
        var neighbour = await factory.JoinAsync(s.Plant, s.Admin);
        (await neighbour.GetRawAsync($"/api/orders/{order.Id}?as=shop")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Owner_can_force_close_a_stuck_order_as_completed_or_cancelled()
    {
        var s = await OpenShopAsync();
        var rice = await AddItemAsync(s, "ข้าวต้ม", 40, stock: 10);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);

        async Task<OrderDto> PlaceAsync()
        {
            await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = s.ShopId, itemId = rice.Item.Id, quantity = 1 });
            var order = await CheckoutAsync(customer, s.CashId);
            await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");
            return order;
        }

        // Stuck at "Ready": the food was handed over but nobody pressed the button.
        var handed = await PlaceAsync();
        await s.Owner.PostOkAsync($"/api/merchant/orders/{handed.Id}/start");
        await s.Owner.PostOkAsync($"/api/merchant/orders/{handed.Id}/ready");
        var completed = await s.Owner.PostAsync<OrderDto>($"/api/merchant/orders/{handed.Id}/force-close", new { outcome = "completed", reason = "ลูกค้ารับไปแล้ว" });
        completed.Status.ShouldBe("Completed");
        completed.Timeline.Last().Type.ShouldBe("ForceCompleted");

        // A broken order closed as cancelled releases its stock.
        var broken = await PlaceAsync();
        var cancelled = await s.Owner.PostAsync<OrderDto>($"/api/merchant/orders/{broken.Id}/force-close", new { outcome = "cancelled", reason = "สั่งผิด" });
        cancelled.Status.ShouldBe("Cancelled");
        await FactoryExtensions.EventuallyAsync(async () => (await StockAsync(s, rice.Item.Id)).ShouldBe(new StockResultDto(9, 0, 9)));

        // Closed orders stay closed; a reason is required.
        (await s.Owner.PostRawAsync($"/api/merchant/orders/{broken.Id}/force-close", new { outcome = "completed", reason = "x" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var third = await PlaceAsync();
        (await s.Owner.PostRawAsync($"/api/merchant/orders/{third.Id}/force-close", new { outcome = "cancelled", reason = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sales_report_counts_delivered_and_completed_orders_of_today()
    {
        var s = await OpenShopAsync();
        var noodles = await AddItemAsync(s, "ก๋วยเตี๋ยว", 50);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);

        async Task<OrderDto> DeliverAsync()
        {
            await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = s.ShopId, itemId = noodles.Item.Id, quantity = 1 });
            var order = await CheckoutAsync(customer, s.CashId);
            foreach (var step in new[] { "accept", "start", "ready" }) await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/{step}");
            await s.Owner.PostOkAsync($"/api/merchant/orders/{order.Id}/deliver", new { });
            return order;
        }

        var completed = await DeliverAsync();
        await customer.PostOkAsync($"/api/orders/{completed.Id}/confirm-received");
        await DeliverAsync(); // delivered, the customer has not confirmed yet

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).ToString("yyyy-MM-dd");
        var report = await s.Owner.GetAsync<SalesReportDto>($"/api/merchant/shops/{s.ShopId}/orders/report?from={today}&to={today}");
        report.Orders.ShouldBe(2);
        report.Revenue.ShouldBe(100);
    }

    [Fact]
    public async Task Concurrent_checkouts_never_oversell_the_last_item()
    {
        var s = await OpenShopAsync();
        var last = await AddItemAsync(s, "ชิ้นสุดท้าย", 99, stock: 1);
        var customers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => factory.JoinAsync(s.Plant, s.Admin)));
        foreach (var c in customers)
            await c.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = last.Item.Id, quantity = 1 });

        var results = await Task.WhenAll(customers.Select(c => CheckoutRawAsync(c, new { fulfillmentType = "Pickup", paymentMethodId = s.CashId })));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var failed in results.Where(r => r.StatusCode != HttpStatusCode.Created))
            (await TestClient.ProblemCodeAsync(failed)).ShouldBeOneOf("out_of_stock", "item_unavailable");
        (await StockAsync(s, last.Item.Id)).ShouldBe(new StockResultDto(1, 1, 0));
    }

    [Fact]
    public async Task Rejected_order_releases_reserved_stock()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "ขนมจีบ", 10, stock: 5);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 3 });
        var order = await CheckoutAsync(customer, s.CashId);

        var rejected = await TestClient.ReadAsync<OrderDto>(await s.Owner.PostRawAsync($"/api/merchant/orders/{order.Id}/reject", new { reason = "ของหมด" }));
        rejected.Status.ShouldBe("Rejected");

        await FactoryExtensions.EventuallyAsync(async () => (await StockAsync(s, item.Item.Id)).ShouldBe(new StockResultDto(5, 0, 5)));
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_order()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "กาแฟ", 30);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });

        var key = Guid.NewGuid().ToString("N");
        var first = await CheckoutAsync(customer, s.CashId, key: key);
        var retry = await CheckoutRawAsync(customer, new { fulfillmentType = "Pickup", paymentMethodId = s.CashId }, key);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TestClient.ReadAsync<OrderDto>(retry)).Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Cart_holds_a_single_shop()
    {
        var s1 = await OpenShopAsync();
        var item1 = await AddItemAsync(s1, "A", 10);
        var customer = await factory.JoinAsync(s1.Plant, s1.Admin);
        // second shop in the same village
        var owner2 = await factory.JoinAsync(s1.Plant, s1.Admin);
        var app = await owner2.PostAsync<ApplicationDto>("/api/shop-applications", new { name = "ร้านสอง", houseNo = "2" });
        var shop2 = await s1.Admin.PostAsync<ApprovedDto>($"/api/plant/admin/shop-applications/{app.Id}/approve", new { });
        var item2 = await owner2.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shop2.ShopId}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "B", price = 20 });

        await customer.PostOkAsync("/api/cart/items", new { shopId = s1.ShopId, itemId = item1.Item.Id, quantity = 1 });
        var conflict = await customer.PostRawAsync("/api/cart/items", new { shopId = shop2.ShopId, itemId = item2.Item.Id, quantity = 1 });
        (await TestClient.ProblemCodeAsync(conflict)).ShouldBe("cart_other_shop");

        var replaced = await customer.PostAsync<CartDto>("/api/cart/items", new { shopId = shop2.ShopId, itemId = item2.Item.Id, quantity = 1, replaceCart = true });
        replaced.ShopId.ShouldBe(shop2.ShopId);
        replaced.Lines.Single().Name.ShouldBe("B");
    }

    [Fact]
    public async Task Closed_shop_requires_scheduling_when_preorders_are_allowed()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var hours = Enumerable.Range(0, 7).SelectMany(d => new[]
        {
            new { day = d, openAt = "00:00", closeAt = "12:00" },
            new { day = d, openAt = "12:00", closeAt = "23:59" },
        }).ToArray();
        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/hours", new { hours });
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "close" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var cash = settings.Shop.PaymentMethods.Single().Id;
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "เค้ก", price = 80 });
        var customer = await factory.JoinAsync(plant, admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId, itemId = item.Item.Id, quantity = 1 });

        var closed = await CheckoutRawAsync(customer, new { fulfillmentType = "Pickup", paymentMethodId = cash });
        (await TestClient.ProblemCodeAsync(closed)).ShouldBe("shop_closed");

        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/order-settings", new
        {
            acceptMode = "Manual",
            acceptTimeoutMinutes = 15,
            reminderAfterMinutes = 3,
            autoCompleteHours = 12,
            allowPreorderWhenClosed = true,
            prepTimeMinutes = 0,
            slotIntervalMinutes = 30,
            requirePaymentBeforePreparing = false,
        });
        var needsTime = await CheckoutRawAsync(customer, new { fulfillmentType = "Pickup", paymentMethodId = cash });
        (await TestClient.ProblemCodeAsync(needsTime)).ShouldBe("schedule_required");

        // Manual close overrides the schedule, so return to "follow schedule" with a closure window instead.
        var windows = await customer.GetAsync<List<WindowDto>>($"/api/shops/{shopId}/windows?days=2");
        var pick = windows[^1];
        var order = await TestClient.ReadAsync<OrderDto>(await CheckoutRawAsync(customer,
            new { fulfillmentType = "Pickup", paymentMethodId = cash, scheduledFrom = pick.Start, scheduledTo = pick.End }));
        order.ScheduledFrom.ShouldBe(pick.Start);
    }

    [Fact]
    public async Task Delivery_uses_member_address_and_enforces_minimum_order()
    {
        var s = await OpenShopAsync(delivery: true, minOrder: 100);
        var item = await AddItemAsync(s, "ส้มตำ", 40);
        var customer = await factory.JoinAsync(s.Plant, s.Admin, houseNo: "55/7");
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 2 });

        var tooSmall = await CheckoutRawAsync(customer, new { fulfillmentType = "Delivery", paymentMethodId = s.CashId });
        (await TestClient.ProblemCodeAsync(tooSmall)).ShouldBe("min_order");

        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });
        var order = await CheckoutAsync(customer, s.CashId, "Delivery");
        order.DeliveryAddress!.HouseNo.ShouldBe("55/7");
        order.Total.ShouldBe(120); // no delivery fee inside the village
    }

    [Fact]
    public async Task Customer_cancels_before_acceptance_but_must_request_afterwards()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "ชา", 25);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);

        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });
        var first = await CheckoutAsync(customer, s.CashId);
        (await customer.PostAsync<OrderDto>($"/api/orders/{first.Id}/cancel", new { reason = "สั่งผิด" })).Status.ShouldBe("Cancelled");

        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });
        var second = await CheckoutAsync(customer, s.CashId);
        await s.Owner.PostOkAsync($"/api/merchant/orders/{second.Id}/accept");
        (await TestClient.ProblemCodeAsync(await customer.PostRawAsync($"/api/orders/{second.Id}/cancel", new { }))).ShouldBe("cannot_cancel");

        await customer.PostOkAsync($"/api/orders/{second.Id}/request-cancel", new { reason = "ไม่อยู่บ้าน" });
        var list = await s.Owner.GetAsync<List<OrderSummaryDto>>($"/api/merchant/shops/{s.ShopId}/orders?tab=active");
        list.Single().CancelRequested.ShouldBeTrue();
        (await s.Owner.PostAsync<OrderDto>($"/api/merchant/orders/{second.Id}/cancel", new { })).CancelReason.ShouldBe("ไม่อยู่บ้าน");
    }

    [Fact]
    public async Task Unaccepted_order_expires_through_the_scheduled_command()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "ข้าวเหนียว", 10, stock: 3);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });
        var order = await CheckoutAsync(customer, s.CashId);

        await factory.Services.GetRequiredService<IMessageBus>().InvokeAsync(new SmartShop.Contracts.Ordering.ExpireOrderIfNotAccepted(order.Id));

        (await customer.GetAsync<OrderDto>($"/api/orders/{order.Id}")).Status.ShouldBe("Expired");
        await FactoryExtensions.EventuallyAsync(async () => (await StockAsync(s, item.Item.Id)).Available.ShouldBe(3));
    }

    [Fact]
    public async Task Auto_accept_shops_skip_the_pending_state()
    {
        var s = await OpenShopAsync();
        await s.Owner.PutOkAsync($"/api/merchant/shops/{s.ShopId}/order-settings", new
        {
            acceptMode = "Auto",
            acceptTimeoutMinutes = 15,
            reminderAfterMinutes = 3,
            autoCompleteHours = 12,
            allowPreorderWhenClosed = false,
            prepTimeMinutes = 10,
            slotIntervalMinutes = 30,
            requirePaymentBeforePreparing = false,
        });
        var item = await AddItemAsync(s, "น้ำแข็ง", 10);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });

        (await CheckoutAsync(customer, s.CashId)).Status.ShouldBe("Accepted");
    }

    [Fact]
    public async Task Orders_are_private_to_customer_shop_and_admins()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "ไข่", 5);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        var neighbour = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 1 });
        var order = await CheckoutAsync(customer, s.CashId);

        (await neighbour.GetRawAsync($"/api/orders/{order.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Admin.GetRawAsync($"/api/orders/{order.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await neighbour.PostRawAsync($"/api/merchant/orders/{order.Id}/accept")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Order_chat_and_prep_summary_work()
    {
        var s = await OpenShopAsync();
        var item = await AddItemAsync(s, "ขนมครก", 20);
        var customer = await factory.JoinAsync(s.Plant, s.Admin);
        await customer.PostOkAsync("/api/cart/items", new { shopId = s.ShopId, itemId = item.Item.Id, quantity = 4 });
        var order = await CheckoutAsync(customer, s.CashId);

        await customer.PostOkAsync($"/api/orders/{order.Id}/messages", new { body = "ไม่ใส่ต้นหอมนะคะ" });
        await s.Owner.PostOkAsync($"/api/orders/{order.Id}/messages", new { body = "ได้ค่ะ" });
        var messages = await customer.GetAsync<List<System.Text.Json.JsonElement>>($"/api/orders/{order.Id}/messages");
        messages.Count.ShouldBe(2);

        var prep = await s.Owner.GetAsync<List<System.Text.Json.JsonElement>>($"/api/merchant/shops/{s.ShopId}/orders/prep-summary");
        prep.Single().GetProperty("quantity").GetInt32().ShouldBe(4);
    }
}
