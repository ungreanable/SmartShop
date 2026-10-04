using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Plants;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Plants.Data;
using SmartShop.Modules.Plants.Endpoints;
using SmartShop.Modules.Plants.Services;

namespace SmartShop.Modules.Plants;

public sealed class PlantsModule : IModule
{
    public string Name => "Plants";

    public IReadOnlyList<Type> DbContexts => [typeof(PlantsDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<PlantsDbContext>(PlantsDbContext.SchemaName);
        services.AddScoped<IPlantDirectory, PlantDirectory>();
        services.AddScoped<MembershipCache>();
        services.AddScoped<PlantAdminEndpoints.Ctx>();
        services.AddScoped<IPersonalDataContributor, PlantsPersonalData>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        PlantEndpoints.Map(app);
        PlantAdminEndpoints.Map(app);
    }
}
