using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace SmartShop.UI.Services;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed record ApiProblem(int Status, string? Code, string Message)
{
    public static async Task<ApiProblem> ReadAsync(HttpResponseMessage response)
    {
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
            var code = doc.TryGetProperty("code", out var c) ? c.GetString() : null;
            var title = doc.TryGetProperty("title", out var t) ? t.GetString() : null;
            return new ApiProblem((int)response.StatusCode, code, title ?? response.ReasonPhrase ?? "Error");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new ApiProblem((int)response.StatusCode, null, response.ReasonPhrase ?? "Error");
        }
    }
}

public sealed class ApiException(ApiProblem problem) : Exception(problem.Message)
{
    public ApiProblem Problem { get; } = problem;
}

/// <summary>
/// Thin JSON client for the SmartShop API. Adds the bearer token and the current village header, retries once
/// after refreshing on 401, and (unless <c>silent</c>) shows problems as snackbars.
/// </summary>
public sealed class Api(HttpClient http, Session session, PlantContextAccessor plant, ISnackbar snackbar, NavigationManager nav, Loc l)
{
    public Task<T?> Get<T>(string url, bool silent = false) => Send<T>(HttpMethod.Get, url, null, silent);
    public Task<T?> Post<T>(string url, object? body = null, bool silent = false, string? idempotencyKey = null) =>
        Send<T>(HttpMethod.Post, url, body ?? new { }, silent, idempotencyKey);
    public Task<T?> Put<T>(string url, object body, bool silent = false) => Send<T>(HttpMethod.Put, url, body, silent);

    public async Task<bool> Post(string url, object? body = null, bool silent = false) => await SendRaw(HttpMethod.Post, url, body ?? new { }, silent) is not null;
    public async Task<bool> Put(string url, object body, bool silent = false) => await SendRaw(HttpMethod.Put, url, body, silent) is not null;
    public async Task<bool> Delete(string url, bool silent = false) => await SendRaw(HttpMethod.Delete, url, null, silent) is not null;

    /// <summary>Last problem returned by the API (useful to branch on <c>code</c>).</summary>
    public ApiProblem? LastProblem { get; private set; }

    public async Task<T?> Send<T>(HttpMethod method, string url, object? body, bool silent = false, string? idempotencyKey = null)
    {
        using var response = await SendRaw(method, url, body, silent, idempotencyKey);
        if (response is null || response.StatusCode == HttpStatusCode.NoContent) return default;
        // An empty 200 body means "nothing" too; never let it crash the page.
        var json = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(json) ? default : System.Text.Json.JsonSerializer.Deserialize<T>(json, ApiJson.Options);
    }

    /// <summary>
    /// Uploads a file and returns either the result or the reason it failed, so the caller can show it next to the file.
    /// The content is rebuilt for the token-refresh retry (a consumed stream would otherwise be sent empty).
    /// </summary>
    public async Task<(UploadResult? Result, ApiProblem? Problem)> UploadAsync(byte[] content, string fileName, string contentType, string purpose)
    {
        HttpRequestMessage Build()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
            form.Add(file, "file", fileName);
            form.Add(new StringContent(purpose), "purpose");
            return new HttpRequestMessage(HttpMethod.Post, "api/media") { Content = form };
        }

        try
        {
            using var response = await ExecuteAsync(Build(), Build);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<UploadResult>(ApiJson.Options), null);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await HandleErrorAsync(response, true);
                return (null, LastProblem);
            }
            LastProblem = await ApiProblem.ReadAsync(response);
            return (null, LastProblem);
        }
        catch (HttpRequestException)
        {
            return (null, LastProblem = new ApiProblem(0, "network", "Network error"));
        }
    }

    public async Task<byte[]?> DownloadAsync(string url)
    {
        using var response = await ExecuteAsync(new HttpRequestMessage(HttpMethod.Get, url), () => new HttpRequestMessage(HttpMethod.Get, url));
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    private async Task<HttpResponseMessage?> SendRaw(HttpMethod method, string url, object? body, bool silent, string? idempotencyKey = null)
    {
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: ApiJson.Options);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            return request;
        }

        try
        {
            var response = await ExecuteAsync(Build(), Build);
            if (response.IsSuccessStatusCode)
            {
                LastProblem = null;
                return response;
            }
            await HandleErrorAsync(response, silent);
            response.Dispose();
            return null;
        }
        catch (HttpRequestException)
        {
            LastProblem = new ApiProblem(0, "offline", l["เชื่อมต่อไม่ได้ กรุณาตรวจสอบอินเทอร์เน็ต", "Cannot connect. Check your internet connection."]);
            if (!silent) snackbar.Add(LastProblem.Message, Severity.Error);
            return null;
        }
    }

    private async Task<HttpResponseMessage> ExecuteAsync(HttpRequestMessage request, Func<HttpRequestMessage> rebuild)
    {
        await AddHeadersAsync(request, false);
        var response = await http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !session.IsAuthenticated) return response;

        response.Dispose();
        var retry = rebuild();
        await AddHeadersAsync(retry, true);
        return await http.SendAsync(retry);
    }

    private async Task AddHeadersAsync(HttpRequestMessage request, bool forceRefresh)
    {
        var token = await session.GetAccessTokenAsync(forceRefresh);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (plant.PlantId is { } plantId) request.Headers.Add("X-Plant-Id", plantId.ToString());
    }

    private async Task HandleErrorAsync(HttpResponseMessage response, bool silent)
    {
        LastProblem = await ApiProblem.ReadAsync(response);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await session.SignOutAsync();
            // Keep the page the user wanted (e.g. a join link) and never nest login inside login.
            var here = "/" + nav.ToBaseRelativePath(nav.Uri);
            if (!here.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
                nav.NavigateTo("/login?returnUrl=" + Uri.EscapeDataString(here));
            return;
        }
        if (!silent) snackbar.Add(LastProblem.Message, Severity.Warning);
    }
}

/// <summary>Breaks the dependency cycle between <see cref="Api"/> and <see cref="PlantContext"/>.</summary>
public sealed class PlantContextAccessor
{
    public Guid? PlantId { get; set; }
}
