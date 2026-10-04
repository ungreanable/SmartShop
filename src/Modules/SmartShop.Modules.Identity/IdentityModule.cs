using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartShop.Contracts.Identity;
using SmartShop.Infrastructure.Modules;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Privacy;
using SmartShop.Modules.Identity.Data;
using SmartShop.Modules.Identity.Endpoints;
using SmartShop.Modules.Identity.Services;

namespace SmartShop.Modules.Identity;

public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public IReadOnlyList<Type> DbContexts => [typeof(IdentityDbContext)];

    public void Register(IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
        services.Configure<LineLoginOptions>(builder.Configuration.GetSection(LineLoginOptions.Section));
        services.AddHttpClient<ILineLoginClient, LineLoginClient>();
        services.AddScoped<TokenService>();
        services.AddScoped<SignInService>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IPersonalDataContributor, IdentityPersonalData>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app);
    }
}
