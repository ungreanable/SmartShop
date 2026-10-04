using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace SmartShop.Infrastructure.Modules;

/// <summary>
/// A bounded context. Each module registers its own services, owns a database schema and maps its own endpoints.
/// Modules talk to each other only through <c>SmartShop.Contracts</c> (interfaces and integration events).
/// </summary>
public interface IModule
{
    string Name { get; }

    /// <summary>DbContext types owned by this module; migrated at start-up or by the migrator.</summary>
    IReadOnlyList<Type> DbContexts { get; }

    void Register(IHostApplicationBuilder builder);

    void MapEndpoints(IEndpointRouteBuilder app);
}
