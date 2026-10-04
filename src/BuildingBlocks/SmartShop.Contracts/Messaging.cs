using SmartShop.SharedKernel;

namespace SmartShop.Contracts;

/// <summary>
/// Marker for any message that crosses process boundaries (published to the broker).
/// Integration events and scheduled commands both implement it.
/// </summary>
public interface IAsyncMessage;

/// <summary>A fact that happened in one module and that other modules may react to.</summary>
public interface IIntegrationEvent : IAsyncMessage
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    Guid PlantId { get; }
}

/// <summary>
/// Base record for integration events. Derived records pass <c>PlantId</c> through the primary constructor:
/// <code>public sealed record OrderPlaced(Guid PlantId, Guid OrderId) : IntegrationEvent(PlantId);</code>
/// </summary>
public abstract record IntegrationEvent(Guid PlantId) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Ids.New();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Command delivered later (scheduled) or asynchronously to the worker.</summary>
public interface IScheduledCommand : IAsyncMessage;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
