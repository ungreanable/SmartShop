using System.Net;
using System.Net.Http.Json;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public class IdentityTests(SmartShopFactory factory)
{
    private sealed record Me(Guid Id, string DisplayName, string? Phone, bool IsSystemAdmin, string? ConsentVersion, List<string> LinkedProviders);
    private sealed record Tokens(string AccessToken, string RefreshToken);

    [Fact]
    public async Task Dev_login_registers_user_and_returns_profile()
    {
        var client = await factory.LoginAsync("Somchai");

        var me = await client.GetAsync<Me>("/api/me");

        me.Id.ShouldBe(client.UserId);
        me.DisplayName.ShouldBe("Somchai");
        me.LinkedProviders.ShouldBe(["dev"]);
    }

    [Fact]
    public async Task Same_login_key_returns_the_same_user()
    {
        var first = await factory.LoginAsync("same-person");
        var second = await factory.LoginAsync("same-person");

        second.UserId.ShouldBe(first.UserId);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var response = await factory.CreateClient().GetAsync("/api/me");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Profile_and_consent_can_be_updated()
    {
        var client = await factory.LoginAsync();

        var updated = await client.PutAsync<Me>("/api/me", new { displayName = "ป้าแดง", phone = "081-234-5678", locale = "th" });
        updated.DisplayName.ShouldBe("ป้าแดง");
        updated.Phone.ShouldBe("0812345678");

        var consented = await client.PostAsync<Me>("/api/me/consent", new { version = "2026-10" });
        consented.ConsentVersion.ShouldBe("2026-10");
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_the_session_family()
    {
        var client = await factory.LoginAsync();
        var anonymous = factory.CreateClient();

        var rotated = await TestClient.ReadAsync<Tokens>(await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = client.RefreshToken }));
        rotated.RefreshToken.ShouldNotBe(client.RefreshToken);

        // Re-using the old (rotated) token is treated as theft...
        (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = client.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // ...and invalidates the newer token too.
        (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_contains_profile_section()
    {
        var client = await factory.LoginAsync("exporter");
        var export = await client.GetAsync<Dictionary<string, System.Text.Json.JsonElement>>("/api/me/export");
        export["profile"].GetProperty("displayName").GetString().ShouldBe("exporter");
    }

    [Fact]
    public async Task Erasing_account_anonymises_and_revokes_tokens()
    {
        var client = await factory.LoginAsync();
        (await client.DeleteRawAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = client.RefreshToken });
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
