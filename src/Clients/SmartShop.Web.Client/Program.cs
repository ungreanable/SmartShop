using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartShop.UI;
using SmartShop.UI.Services;
using SmartShop.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SmartShopApp>("#app");

// Runtime configuration comes from the host so one image works for every deployment.
using var bootstrap = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var config = await bootstrap.GetFromJsonAsync<AppConfig>("app-config.json", ApiJson.Options) ?? new AppConfig();
if (string.IsNullOrWhiteSpace(config.ApiBaseUrl)) config.ApiBaseUrl = builder.HostEnvironment.BaseAddress;
if (!config.ApiBaseUrl.EndsWith('/')) config.ApiBaseUrl += "/";

builder.Services.AddSmartShopUi(config);
builder.Services.AddScoped<IAppStorage, BrowserStorage>();
builder.Services.AddScoped<ILoginLauncher, WebLoginLauncher>();
builder.Services.AddScoped<IPushRegistrar, WebPushRegistrar>();

await builder.Build().RunAsync();
