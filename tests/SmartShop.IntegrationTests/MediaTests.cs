using System.Net;
using SkiaSharp;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public class MediaTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Uploaded_image_is_served_through_signed_urls_as_webp_variants()
    {
        var (_, admin) = await factory.CreatePlantAsync();

        var upload = await admin.UploadAsync("ItemImage", Scenario.Png(3000, 1000));

        upload.Width.ShouldBe(2400); // original capped
        upload.Url.ShouldNotBeNull();
        var anonymous = factory.CreateClient();
        var medium = await anonymous.GetAsync(upload.Url);
        medium.StatusCode.ShouldBe(HttpStatusCode.OK);
        medium.Content.Headers.ContentType!.MediaType.ShouldBe("image/webp");
        using var decoded = SKBitmap.Decode(await medium.Content.ReadAsByteArrayAsync());
        decoded.Width.ShouldBe(1200);
    }

    [Fact]
    public async Task Tampered_signature_is_rejected()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var upload = await admin.UploadAsync("ItemImage");

        var tampered = upload.Url![..^4] + "0000";
        (await factory.CreateClient().GetAsync(tampered)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Non_image_content_is_rejected_regardless_of_declared_type()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var fake = "MZ this is an executable"u8.ToArray();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => admin.UploadAsync("ItemImage", fake, "evil.png"));
        ex.Message.ShouldContain("media_type");
    }

    [Fact]
    public async Task Pdf_is_only_allowed_for_payment_slips()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var pdf = "%PDF-1.4\n%fake slip\n"u8.ToArray();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => admin.UploadAsync("ItemImage", pdf, "slip.pdf"));
        ex.Message.ShouldContain("media_type");
        (await admin.UploadAsync("PaymentSlip", pdf, "slip.pdf")).ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task Uploads_inside_a_village_require_membership()
    {
        var (plant, _) = await factory.CreatePlantAsync();
        var outsider = (await factory.LoginAsync()).InPlant(plant.Id);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => outsider.UploadAsync("ItemImage"));
        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
