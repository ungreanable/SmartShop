using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartShop.IntegrationTests.Infrastructure;

/// <summary>An authenticated API client for one test user, optionally scoped to a plant.</summary>
public sealed class TestClient(HttpClient http, Guid userId, string displayName, string refreshToken)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Http { get; } = http;
    public Guid UserId { get; } = userId;
    public string DisplayName { get; } = displayName;
    public string RefreshToken { get; } = refreshToken;

    public TestClient InPlant(Guid plantId)
    {
        Http.DefaultRequestHeaders.Remove("X-Plant-Id");
        Http.DefaultRequestHeaders.Add("X-Plant-Id", plantId.ToString());
        return this;
    }

    public async Task<T> GetAsync<T>(string url)
    {
        using var response = await Http.GetAsync(url);
        return await ReadAsync<T>(response);
    }

    public async Task<T> PostAsync<T>(string url, object? body = null)
    {
        using var response = await Http.PostAsJsonAsync(url, body ?? new { }, Json);
        return await ReadAsync<T>(response);
    }

    public async Task<T> PutAsync<T>(string url, object body)
    {
        using var response = await Http.PutAsJsonAsync(url, body, Json);
        return await ReadAsync<T>(response);
    }

    public Task<HttpResponseMessage> PostRawAsync(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { }, Json);
    public Task<HttpResponseMessage> PutRawAsync(string url, object body) => Http.PutAsJsonAsync(url, body, Json);
    public Task<HttpResponseMessage> GetRawAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> DeleteRawAsync(string url) => Http.DeleteAsync(url);

    public async Task<HttpResponseMessage> PostOkAsync(string url, object? body = null)
    {
        var response = await PostRawAsync(url, body);
        await EnsureSuccessAsync(response);
        return response;
    }

    public async Task<HttpResponseMessage> PutOkAsync(string url, object body)
    {
        var response = await PutRawAsync(url, body);
        await EnsureSuccessAsync(response);
        return response;
    }

    public async Task<HttpResponseMessage> DeleteOkAsync(string url)
    {
        var response = await DeleteRawAsync(url);
        await EnsureSuccessAsync(response);
        return response;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}",
                null, response.StatusCode);
        }
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

public static class FactoryExtensions
{
    private sealed record AuthUserDto(Guid Id, string DisplayName, bool IsSystemAdmin);
    private sealed record AuthResultDto(string AccessToken, string RefreshToken, AuthUserDto User);

    public static async Task<TestClient> LoginAsync(this SmartShopFactory factory, string? name = null, bool systemAdmin = false)
    {
        name ??= "user-" + Guid.NewGuid().ToString("N")[..8];
        var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/auth/dev", new { key = name, displayName = name, systemAdmin });
        var auth = await TestClient.ReadAsync<AuthResultDto>(response);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestClient(http, auth.User.Id, name, auth.RefreshToken);
    }

    /// <summary>Polls until the assertion passes; used for effects of asynchronous event handlers.</summary>
    public static async Task EventuallyAsync(Func<Task> assertion, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    public static void ShouldHaveStatus(this HttpResponseMessage response, HttpStatusCode status) =>
        response.StatusCode.ShouldBe(status, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
}
