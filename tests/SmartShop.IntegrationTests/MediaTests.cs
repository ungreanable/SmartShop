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
    public async Task Large_phone_photo_is_decoded_at_reduced_size_and_still_capped()
    {
        var (_, admin) = await factory.CreatePlantAsync();

        // 48 MP, like current phone cameras; decoded at 1/2 scale instead of ~190 MB of pixels.
        var upload = await admin.UploadAsync("ItemImage", Scenario.Jpeg(8000, 6000), "photo.jpg");

        upload.Width.ShouldBe(2400);
        upload.Height.ShouldBe(1800);
    }

    [Theory]
    [InlineData(6, "top")]    // rotate 90° clockwise: the stored left edge ends up on top
    [InlineData(8, "bottom")] // rotate 90° counter-clockwise
    [InlineData(5, "top")]    // mirrored variants written by some front cameras
    [InlineData(7, "bottom")]
    public async Task Portrait_phone_photo_is_turned_upright_without_distortion(ushort orientation, string redSide)
    {
        var (_, admin) = await factory.CreatePlantAsync();

        var upload = await admin.UploadAsync("ItemImage", Scenario.JpegWithOrientation(4000, 3000, orientation), "portrait.jpg");

        upload.Width.ShouldBe(1800);
        upload.Height.ShouldBe(2400);
        using var small = SKBitmap.Decode(await factory.CreateClient().GetByteArrayAsync(upload.ThumbnailUrl));
        ((double)small.Width / small.Height).ShouldBe(0.75, 0.01);
        var top = small.GetPixel(small.Width / 2, small.Height / 8);
        (top.Red > top.Blue ? "top" : "bottom").ShouldBe(redSide);
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
