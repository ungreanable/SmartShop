using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>Controllable clock. Starts at real "now"; tests may advance it.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

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
        builder.UseSetting("Auth:Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Auth:DevLogin:Enabled", "true");
        builder.UseSetting("Storage:Provider", "FileSystem");
        builder.UseSetting("Storage:FileSystemRoot", _mediaRoot);
        builder.UseSetting("Line:MessagingChannelAccessToken", "");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            // Recurring jobs are triggered explicitly by tests (RunJobAsync) for determinism.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>(typeof(RecurringJobRunner));
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
