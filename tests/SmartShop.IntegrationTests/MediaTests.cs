using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SmartShop.Contracts.Media;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Storage;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public class MediaTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Uploaded_image_is_served_through_signed_urls_as_webp_variants()
    {
        var (_, admin) = await factory.CreatePlantAsync();

        var upload = await admin.UploadAsync("ItemImage", Scenario.Png(3000, 1000));

        upload.Width.ShouldBe(2048); // original capped
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

        upload.Width.ShouldBe(2048);
        upload.Height.ShouldBe(1536);
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

        upload.Width.ShouldBe(1536);
        upload.Height.ShouldBe(2048);
        using var small = SKBitmap.Decode(await factory.CreateClient().GetByteArrayAsync(upload.ThumbnailUrl));
        ((double)small.Width / small.Height).ShouldBe(0.75, 0.01);
        var top = small.GetPixel(small.Width / 2, small.Height / 8);
        (top.Red > top.Blue ? "top" : "bottom").ShouldBe(redSide);
    }

    [Fact]
    public async Task Oversized_upload_is_refused_with_413_and_a_code()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[16 * 1024 * 1024]), "file", "huge.jpg" },
            { new StringContent("PlantPicture"), "purpose" },
        };

        var response = await admin.Http.PostAsync("/api/media", form);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await TestClient.ProblemCodeAsync(response)).ShouldBe("media_too_large");
    }

    [Fact]
    public async Task Cached_image_is_revalidated_with_its_etag_without_sending_it_again()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var upload = await admin.UploadAsync("ItemImage");
        var anonymous = factory.CreateClient();

        var first = await anonymous.GetAsync(upload.Url);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = first.Headers.ETag.ShouldNotBeNull();

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, upload.Url);
        revalidate.Headers.IfNoneMatch.Add(etag);
        var second = await anonymous.SendAsync(revalidate);
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();

        // The signature is still checked first: a matching ETag does not open an unsigned URL.
        using var tampered = new HttpRequestMessage(HttpMethod.Get, upload.Url![..^4] + "0000");
        tampered.Headers.IfNoneMatch.Add(etag);
        (await anonymous.SendAsync(tampered)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    [Fact]
    public async Task Original_is_stored_as_webp_too()
    {
        var (_, admin) = await factory.CreatePlantAsync();

        var upload = await admin.UploadAsync("ItemImage", Scenario.Jpeg(1600, 1200), "photo.jpg");

        upload.ContentType.ShouldBe("image/webp");
        var original = await factory.CreateClient().GetAsync(OriginalUrl(upload.Id));
        original.Content.Headers.ContentType!.MediaType.ShouldBe("image/webp");
        using var decoded = SKBitmap.Decode(await original.Content.ReadAsByteArrayAsync());
        decoded.Width.ShouldBe(1600);
    }

    [Fact]
    public async Task Webp_already_shrunk_on_the_phone_is_kept_as_it_is()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var webp = Scenario.Webp(1500, 1000);

        var upload = await admin.UploadAsync("ItemImage", webp, "photo.webp");

        (await factory.CreateClient().GetByteArrayAsync(OriginalUrl(upload.Id))).ShouldBe(webp);
    }

    [Fact]
    public async Task Webp_from_chrome_with_its_colour_profile_is_kept_as_it_is()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var webp = WithColourProfile(Scenario.Webp(1500, 1000), 1500, 1000);

        var upload = await admin.UploadAsync("ItemImage", webp, "photo.webp");

        (await factory.CreateClient().GetByteArrayAsync(OriginalUrl(upload.Id))).ShouldBe(webp);
    }

    [Fact]
    public async Task Webp_carrying_metadata_is_encoded_again_so_the_metadata_is_dropped()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var webp = WithChunk(Scenario.Webp(1500, 1000), "EXIF", "GPS 13.7563 100.5018"u8.ToArray());

        var upload = await admin.UploadAsync("ItemImage", webp, "photo.webp");

        var stored = await factory.CreateClient().GetByteArrayAsync(OriginalUrl(upload.Id));
        stored.ShouldNotBe(webp);
        System.Text.Encoding.ASCII.GetString(stored).ShouldNotContain("GPS");
    }

    [Fact]
    public async Task Small_picture_is_served_from_memory_after_the_first_view()
    {
        var (_, admin) = await factory.CreatePlantAsync();
        var upload = await admin.UploadAsync("ItemImage");
        var anonymous = factory.CreateClient();
        var first = await anonymous.GetByteArrayAsync(upload.ThumbnailUrl);

        // Gone from object storage: only the in-memory copy can answer now.
        await factory.Services.GetRequiredService<IObjectStorage>().DeleteAsync($"{upload.Id:N}/sm.webp");

        (await anonymous.GetByteArrayAsync(upload.ThumbnailUrl)).ShouldBe(first);
    }

    [Fact]
    public async Task Api_responses_say_how_long_the_server_took()
    {
        var response = await factory.CreateClient().GetAsync("/api/me");

        response.Headers.GetValues("Server-Timing").ShouldContain(v => v.StartsWith("app;dur=", StringComparison.Ordinal));
    }

    private string OriginalUrl(Guid id) => factory.Services.GetRequiredService<IMediaUrls>().For(id, MediaVariant.Original)!;

    /// <summary>The layout Chrome writes: VP8X header, ICC colour profile, then the picture.</summary>
    private static byte[] WithColourProfile(byte[] simple, int width, int height)
    {
        System.Text.Encoding.ASCII.GetString(simple, 12, 4).ShouldBe("VP8 ");
        var header = new byte[18];
        "VP8X"u8.CopyTo(header);
        header[4] = 10;
        header[8] = 0x20; // ICC profile present
        WriteUInt24(header.AsSpan(12), width - 1);
        WriteUInt24(header.AsSpan(15), height - 1);
        var profile = new byte[8 + 456];
        "ICCP"u8.CopyTo(profile);
        BinaryPrimitives.WriteUInt32LittleEndian(profile.AsSpan(4), 456);
        byte[] result = [.. simple[..12], .. header, .. profile, .. simple[12..]];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;

        static void WriteUInt24(Span<byte> target, int value) =>
            (target[0], target[1], target[2]) = ((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    private static byte[] WithChunk(byte[] webp, string type, byte[] data)
    {
        var padded = data.Length + (data.Length & 1);
        var result = new byte[webp.Length + 8 + padded];
        webp.CopyTo(result, 0);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(result, webp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(webp.Length + 4), (uint)data.Length);
        data.CopyTo(result, webp.Length + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }
}
