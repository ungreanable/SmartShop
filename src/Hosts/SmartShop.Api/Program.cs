using SmartShop.Bootstrap;
using SmartShop.Infrastructure.Messaging;

if (HealthProbe.IsRequested(args)) return await HealthProbe.RunAsync();
if (SmartShopHost.TryRunTool(args)) return 0;

var builder = WebApplication.CreateBuilder(args);

// "Api" publishes events to RabbitMQ for the worker; "Standalone" handles them in-process (no broker needed).
var role = builder.Configuration.GetValue("Messaging:Role", MessagingRole.Api);
builder.AddSmartShop(role);

var app = builder.Build();
app.MapSmartShop();
await app.RunSmartShopAsync(args);
return 0;

public partial class Program;
