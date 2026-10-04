using SmartShop.Infrastructure.Modules;

namespace SmartShop.Bootstrap;

/// <summary>The single list of modules composed into every host.</summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All() =>
    [
        new Modules.Identity.IdentityModule(),
        new Modules.Media.MediaModule(),
        new Modules.Plants.PlantsModule(),
        new Modules.Shops.ShopsModule(),
        new Modules.Catalog.CatalogModule(),
        new Modules.Ordering.OrderingModule(),
        new Modules.Payments.PaymentsModule(),
        new Modules.Notifications.NotificationsModule(),
        new Modules.Search.SearchModule(),
        new Modules.Reviews.ReviewsModule(),
        new Modules.Promotions.PromotionsModule(),
        new Modules.Audit.AuditModule(),
        new Modules.Integrations.IntegrationsModule(),
    ];
}
