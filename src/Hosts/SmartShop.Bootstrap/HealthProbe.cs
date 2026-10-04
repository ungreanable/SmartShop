namespace SmartShop.Bootstrap;

/// <summary>
/// Container health check for chiseled images (no shell, no curl): <c>dotnet SmartShop.Api.dll --healthcheck</c>.
/// </summary>
public static class HealthProbe
{
    public static bool IsRequested(string[] args) => args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health/live"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
