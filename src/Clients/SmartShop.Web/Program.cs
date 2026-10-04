// Hosts the Blazor WebAssembly PWA (works in browsers and inside LINE via LIFF) and proxies /api and /hubs
// to the API so the app is same-origin (no CORS) even without Caddy in front.
if (args.Contains("--healthcheck"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await http.GetAsync(new Uri("http://127.0.0.1:8080/health/live"))).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
    catch (TaskCanceledException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

app.MapDefaultEndpoints();

// Public, non-secret runtime settings for the client.
app.MapGet("/app-config.json", (IConfiguration config, HttpResponse response) =>
{
    response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        apiBaseUrl = config["Client:ApiBaseUrl"] ?? "",
        lineChannelId = config["Auth:Line:ChannelId"] ?? "",
        liffId = config["Auth:Line:LiffId"] ?? "",
        lineAddFriendUrl = config["Line:AddFriendUrl"],
        devLoginEnabled = config.GetValue("Auth:DevLogin:Enabled", false) && !app.Environment.IsProduction(),
        appName = config["Client:AppName"] ?? "SmartShop",
        platform = "web",
    });
});

app.UseBlazorFrameworkFiles();
// css/js/html are not fingerprinted: make browsers revalidate (cheap 304 via ETag) so updates show up right away.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (!ctx.Context.Request.Path.StartsWithSegments("/_framework"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.MapReverseProxy();
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;
