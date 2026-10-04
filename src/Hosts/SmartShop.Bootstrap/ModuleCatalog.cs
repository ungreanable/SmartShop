using SmartShop.Infrastructure.Modules;

namespace SmartShop.Bootstrap;

/// <summary>The single list of modules composed into every host.</summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All() =>
    [
        new Modules.Identity.IdentityModule(),
    ];
}
