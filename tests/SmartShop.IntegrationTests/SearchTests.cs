using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record StatusLiteDto(string State, string Reason, bool AcceptingOrders);
public sealed record ShopCardDto(Guid Id, string Name, string? Category, StatusLiteDto Status, bool AcceptsPreorders, bool IsNew);
public sealed record HomeDto(List<string> Categories, List<ShopCardDto> OpenNow, List<ShopCardDto> Shops);
public sealed record ItemHitDto(Guid ItemId, Guid ShopId, string ShopName, string Name, decimal Price, bool IsSoldOut);
public sealed record SearchResultDto(List<ShopCardDto> Shops, List<ItemHitDto> Items);

public class SearchTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Home_feed_lists_open_shops_first_and_search_finds_thai_items()
    {
        var (plant, admin) = await factory.CreatePlantAsync();

        async Task<(TestClient Owner, Guid ShopId)> ShopAsync(string name, string category)
        {
            var owner = await factory.JoinAsync(plant, admin);
            var app = await owner.PostAsync<ApplicationDto>("/api/shop-applications", new { name, category, houseNo = "1" });
            var approved = await admin.PostAsync<ApprovedDto>($"/api/plant/admin/shop-applications/{app.Id}/approve", new { });
            return (owner, approved.ShopId);
        }

        var (closedOwner, closedShop) = await ShopAsync("ร้านปิดอยู่", "ขนม");
        var (openOwner, openShop) = await ShopAsync("ร้านป้าแดง", "อาหาร");
        await openOwner.PostOkAsync($"/api/merchant/shops/{openShop}/status", new { action = "open" });
        await openOwner.PostOkAsync($"/api/merchant/shops/{openShop}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "ข้าวมันไก่ต้ม", price = 50 });

        var member = await factory.JoinAsync(plant, admin);
        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var home = await member.GetAsync<HomeDto>("/api/home");
            home.Shops.Count.ShouldBe(2);
            home.Shops[0].Id.ShouldBe(openShop);
            home.OpenNow.Select(s => s.Id).ShouldBe([openShop]);
            home.Categories.ShouldBe(["ขนม", "อาหาร"]);
            home.Shops.ShouldAllBe(s => s.IsNew);
        });

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var result = await member.GetAsync<SearchResultDto>("/api/search?q=มันไก่");
            result.Items.Single().ShopName.ShouldBe("ร้านป้าแดง");
            result.Shops.Single().Id.ShouldBe(openShop); // item names are part of the shop's search text
        });

        var filtered = await member.GetAsync<HomeDto>("/api/home?category=ขนม");
        filtered.Shops.Single().Id.ShouldBe(closedShop);
        _ = closedOwner;
    }

    [Fact]
    public async Task Search_is_scoped_to_the_current_village()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync("ร้านลับเฉพาะ");
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/status", new { action = "open" });
        var (otherPlant, otherAdmin) = await factory.CreatePlantAsync();
        var outsider = await factory.JoinAsync(otherPlant, otherAdmin);

        await Task.Delay(1000);
        (await outsider.GetAsync<SearchResultDto>("/api/search?q=ลับเฉพาะ")).Shops.ShouldBeEmpty();
    }
}
