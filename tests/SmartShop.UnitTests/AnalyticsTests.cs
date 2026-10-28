using Microsoft.Extensions.Configuration;
using SmartShop.Modules.Analytics.Services;

namespace SmartShop.UnitTests;

public class VisitorKeyTests
{
    private static readonly VisitorKeys Keys = new("unit-test-analytics-secret-0123456789abcdef");
    private static readonly Guid User = Guid.Parse("6f1c2a4e-0d3b-4c55-9a77-1b2c3d4e5f60");
    private static readonly DateOnly Day = new(2026, 10, 29);

    [Fact]
    public void Same_user_and_day_gives_the_same_short_key()
    {
        var key = Keys.Visitor(User, Day);
        key.ShouldBe(Keys.Visitor(User, Day));
        key.Length.ShouldBe(16);
        key.ShouldMatch("^[0-9a-f]{16}$");
        key.ShouldNotContain(User.ToString("N")[..8]);
    }

    [Fact]
    public void Another_day_or_user_gives_another_key()
    {
        Keys.Visitor(User, Day).ShouldNotBe(Keys.Visitor(User, Day.AddDays(1)));
        Keys.Visitor(User, Day).ShouldNotBe(Keys.Visitor(Guid.NewGuid(), Day));
    }

    [Fact]
    public void Key_depends_on_the_secret_and_not_on_the_ui_culture()
    {
        Keys.Visitor(User, Day).ShouldNotBe(new VisitorKeys("another-secret-0123456789abcdef0123").Visitor(User, Day));

        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            // The Thai culture formats dates in the Buddhist era (2569); the key must not change with it.
            var expected = Keys.Visitor(User, Day);
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            Keys.Visitor(User, Day).ShouldBe(expected);
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; }
    }

    [Fact]
    public void Repeat_customer_key_is_per_shop_and_unrelated_to_the_visitor_key()
    {
        var shopA = Guid.NewGuid();
        Keys.Customer(shopA, User).ShouldBe(Keys.Customer(shopA, User));
        Keys.Customer(shopA, User).ShouldNotBe(Keys.Customer(Guid.NewGuid(), User));
        Keys.Customer(shopA, User).ShouldNotStartWith(Keys.Visitor(User, Day));
    }

    [Fact]
    public void Bangkok_day_starts_at_midnight_thai_time()
    {
        VisitorKeys.BangkokDay(new DateTimeOffset(2026, 10, 28, 16, 59, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 10, 28));
        VisitorKeys.BangkokDay(new DateTimeOffset(2026, 10, 28, 17, 0, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 10, 29));
        VisitorKeys.StartOf(new DateOnly(2026, 10, 29)).ShouldBe(new DateTimeOffset(2026, 10, 28, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Secret_falls_back_to_the_signing_key_when_not_configured()
    {
        var jwtOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Jwt:SigningKey"] = "jwt-key-0123456789abcdef0123456789" }).Build();
        new VisitorKeys(jwtOnly).Visitor(User, Day).ShouldBe(new VisitorKeys("jwt-key-0123456789abcdef0123456789").Visitor(User, Day));
        Should.Throw<InvalidOperationException>(() => new VisitorKeys(new ConfigurationBuilder().Build()));
    }
}

public class InMemoryPresenceTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 5, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Counts_distinct_viewers_until_their_heartbeat_expires()
    {
        var clock = new ManualClock();
        var presence = new InMemoryPresence(clock);
        var shop = Guid.NewGuid();

        (await presence.TouchAsync(shop, "a")).ShouldBe(1);
        (await presence.TouchAsync(shop, "a")).ShouldBe(1);
        (await presence.TouchAsync(shop, "b")).ShouldBe(2);
        (await presence.CountAsync(Guid.NewGuid())).ShouldBe(0);

        clock.Now += TimeSpan.FromSeconds(40);
        (await presence.TouchAsync(shop, "b")).ShouldBe(2); // a's last beat was 40 s ago: still there

        clock.Now += Presence.Ttl - TimeSpan.FromSeconds(30);
        (await presence.CountAsync(shop)).ShouldBe(1); // a missed two beats

        await presence.LeaveAsync(shop, "b");
        (await presence.CountAsync(shop)).ShouldBe(0);
    }
}
