using System.Data.Common;
using SmartShop.Contracts.Shops;

namespace SmartShop.Contracts.Catalog;

public enum ItemKind { Product, Service }

/// <summary>
/// Untracked: no counting, manual sold-out only. Tracked: on-hand counter. Daily: counter reset to a quota every day.
/// Slot: time-slot booking with capacity per slot (services).
/// </summary>
public enum StockMode { Untracked, Tracked, Daily, Slot }

public sealed record ModifierOptionInfo(Guid Id, string Name, decimal PriceDelta, bool IsAvailable);

public sealed record ModifierGroupInfo(Guid Id, string Name, int MinSelect, int MaxSelect, IReadOnlyList<ModifierOptionInfo> Options);

public sealed record PreOrderRoundInfo(
    Guid Id, string Title, DateTimeOffset OrderCutoff, DateTimeOffset FulfillFrom, DateTimeOffset FulfillTo,
    string Status, int? MinTotalQuantity);

public sealed record CheckoutItem(
    Guid ItemId, Guid ShopId, string Name, decimal Price, ItemKind Kind, StockMode StockMode,
    bool IsOrderable, string? UnavailableReason, int? Available, int? MaxPerOrder, int? DurationMinutes,
    IReadOnlyList<ModifierGroupInfo> ModifierGroups, IReadOnlyList<FulfillmentType>? AllowedFulfillment,
    PreOrderRoundInfo? Round, Guid? ImageId);

public sealed record ReserveLine(Guid ItemId, int Quantity, DateTimeOffset? SlotStart);

public sealed record ItemListingSnapshot(
    Guid ItemId, Guid ShopId, Guid PlantId, string Name, string? Description, decimal Price, Guid? ImageId,
    bool IsVisible, bool IsSoldOut);

public interface ICatalogService
{
    Task<IReadOnlyList<CheckoutItem>> GetCheckoutItemsAsync(Guid shopId, IReadOnlyCollection<Guid> itemIds, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves stock inside the caller's transaction so an order and its reservation commit together.
    /// Throws a DomainException with code <c>out_of_stock</c> when there is not enough stock.
    /// </summary>
    Task ReserveAsync(DbTransaction transaction, Guid orderId, IReadOnlyList<ReserveLine> lines, Guid actorId, CancellationToken ct = default);

    Task<ItemListingSnapshot?> GetListingSnapshotAsync(Guid itemId, CancellationToken ct = default);
}

public sealed record ItemChanged(Guid PlantId, Guid ShopId, Guid ItemId, bool Deleted) : IntegrationEvent(PlantId);
public sealed record ItemSoldOut(Guid PlantId, Guid ShopId, Guid ItemId) : IntegrationEvent(PlantId);
public sealed record ItemBackInStock(Guid PlantId, Guid ShopId, Guid ItemId) : IntegrationEvent(PlantId);
public sealed record StockLow(Guid PlantId, Guid ShopId, Guid ItemId, string ItemName, int Available) : IntegrationEvent(PlantId);

/// <summary>A pre-order round passed its cut-off. When a group-buy target was not reached its orders must be cancelled.</summary>
public sealed record PreOrderRoundClosed(Guid PlantId, Guid ShopId, Guid RoundId, string Title, bool TargetReached) : IntegrationEvent(PlantId);
