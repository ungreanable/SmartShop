using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record NotificationDto(Guid Id, Guid? PlantId, string Type, string Priority, string Title, string Body, string? Link, bool Read);
public sealed record DeviceDto(Guid Id, string Kind, string? Label);
public sealed record ChannelsDto(bool LineConfigured, bool LineFriend, bool WebPushConfigured, List<DeviceDto> Devices, bool HasPush);
public sealed record MemberChannelDto(Guid UserId, string DisplayName, bool ReceiveOrderNotifications, bool LineFriend, int WebPushDevices, bool HasPush);
public sealed record NotificationHealthDto(string Requirement, bool AnyRecipientHasPush, List<MemberChannelDto> Members);

public class NotificationTests(SmartShopFactory factory)
{
    private static async Task<List<NotificationDto>> InboxAsync(TestClient client) =>
        (await client.GetAsync<PagedDto<NotificationDto>>("/api/notifications")).Items;

    private static async Task<OrderDto> PlaceOrderAsync(TestClient customer, Guid shopId, Guid itemId, Guid paymentMethodId)
    {
        await customer.PostOkAsync("/api/cart/items", new { shopId, itemId, quantity = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { fulfillmentType = "Pickup", paymentMethodId }, options: TestClient.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await TestClient.ReadAsync<OrderDto>(await customer.Http.SendAsync(request));
    }

    [Fact]
    public async Task New_order_reaches_every_shop_member_who_has_notifications_on()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var secondPhone = await factory.JoinAsync(plant, admin, "owner-phone-2");
        var staff = await factory.JoinAsync(plant, admin);
        foreach (var (member, role) in new[] { (secondPhone, "Manager"), (staff, "Staff") })
        {
            var invite = await owner.PostAsync<InviteDto>($"/api/merchant/shops/{shopId}/invites", new { role });
            await member.PostOkAsync($"/api/shop-invites/{invite.Code}/accept");
        }
        // Staff is off duty: mutes order notifications for themselves.
        await staff.PutOkAsync($"/api/merchant/shops/{shopId}/members/{staff.UserId}/notifications", new { enabled = false });

        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "ข้าวแกง", price = 40 });
        var customer = await factory.JoinAsync(plant, admin);
        var order = await PlaceOrderAsync(customer, shopId, item.Item.Id, settings.Shop.PaymentMethods[0].Id);

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            (await InboxAsync(owner)).ShouldContain(n => n.Type == "order.new" && n.Priority == "High" && n.Title.Contains(order.OrderNo));
            (await InboxAsync(secondPhone)).ShouldContain(n => n.Type == "order.new");
        });
        (await InboxAsync(staff)).ShouldNotContain(n => n.Type == "order.new");

        await owner.PostOkAsync($"/api/merchant/orders/{order.Id}/accept");
        await FactoryExtensions.EventuallyAsync(async () =>
            (await InboxAsync(customer)).ShouldContain(n => n.Type == "order.accepted" && n.Link == $"/orders/{order.Id}"));
    }

    [Fact]
    public async Task Admins_hear_about_join_requests_and_applicants_about_decisions()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var member = await factory.JoinAsync(plant, admin);

        await FactoryExtensions.EventuallyAsync(async () => (await InboxAsync(admin)).ShouldContain(n => n.Type == "admin.join_request"));
        await FactoryExtensions.EventuallyAsync(async () => (await InboxAsync(member)).ShouldContain(n => n.Type == "membership.approved"));
    }

    [Fact]
    public async Task Inbox_supports_read_state()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        await factory.JoinAsync(plant, admin);
        await FactoryExtensions.EventuallyAsync(async () => (await InboxAsync(admin)).ShouldNotBeEmpty());

        var count = await admin.GetAsync<Dictionary<string, int>>("/api/notifications/unread-count");
        count["count"].ShouldBeGreaterThan(0);
        await admin.PostOkAsync("/api/notifications/read-all");
        (await admin.GetAsync<Dictionary<string, int>>("/api/notifications/unread-count"))["count"].ShouldBe(0);
    }

    [Fact]
    public async Task Devices_are_registered_per_user_and_listed_in_channels()
    {
        var user = await factory.LoginAsync();
        await user.PostOkAsync("/api/notifications/devices", new
        {
            kind = "WebPush", endpoint = $"https://push.example.com/{Guid.NewGuid()}", p256dh = "BElz6", auth = "abc", label = "มือถือแม่",
        });
        var channels = await user.GetAsync<ChannelsDto>("/api/notifications/channels");
        channels.Devices.Single().Label.ShouldBe("มือถือแม่");

        var invalid = await user.PostRawAsync("/api/notifications/devices", new { kind = "WebPush", endpoint = "http://insecure", p256dh = "x", auth = "y" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Line_webhook_requires_a_valid_signature_and_tracks_friendship()
    {
        var user = await factory.LoginAsync();
        var lineUserId = "U" + Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartShop.Modules.Identity.Data.IdentityDbContext>();
            db.ExternalLogins.Add(new SmartShop.Modules.Identity.Domain.ExternalLogin(user.UserId, "line", lineUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var body = $$$"""{"destination":"x","events":[{"type":"follow","source":{"type":"user","userId":"{{{lineUserId}}}"}}]}""";
        var anonymous = factory.CreateClient();

        using var forged = new StringContent(body, Encoding.UTF8, "application/json");
        forged.Headers.Add("X-Line-Signature", "forged");
        (await anonymous.PostAsync("/api/webhooks/line", forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var signed = new StringContent(body, Encoding.UTF8, "application/json");
        signed.Headers.Add("X-Line-Signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SmartShopFactory.LineChannelSecret), Encoding.UTF8.GetBytes(body))));
        (await anonymous.PostAsync("/api/webhooks/line", signed)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await user.GetAsync<ChannelsDto>("/api/notifications/channels")).LineFriend.ShouldBeTrue();
    }

    [Fact]
    public async Task Notification_health_shows_which_members_can_be_reached()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();
        var health = await owner.GetAsync<NotificationHealthDto>($"/api/merchant/shops/{shopId}/notification-health");
        health.Requirement.ShouldBe("Warn");
        health.AnyRecipientHasPush.ShouldBeFalse();
        health.Members.Single().UserId.ShouldBe(owner.UserId);
    }

    [Fact]
    public async Task Village_can_block_opening_a_shop_nobody_can_be_notified_for()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        await admin.PutOkAsync("/api/plant/admin/settings", new { name = plant.Name, pushRequirement = "Block" });

        var response = await owner.PostRawAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        (await TestClient.ProblemCodeAsync(response)).ShouldBe("no_push_channel");
        // Closing is always allowed.
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "close" });
    }
}
