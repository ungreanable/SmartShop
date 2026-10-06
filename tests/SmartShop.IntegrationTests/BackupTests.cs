using System.Text.Json.Nodes;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record CatalogRestoreDto(int CategoriesCreated, int ItemsCreated, int ItemsUpdated, int ItemsWithoutImages);
public sealed record ShopRestoreDto(int PaymentMethodsCreated, int PaymentMethodsUpdated, bool HoursRestored, bool PicturesKept);

public class BackupTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Backup_restores_settings_and_brings_deleted_items_back()
    {
        var (_, _, owner, shopId) = await factory.CreateShopAsync("ร้านสำรอง");
        var category = await owner.PostAsync<CategoryDto>($"/api/merchant/shops/{shopId}/catalog/categories", new { name = "ของหวาน" });
        var cake = await owner.PostAsync<MerchantItemDto>($"/api/merchant/shops/{shopId}/catalog/items", new
        {
            kind = "Product",
            stockMode = "Tracked",
            name = "เค้กส้ม",
            price = 65,
            categoryId = category.Id,
            initialStock = 8,
            modifierGroups = new[] { new { name = "ขนาด", minSelect = 1, maxSelect = 1, options = new[] { new { name = "ปอนด์", priceDelta = 300, isAvailable = true } } } },
        });
        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/delivery-options", new { pickupEnabled = true, deliveryEnabled = true, deliveryMinOrder = 120 });

        var shopBackup = await owner.GetAsync<JsonObject>($"/api/merchant/shops/{shopId}/backup");
        var catalogBackup = await owner.GetAsync<JsonObject>($"/api/merchant/shops/{shopId}/catalog/backup");

        // Things go wrong: the item is deleted and settings are changed.
        await owner.DeleteOkAsync($"/api/merchant/shops/{shopId}/catalog/items/{cake.Item.Id}");
        await owner.PutOkAsync($"/api/merchant/shops/{shopId}/delivery-options", new { pickupEnabled = true, deliveryEnabled = false });

        var shopResult = await owner.PostAsync<ShopRestoreDto>($"/api/merchant/shops/{shopId}/backup/restore", shopBackup);
        var catalogResult = await owner.PostAsync<CatalogRestoreDto>($"/api/merchant/shops/{shopId}/catalog/backup/restore", catalogBackup);

        shopResult.HoursRestored.ShouldBeTrue();
        catalogResult.ShouldBe(new CatalogRestoreDto(0, 1, 0, 0));
        var settings = await owner.GetAsync<ShopSettingsDto>($"/api/merchant/shops/{shopId}");
        settings.Shop.DeliveryEnabled.ShouldBeTrue();

        var items = await owner.GetAsync<List<MerchantItemDto>>($"/api/merchant/shops/{shopId}/catalog/items");
        var restored = items.Single();
        restored.Item.Name.ShouldBe("เค้กส้ม");
        restored.Item.CategoryId.ShouldBe(category.Id);
        restored.OnHand.ShouldBe(8);
        restored.Item.ModifierGroups.Single().Options.Single().PriceDelta.ShouldBe(300);

        // Restoring again only updates: nothing is duplicated.
        (await owner.PostAsync<CatalogRestoreDto>($"/api/merchant/shops/{shopId}/catalog/backup/restore", catalogBackup))
            .ShouldBe(new CatalogRestoreDto(0, 0, 1, 0));
    }

    [Fact]
    public async Task Staff_cannot_read_the_backup()
    {
        var (plant, admin, owner, shopId) = await factory.CreateShopAsync();
        var outsider = await factory.JoinAsync(plant, admin);
        (await outsider.GetRawAsync($"/api/merchant/shops/{shopId}/backup")).IsSuccessStatusCode.ShouldBeFalse();
        (await outsider.GetRawAsync($"/api/merchant/shops/{shopId}/catalog/backup")).IsSuccessStatusCode.ShouldBeFalse();
        _ = owner;
    }
}
