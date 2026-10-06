using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Catalog;
using SmartShop.Infrastructure.Jobs;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Catalog.Data;
using SmartShop.Modules.Catalog.Endpoints;
using SmartShop.Modules.Catalog.Services;

namespace SmartShop.Modules.Catalog;

public sealed class CatalogModule : IModule
{
    public string Name => "Catalog";

    public IReadOnlyList<Type> DbContexts => [typeof(CatalogDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<CatalogDbContext>(CatalogDbContext.SchemaName);
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<MenuService>();
        services.AddScoped<StockLedger>();
        services.AddScoped<StockAnnouncer>();
        services.AddScoped<MerchantCatalogEndpoints.CatalogCtx>();
        services.AddScoped<IRecurringJob, DailyStockResetJob>();
        services.AddScoped<IRecurringJob, PreOrderRoundCloseJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        CatalogEndpoints.Map(app);
        MerchantCatalogEndpoints.Map(app);
        CatalogBackupEndpoints.Map(app);
    }
}
