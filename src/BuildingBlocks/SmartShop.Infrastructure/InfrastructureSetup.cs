using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Caching;
using SmartShop.Infrastructure.Http;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Realtime;
using SmartShop.Infrastructure.Storage;
using SmartShop.Infrastructure.Tenancy;

namespace SmartShop.Infrastructure;

public static class InfrastructureSetup
{
    /// <summary>Cross-cutting services shared by every host (API and worker).</summary>
    public static IHostApplicationBuilder AddSmartShopInfrastructure(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSmartShopDataSource(configuration);
        services.AddSmartShopCaching(configuration);
        services.AddSmartShopStorage(configuration);
        services.AddSmartShopRealtime(configuration);
        services.AddSmartShopAuth(configuration);
        services.AddSmartShopTenancy();
        services.AddSingleton<IMediaUrls, MediaUrls>();

        services.AddProblemDetails();
        services.AddExceptionHandler<SmartShopExceptionHandler>();
        return builder;
    }
}
