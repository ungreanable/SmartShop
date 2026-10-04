using System.Net;
using SmartShop.Modules.Integrations;
using SmartShop.Modules.Promotions;
using SmartShop.SharedKernel;

namespace SmartShop.UnitTests;

public class WebhookUrlPolicyTests
{
    private static readonly WebhookOptions Production = new();

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_and_special_addresses_are_not_public(string address) =>
        WebhookUrlPolicy.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("203.0.113.10")]
    [InlineData("2606:4700:4700::1111")]
    public void Internet_addresses_are_public(string address) =>
        WebhookUrlPolicy.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();

    [Theory]
    [InlineData("http://example.com/hook", "webhook_url_insecure")]
    [InlineData("https://127.0.0.1/hook", "webhook_url_private")]
    [InlineData("https://localhost/hook", "webhook_url_private")]
    [InlineData("https://10.1.2.3/hook", "webhook_url_private")]
    [InlineData("https://user:pass@example.com/hook", "webhook_url_invalid")]
    [InlineData("ftp://example.com", "webhook_url_insecure")]
    [InlineData("not a url", "webhook_url_invalid")]
    public void Unsafe_urls_are_rejected_in_production(string url, string code) =>
        Should.Throw<DomainException>(() => WebhookUrlPolicy.Validate(url, Production)).Code.ShouldBe(code);

    [Fact]
    public void Https_public_urls_are_accepted() =>
        WebhookUrlPolicy.Validate("https://hooks.example.com/smartshop?x=1", Production).Host.ShouldBe("hooks.example.com");

    [Fact]
    public void Signature_is_hmac_of_timestamp_and_body() =>
        // Vector computed independently: hmac.new(b'whsec_test', b'1700000000.{"a":1}', sha256).hexdigest()
        WebhookSender.Sign("whsec_test", "1700000000", "{\"a\":1}").ShouldBe("38877139021993b830af32feea6e18a8da83eb2f6e49ee50bd9e4cf4ca4d3789");
}

public class WebhookDeliveryTests
{
    [Fact]
    public void Failed_deliveries_back_off_and_give_up_after_max_attempts()
    {
        var endpoint = WebhookEndpoint.Create(Guid.NewGuid(), Guid.NewGuid(), "https://example.com/h", ["order.placed"], DateTimeOffset.UtcNow);
        var delivery = WebhookDelivery.Create(Guid.NewGuid(), endpoint, "order.placed", "{}", DateTimeOffset.UtcNow);
        var delays = new List<TimeSpan?>();
        for (var i = 0; i < WebhookDelivery.MaxAttempts; i++) delays.Add(delivery.Failed(500, "HTTP 500", DateTimeOffset.UtcNow));

        delays.Take(WebhookDelivery.MaxAttempts - 1).ShouldAllBe(d => d != null);
        delays.Take(WebhookDelivery.MaxAttempts - 1).Select(d => d!.Value).ShouldBeInOrder();
        delays.Last().ShouldBeNull();
        delivery.Status.ShouldBe(DeliveryStatus.Failed);
    }

    [Fact]
    public void Endpoint_is_disabled_after_too_many_consecutive_failures()
    {
        var endpoint = WebhookEndpoint.Create(Guid.NewGuid(), Guid.NewGuid(), "https://example.com/h", ["order.placed"], DateTimeOffset.UtcNow);
        for (var i = 0; i < WebhookEndpoint.DisableAfterFailures - 1; i++) endpoint.RecordFailure();
        endpoint.Active.ShouldBeTrue();
        endpoint.RecordFailure();
        endpoint.Active.ShouldBeFalse();
        endpoint.DisabledReason.ShouldBe("too_many_failures");
    }

    [Fact]
    public void Api_key_is_only_stored_as_a_hash()
    {
        var (key, plain) = ApiKey.Create(Guid.NewGuid(), Guid.NewGuid(), "pos", [ApiScopes.OrdersRead], Guid.NewGuid(), DateTimeOffset.UtcNow);
        plain.ShouldStartWith($"ssk_{key.Prefix}_");
        key.Hash.ShouldBe(ApiKey.HashOf(plain));
        key.Hash.ShouldNotContain(plain[^10..]);
        Should.Throw<DomainException>(() => ApiKey.Create(Guid.NewGuid(), Guid.NewGuid(), "x", ["admin:all"], Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}

public class PromotionTests
{
    private static Promotion Make(PromotionType type, decimal value, decimal? min = null, decimal? cap = null, string? code = null) =>
        Promotion.Create(Guid.NewGuid(), Guid.NewGuid(),
            new PromotionInput(code, "promo", type, value, min, cap, null, null, null, null, false, true), DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(PromotionType.Percent, 10, null, null, 250, 25)]
    [InlineData(PromotionType.Percent, 10, null, 20, 250, 20)]
    [InlineData(PromotionType.Percent, 15, null, null, 99, 14)] // rounded down to whole baht
    [InlineData(PromotionType.Amount, 50, null, null, 30, 30)] // never more than the subtotal
    [InlineData(PromotionType.Amount, 20, 100, null, 99, 0)] // below the minimum order
    [InlineData(PromotionType.Amount, 20, 100, null, 100, 20)]
    public void Discount_rules(PromotionType type, decimal value, int? min, int? cap, decimal subtotal, decimal expected) =>
        Make(type, value, (decimal?)min, (decimal?)cap).DiscountFor(subtotal).ShouldBe(expected);

    [Fact]
    public void Codes_are_normalised_and_codeless_promotions_apply_automatically()
    {
        Make(PromotionType.Amount, 10, code: " summer-10 ").Code.ShouldBe("SUMMER-10");
        Make(PromotionType.Amount, 10).AutoApply.ShouldBeTrue();
        Should.Throw<DomainException>(() => Make(PromotionType.Amount, 10, code: "ลดราคา"));
        Should.Throw<DomainException>(() => Make(PromotionType.Percent, 150));
    }

    [Fact]
    public void Promotion_is_live_only_inside_its_window_and_limits()
    {
        var now = DateTimeOffset.UtcNow;
        var p = Promotion.Create(Guid.NewGuid(), Guid.NewGuid(),
            new PromotionInput(null, "x", PromotionType.Amount, 5, null, null, now.AddHours(-1), now.AddHours(1), null, null, true, true), now);
        p.IsLive(now).ShouldBeTrue();
        p.IsLive(now.AddHours(2)).ShouldBeFalse();
        p.IsLive(now.AddHours(-2)).ShouldBeFalse();
    }
}
