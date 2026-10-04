using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public class ShopTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Application_is_reviewed_by_admin_before_a_shop_exists()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var applicant = await factory.JoinAsync(plant, admin);
        var sample = await applicant.UploadAsync("ApplicationSample");

        var app = await applicant.PostAsync<ApplicationDto>("/api/shop-applications",
            new { name = "ร้านป้าแดง", category = "อาหาร", houseNo = "12/3", sampleImageIds = new[] { sample.Id } });
        app.Status.ShouldBe("Submitted");

        // Only one open application at a time.
        var second = await applicant.PostRawAsync("/api/shop-applications", new { name = "อีกร้าน", houseNo = "1" });
        (await TestClient.ProblemCodeAsync(second)).ShouldBe("application_pending");

        var queue = await admin.GetAsync<List<ApplicationDto>>("/api/plant/admin/shop-applications?status=Submitted");
        queue.ShouldContain(a => a.Id == app.Id && a.ApplicantName == applicant.DisplayName && a.SampleImageUrls.Count == 1);

        await admin.PostOkAsync($"/api/plant/admin/shop-applications/{app.Id}/request-changes", new { note = "ขอรูปเมนูเพิ่ม" });
        var mine = await applicant.GetAsync<List<ApplicationDto>>("/api/shop-applications/mine");
        mine.Single().Status.ShouldBe("ChangesRequested");
        mine.Single().ReviewNote.ShouldBe("ขอรูปเมนูเพิ่ม");

        await applicant.PutOkAsync($"/api/shop-applications/{app.Id}", new { name = "ร้านป้าแดง", houseNo = "12/3", sampleImageIds = new[] { sample.Id } });
        var approved = await admin.PostAsync<ApprovedDto>($"/api/plant/admin/shop-applications/{app.Id}/approve", new { note = "ยินดีด้วย" });
        approved.Code.ShouldBe("A");

        var myShops = await applicant.GetAsync<List<MyShopDto>>("/api/me/shops");
        myShops.Single().Role.ShouldBe("Owner");
        myShops.Single().Id.ShouldBe(approved.ShopId);
    }

    [Fact]
    public async Task New_shop_is_closed_until_hours_are_set_and_follows_manual_override()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var customer = await factory.JoinAsync(plant, admin);

        var details = await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}");
        details.Status.State.ShouldBe("Closed");
        details.Status.Reason.ShouldBe("OutsideHours");
        details.PaymentMethods.Single().Type.ShouldBe("Cash");

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        (await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}")).Status.AcceptingOrders.ShouldBeTrue();

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/busy", new { minutes = 30 });
        var busy = (await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}")).Status;
        busy.State.ShouldBe("Busy");
        busy.AcceptingOrders.ShouldBeFalse();

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "close" });
        (await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}")).Status.Reason.ShouldBe("Manual");
    }

    [Fact]
    public async Task Weekly_hours_define_status_and_checkout_windows()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var everyDayAllDay = Enumerable.Range(0, 7).Select(d => new { day = d, openAt = "00:00", closeAt = "00:00" }).ToArray();
        // 00:00-00:00 is invalid (equal); use two half-day slots instead.
        var hours = Enumerable.Range(0, 7).SelectMany(d => new[]
        {
            new { day = d, openAt = "00:00", closeAt = "12:00" },
            new { day = d, openAt = "12:00", closeAt = "23:59" },
        }).ToArray();
        (await owner.PutRawAsync($"/api/merchant/shops/{shopId}/hours", new { hours = everyDayAllDay })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/hours", new { hours });

        var customer = await factory.JoinAsync(plant, admin);
        var details = await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}");
        details.Hours.Count.ShouldBe(14);
        var windows = await customer.GetAsync<List<WindowDto>>($"/api/shops/{shopId}/windows?days=1");
        windows.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Status_change_is_announced_by_the_event_driven_evaluator()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartShop.Modules.Shops.Data.ShopsDbContext>();
            var tracker = await db.StatusTrackers.AsNoTracking().FirstAsync(s => s.ShopId == shopId);
            tracker.LastAnnouncedState.ShouldBe(SmartShop.SharedKernel.Scheduling.ShopOpenState.Open);
        });
    }

    [Fact]
    public async Task Delivery_options_require_at_least_one_choice()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();

        var none = await owner.PutRawAsync($"/api/merchant/shops/{shopId}/delivery-options", new { pickupEnabled = false, deliveryEnabled = false });
        (await TestClient.ProblemCodeAsync(none)).ShouldBe("delivery_option_required");

        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/delivery-options",
            new { pickupEnabled = true, pickupInstruction = "กดกริ่งหน้าบ้าน", deliveryEnabled = true, deliveryZoneNote = "ซอย 1-5" });
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        settings.Shop.DeliveryEnabled.ShouldBeTrue();
        settings.Shop.DeliveryZoneNote.ShouldBe("ซอย 1-5");
    }

    [Fact]
    public async Task Payment_methods_are_validated_per_type()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();

        var invalid = await owner.PostRawAsync($"/api/merchant/shops/{shopId}/payment-methods", new { type = "PromptPayQr", displayName = "พร้อมเพย์" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var promptPay = await owner.PostAsync<PaymentMethodDto>($"/api/merchant/shops/{shopId}/payment-methods",
            new { type = "PromptPayQr", displayName = "พร้อมเพย์", promptPayId = "081-234-5678" });
        promptPay.PromptPayId.ShouldBe("0812345678");
        promptPay.RequiresProof.ShouldBeTrue();

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/payment-methods",
            new { type = "BankTransfer", displayName = "กสิกร", bankName = "KBank", accountNumber = "123-4-56789-0", accountName = "ป้าแดง" });
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/payment-methods",
            new { type = "Custom", displayName = "เป๋าตัง / ไทยช่วยไทย", instructions = "สแกนในแอปเป๋าตัง" });

        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        settings.Shop.PaymentMethods.Select(p => p.Type).ShouldBe(["Cash", "PromptPayQr", "BankTransfer", "Custom"]);
    }

    [Fact]
    public async Task Second_phone_joins_as_manager_through_an_invite()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var secondPhone = await factory.JoinAsync(plant, admin, "แม่ (เครื่อง 2)");

        var invite = await owner.PostAsync<InviteDto>($"/api/merchant/shops/{shopId}/invites", new { role = "Manager" });
        var accepted = await secondPhone.PostAsync<AcceptedInviteDto>($"/api/shop-invites/{invite.Code}/accept");
        accepted.Role.ShouldBe("Manager");

        // Invites are single-use.
        var third = await factory.JoinAsync(plant, admin);
        (await TestClient.ProblemCodeAsync(await third.PostRawAsync($"/api/shop-invites/{invite.Code}/accept"))).ShouldBe("invite_used");

        // The manager can now change shop settings ...
        await secondPhone.PutOkAsync($"/api/merchant/shops/{shopId}/delivery-options", new { pickupEnabled = true, deliveryEnabled = true });
        // ... but cannot transfer ownership.
        (await secondPhone.PostRawAsync($"/api/merchant/shops/{shopId}/transfer-ownership", new { userId = secondPhone.UserId }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var members = await owner.GetAsync<List<ShopMemberDto>>($"/api/merchant/shops/{shopId}/members");
        members.Select(m => m.Role).ShouldBe(["Owner", "Manager"]);
    }

    [Fact]
    public async Task At_least_one_member_must_keep_order_notifications()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();

        var response = await owner.PutRawAsync($"/api/merchant/shops/{shopId}/members/{owner.UserId}/notifications", new { enabled = false });
        (await TestClient.ProblemCodeAsync(response)).ShouldBe("last_notification_recipient");
    }

    [Fact]
    public async Task Customers_cannot_use_merchant_endpoints()
    {
        var (plant, admin, _, shopId) = await factory.CreateShopAsync();
        var customer = await factory.JoinAsync(plant, admin);

        (await customer.PostRawAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Shops_are_invisible_to_other_villages()
    {
        var (_, _, _, shopId) = await factory.CreateShopAsync();
        var (otherPlant, otherAdmin) = await factory.CreatePlantAsync();
        var outsider = await factory.JoinAsync(otherPlant, otherAdmin);

        (await outsider.GetRawAsync($"/api/shops/{shopId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspended_shop_is_closed_and_hidden_from_customers()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var customer = await factory.JoinAsync(plant, admin);
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });

        await admin.PostOkAsync($"/api/plant/admin/shops/{shopId}/suspend", new { reason = "ร้องเรียน" });

        (await customer.GetRawAsync($"/api/shops/{shopId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}")).Status.Reason.ShouldBe("Suspended");
    }

    [Fact]
    public async Task Favorites_can_be_toggled()
    {
        var (plant, admin, _, shopId) = await factory.CreateShopAsync();
        var customer = await factory.JoinAsync(plant, admin);

        await customer.Http.PutAsync($"/api/shops/{shopId}/favorite", null);
        (await customer.GetAsync<ShopDetailsDto>($"/api/shops/{shopId}")).IsFavorite.ShouldBeTrue();
        (await customer.GetAsync<List<Guid>>("/api/shops/favorites")).ShouldBe([shopId]);
    }
}
