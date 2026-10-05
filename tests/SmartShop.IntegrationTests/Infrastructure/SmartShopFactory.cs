using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartShop.Infrastructure.Jobs;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(SmartShop.IntegrationTests.Infrastructure.SmartShopFactory))]

namespace SmartShop.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a disposable PostgreSQL container in <c>Standalone</c> messaging mode,
/// so integration events are handled in-process through Wolverine's durable local queues.
/// </summary>
public sealed class SmartShopFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), "smartshop-tests", Guid.NewGuid().ToString("N"));

    public const string LineChannelSecret = "test-line-channel-secret";

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _ = Server; // boot the host (runs migrations)
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:smartshop", _postgres.GetConnectionString());
        builder.UseSetting("Messaging:Role", "Standalone");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        // The whole suite runs with row-level security on, proving the app works under tenant policies.
        builder.UseSetting("Database:RowLevelSecurity", "true");
        builder.UseSetting("Auth:Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Auth:DevLogin:Enabled", "true");
        builder.UseSetting("Storage:Provider", "FileSystem");
        builder.UseSetting("Storage:FileSystemRoot", _mediaRoot);
        builder.UseSetting("Line:MessagingChannelAccessToken", "");
        builder.UseSetting("Line:MessagingChannelSecret", LineChannelSecret);
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:UserTokensPer10Seconds", "100000");
        builder.UseSetting("Payments:SlipVerifier:Url", "http://slip-verifier.test/verify");
        builder.UseSetting("Payments:SlipVerifier:AutoConfirm", "true");
        builder.UseSetting("Features:Webhooks", "true");
        builder.UseSetting("Integrations:Webhooks:AllowInsecure", "true");
        builder.UseSetting("Integrations:Webhooks:AllowPrivateNetworks", "true");

        builder.ConfigureServices(services =>
        {
            // Recurring jobs are triggered explicitly by tests (RunJobAsync) for determinism.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>(typeof(RecurringJobRunner));
            // The slip verification plug-in talks to a fake service (only slips with a readable QR reach it).
            services.AddHttpClient(nameof(SmartShop.Modules.Payments.Services.ISlipVerifier))
                .ConfigurePrimaryHttpMessageHandler(() => new FakeSlipVerifierHandler());
            services.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(() => new FakeWebhookReceiver());
        });
    }

    public async Task RunJobAsync(string name)
    {
        var runner = ActivatorUtilities.CreateInstance<RecurringJobRunner>(Services);
        await runner.RunOnceAsync(name, CancellationToken.None);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_mediaRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<TService>(this IServiceCollection services, Type implementation) =>
        services.RemoveAll(d => d.ServiceType == typeof(TService) && d.ImplementationType == implementation);

    private static void RemoveAll(this IServiceCollection services, Func<ServiceDescriptor, bool> predicate)
    {
        foreach (var descriptor in services.Where(predicate).ToList())
            services.Remove(descriptor);
    }
}
