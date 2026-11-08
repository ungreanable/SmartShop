using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record AdminShopRowDto(Guid Id, string Name, int? DisplayOrder);

public class HomeFeedTests(SmartShopFactory factory)
{
    private async Task<(TestClient Owner, Guid ShopId)> ShopAsync(PlantDto plant, TestClient admin, string name, string? category = null)
    {
        var owner = await factory.JoinAsync(plant, admin);
        var app = await owner.PostAsync<ApplicationDto>("/api/shop-applications", new { name, category, houseNo = "1" });
        var approved = await admin.PostAsync<ApprovedDto>($"/api/plant/admin/shop-applications/{app.Id}/approve", new { });
        return (owner, approved.ShopId);
    }

    [Fact]
    public async Task Village_admin_orders_the_shop_list_and_open_shops_stay_first()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var (_, a) = await ShopAsync(plant, admin, "ร้านเอ");
        var (bOwner, b) = await ShopAsync(plant, admin, "ร้านบี");
        var (_, c) = await ShopAsync(plant, admin, "ร้านซี");
        var member = await factory.JoinAsync(plant, admin);
        await FactoryExtensions.EventuallyAsync(async () => (await member.GetAsync<HomeDto>("/api/home")).Total.ShouldBe(3));

        // Only the village admin may arrange the list; a shop owner is not one.
        (await member.PutRawAsync("/api/plant/admin/shops/order", new { shopIds = new[] { c, a, b } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await bOwner.PutRawAsync("/api/plant/admin/shops/order", new { shopIds = new[] { b, a, c } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await admin.PutOkAsync("/api/plant/admin/shops/order", new { shopIds = new[] { c, a, b } });
        (await admin.GetAsync<List<AdminShopRowDto>>("/api/plant/admin/shops")).Select(s => s.Id).ShouldBe([c, a, b]);
        await FactoryExtensions.EventuallyAsync(async () =>
            (await member.GetAsync<HomeDto>("/api/home")).Shops.Select(s => s.Id).ShouldBe([c, a, b]));

        // Open shops come before closed ones; the admin's order still applies inside each section.
        await bOwner.PostOkAsync($"/api/merchant/shops/{b}/status", new { action = "open" });
        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var home = await member.GetAsync<HomeDto>("/api/home");
            home.Shops.Select(s => s.Id).ShouldBe([b, c, a]);
            home.OpenCount.ShouldBe(1);
        });

        // A shop approved after the list was arranged goes to the end.
        var (_, d) = await ShopAsync(plant, admin, "ร้านดี");
        await FactoryExtensions.EventuallyAsync(async () =>
            (await member.GetAsync<HomeDto>("/api/home")).Shops.Select(s => s.Id).ShouldBe([b, c, a, d]));
    }

    [Fact]
    public async Task Home_pages_through_open_shops_before_closed_ones()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var shops = new List<(TestClient Owner, Guid Id)>();
        for (var i = 0; i < 5; i++) shops.Add(await ShopAsync(plant, admin, $"ร้าน {i}", i % 2 == 0 ? "อาหาร" : "ขนม"));
        // Open the last two so that "open first" is not just creation order.
        foreach (var (owner, id) in shops.TakeLast(2)) await owner.PostOkAsync($"/api/merchant/shops/{id}/status", new { action = "open" });
        var open = shops.TakeLast(2).Select(s => s.Id).ToHashSet();
        var member = await factory.JoinAsync(plant, admin);

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var all = await member.GetAsync<HomeDto>("/api/home?size=20");
            all.Total.ShouldBe(5);
            all.OpenCount.ShouldBe(2);
            all.HasMore.ShouldBeFalse();
            all.Shops.Take(2).Select(s => s.Id).ToHashSet().SetEquals(open).ShouldBeTrue();
            all.Shops.Take(2).ShouldAllBe(s => s.Status.AcceptingOrders);
            all.Shops.Skip(2).ShouldAllBe(s => !s.Status.AcceptingOrders);
        });

        var full = (await member.GetAsync<HomeDto>("/api/home?size=20")).Shops.Select(s => s.Id).ToList();
        var paged = new List<Guid>();
        for (var page = 1; ; page++)
        {
            var home = await member.GetAsync<HomeDto>($"/api/home?page={page}&size=2");
            home.Total.ShouldBe(5);
            home.OpenCount.ShouldBe(2);
            home.Categories.ShouldBe(["ขนม", "อาหาร"]); // every page carries the full category list
            paged.AddRange(home.Shops.Select(s => s.Id));
            if (page == 1) home.Shops.ShouldAllBe(s => s.Status.AcceptingOrders); // the open section is complete on page one
            if (!home.HasMore) break;
            page.ShouldBeLessThan(3);
        }
        paged.ShouldBe(full);

        // Category filter pages the filtered list: shops 0, 2 and 4 are "อาหาร", and 4 is open.
        var food = await member.GetAsync<HomeDto>("/api/home?category=" + Uri.EscapeDataString("อาหาร") + "&page=1&size=2");
        food.Total.ShouldBe(3);
        food.OpenCount.ShouldBe(1);
        food.HasMore.ShouldBeTrue();
        food.Shops[0].Id.ShouldBe(shops[4].Id);
        (await member.GetAsync<HomeDto>("/api/home?category=" + Uri.EscapeDataString("อาหาร") + "&page=3&size=2")).Shops.ShouldBeEmpty();
    }
}
