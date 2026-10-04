using SmartShop.Bootstrap;
using SmartShop.Infrastructure.Messaging;

// The worker consumes integration events and scheduled commands from RabbitMQ and runs recurring jobs.
// It exposes only health endpoints; it shares module code with the API.
if (HealthProbe.IsRequested(args)) return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartShop(MessagingRole.Worker);

var app = builder.Build();
app.MapDefaultEndpoints();
await app.RunAsync();
return 0;
