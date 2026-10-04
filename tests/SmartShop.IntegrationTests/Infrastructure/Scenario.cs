using System.Net.Http.Headers;
using SkiaSharp;

namespace SmartShop.IntegrationTests.Infrastructure;

public sealed record PlantDto(Guid Id, string Name, string JoinCode);
public sealed record MemberRowDto(Guid MembershipId, Guid UserId, string DisplayName, string Role, string Status, string? HouseNo);
public sealed record PagedDto<T>(List<T> Items, int Total, int Page, int PageSize);
public sealed record UploadDto(Guid Id, string ContentType, int? Width, int? Height, string? Url, string? ThumbnailUrl);

/// <summary>Builders for common test set-ups (a village with an admin and approved members ...).</summary>
public static class Scenario
{
    public static async Task<(PlantDto Plant, TestClient Admin)> CreatePlantAsync(this SmartShopFactory factory, string? name = null)
    {
        var admin = await factory.LoginAsync("admin-" + Guid.NewGuid().ToString("N")[..6], systemAdmin: true);
        var plant = await admin.PostAsync<PlantDto>("/api/admin/plants", new { name = name ?? "หมู่บ้าน " + Guid.NewGuid().ToString("N")[..4] });
        admin.InPlant(plant.Id);
        return (plant, admin);
    }

    /// <summary>Requests to join with the plant's code and has the admin approve it.</summary>
    public static async Task<TestClient> JoinAsync(this SmartShopFactory factory, PlantDto plant, TestClient admin, string? name = null, string houseNo = "99/1")
    {
        var member = await factory.LoginAsync(name);
        await member.PostOkAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo, nickname = member.DisplayName });
        var pending = await admin.GetAsync<PagedDto<MemberRowDto>>("/api/plant/admin/members?status=Pending");
        var row = pending.Items.Single(r => r.UserId == member.UserId);
        await admin.PostOkAsync($"/api/plant/admin/members/{row.MembershipId}/approve");
        return member.InPlant(plant.Id);
    }

    /// <summary>A village with an approved shop owned by a fresh member.</summary>
    public static async Task<(PlantDto Plant, TestClient Admin, TestClient Owner, Guid ShopId)> CreateShopAsync(this SmartShopFactory factory, string shopName = "ร้านทดสอบ")
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var owner = await factory.JoinAsync(plant, admin, "owner-" + Guid.NewGuid().ToString("N")[..6]);
        var app = await owner.PostAsync<ApplicationDto>("/api/shop-applications", new { name = shopName, houseNo = "1/1" });
        var approved = await admin.PostAsync<ApprovedDto>($"/api/plant/admin/shop-applications/{app.Id}/approve", new { });
        return (plant, admin, owner, approved.ShopId);
    }

    public static byte[] Png(int width = 64, int height = 48, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color ?? SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static async Task<UploadDto> UploadAsync(this TestClient client, string purpose, byte[]? bytes = null, string fileName = "image.png")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(purpose), "purpose");
        using var response = await client.Http.PostAsync("/api/media", form);
        return await TestClient.ReadAsync<UploadDto>(response);
    }
}
