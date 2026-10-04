using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record MenuItemDto(Guid Id, Guid? CategoryId, string Kind, string StockMode, string Name, decimal Price, bool IsOrderable,
    string? UnavailableReason, int? Remaining, string? ThumbnailUrl, List<ModifierGroupDto> ModifierGroups);
public sealed record ModifierGroupDto(Guid Id, string Name, int MinSelect, int MaxSelect, List<ModifierOptionDto> Options);
public sealed record ModifierOptionDto(Guid Id, string Name, decimal PriceDelta, bool IsAvailable);
public sealed record MenuCategoryDto(Guid? Id, string Name, List<MenuItemDto> Items);
public sealed record MenuDto(Guid ShopId, List<MenuCategoryDto> Categories);
public sealed record CategoryDto(Guid Id, string Name, int SortOrder);
public sealed record MerchantItemDto(MenuItemDto Item, bool IsAvailable, bool IsSoldOut, int? OnHand, int? Reserved, int? DailyQuota);
public sealed record StockResultDto(int OnHand, int Reserved, int Available);
public sealed record MovementDto(Guid Id, string Type, int Quantity, int OnHandAfter, int ReservedAfter, Guid? OrderId);

public class CatalogTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Owner_builds_a_menu_that_members_can_browse()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var image = await owner.UploadAsync("ItemImage");
        var category = await owner.PostAsync<CategoryDto>($"/api/merchant/shops/{shopId}/catalog/categories", new { name = "ข้าว" });

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = "Tracked",
            name = "ข้าวมันไก่",
            price = 50,
            categoryId = category.Id,
            imageIds = new[] { image.Id },
            initialStock = 10,
            isRecommended = true,
        });
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Service",
            stockMode = "Untracked",
            name = "นวดเท้า",
            price = 200,
            durationMinutes = 60,
        });

        var customer = await factory.JoinAsync(plant, admin);
        var menu = await customer.GetAsync<MenuDto>($"/api/shops/{shopId}/menu");

        menu.Categories.First().Name.ShouldBe("★"); // recommended section first
        var rice = menu.Categories.Single(c => c.Id == category.Id).Items.Single();
        rice.IsOrderable.ShouldBeTrue();
        rice.Remaining.ShouldBe(10);
        rice.ThumbnailUrl.ShouldNotBeNull();
        menu.Categories.SelectMany(c => c.Items).ShouldContain(i => i.Name == "นวดเท้า" && i.Remaining == null);
    }

    [Fact]
    public async Task Sold_out_toggle_and_stock_changes_show_up_in_the_menu()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var untracked = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "น้ำแข็ง", price = 10 });
        var tracked = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Tracked", name = "น้ำพริก", price = 60, initialStock = 2 });

        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/catalog/items/{untracked.Item.Id}/sold-out", new { soldOut = true });
        var stock = await owner.PostAsync<StockResultDto>($"/api/merchant/shops/{shopId}/catalog/items/{tracked.Item.Id}/stock", new { action = "set", quantity = 0 });
        stock.Available.ShouldBe(0);

        var customer = await factory.JoinAsync(plant, admin);
        var items = (await customer.GetAsync<MenuDto>($"/api/shops/{shopId}/menu")).Categories.SelectMany(c => c.Items).ToList();
        items.Single(i => i.Name == "น้ำแข็ง").UnavailableReason.ShouldBe("sold_out");
        items.Single(i => i.Name == "น้ำพริก").UnavailableReason.ShouldBe("sold_out");

        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/catalog/items/{tracked.Item.Id}/stock", new { action = "add", quantity = 5, note = "ทำเพิ่ม" });
        var movements = await owner.GetAsync<List<MovementDto>>($"/api/merchant/shops/{shopId}/catalog/items/{tracked.Item.Id}/movements");
        movements.Select(m => m.Type).ShouldBe(["Restock", "Adjust", "Adjust"]);
        movements[0].OnHandAfter.ShouldBe(5);
    }

    [Fact]
    public async Task Availability_window_hides_items_outside_their_hours()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var from = TimeOnly.FromDateTime(now.AddHours(2).DateTime);
        var to = TimeOnly.FromDateTime(now.AddHours(3).DateTime);
        await owner.PostOkAsync($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = "Untracked",
            name = "โจ๊ก",
            price = 35,
            windows = new[] { new { days = Array.Empty<int>(), from = from.ToString("HH:mm"), to = to.ToString("HH:mm") } },
        });

        var customer = await factory.JoinAsync(plant, admin);
        var item = (await customer.GetAsync<MenuDto>($"/api/shops/{shopId}/menu")).Categories.SelectMany(c => c.Items).Single();
        item.UnavailableReason.ShouldBe("outside_hours");
    }

    [Fact]
    public async Task Option_groups_are_validated()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync();
        var invalid = await owner.PostRawAsync($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = "Untracked",
            name = "ก๋วยเตี๋ยว",
            price = 40,
            modifierGroups = new[] { new { name = "ขนาด", minSelect = 2, maxSelect = 1, options = new[] { new { name = "ธรรมดา", priceDelta = 0, isAvailable = true } } } },
        });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var ok = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = "Untracked",
            name = "ก๋วยเตี๋ยว",
            price = 40,
            modifierGroups = new[]
            {
                new { name = "ขนาด", minSelect = 1, maxSelect = 1, options = new[] { new { name = "ธรรมดา", priceDelta = 0m, isAvailable = true }, new { name = "พิเศษ", priceDelta = 10m, isAvailable = true } } },
            },
        });
        ok.Item.ModifierGroups.Single().Options.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Staff_can_toggle_stock_but_not_create_items()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var staff = await factory.JoinAsync(plant, admin);
        var invite = await owner.PostAsync<InviteDto>($"/api/merchant/shops/{shopId}/invites", new { role = "Staff" });
        await staff.PostOkAsync($"/api/shop-invites/{invite.Code}/accept");

        var item = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items",
            new { kind = "Product", stockMode = "Untracked", name = "กาแฟ", price = 30 });

        await staff.PutOkAsync($"/api/merchant/shops/{shopId}/catalog/items/{item.Item.Id}/sold-out", new { soldOut = true });
        (await staff.PostRawAsync($"/api/merchant/shops/{shopId}/catalog/items", new { kind = "Product", stockMode = "Untracked", name = "x", price = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
