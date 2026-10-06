using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Endpoints;
using SmartShop.Modules.Shops.Services;

namespace SmartShop.Modules.Shops;

public sealed class ShopsModule : IModule
{
    public string Name => "Shops";

    public IReadOnlyList<Type> DbContexts => [typeof(ShopsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<ShopsDbContext>(ShopsDbContext.SchemaName);
        services.AddScoped<IShopDirectory, ShopDirectory>();
        services.AddScoped<MerchantContext>();
        services.AddScoped<ApplicationEndpoints.ReviewCtx>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        ApplicationEndpoints.Map(app);
        ShopEndpoints.Map(app);
        MerchantEndpoints.Map(app);
        ShopBackupEndpoints.Map(app);
        MerchantMemberEndpoints.Map(app);
    }
}
