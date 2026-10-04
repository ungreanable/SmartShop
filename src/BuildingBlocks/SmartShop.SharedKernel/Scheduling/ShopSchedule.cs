namespace SmartShop.SharedKernel.Scheduling;

public enum ShopOpenState { Open, Busy, Closed }

public enum ShopClosedReason { None, Suspended, Vacation, TemporaryClosed, Manual, OutsideHours }

public enum OverrideMode { None, ForceOpen, ForceClosed }

/// <summary>A weekly opening slot. When <see cref="CloseAt"/> is not after <see cref="OpenAt"/> the slot runs past midnight.</summary>
public sealed record OpeningSlot(DayOfWeek Day, TimeOnly OpenAt, TimeOnly CloseAt);

public sealed record ClosureWindow(DateTimeOffset Start, DateTimeOffset End, string? Reason);

/// <summary>Everything needed to compute whether a shop is open, independent of storage.</summary>
public sealed record ShopScheduleSnapshot(
    string TimeZoneId,
    IReadOnlyList<OpeningSlot> Hours,
    IReadOnlyList<ClosureWindow> Closures,
    OverrideMode OverrideMode = OverrideMode.None,
    DateTimeOffset? OverrideUntil = null,
    DateTimeOffset? BusyUntil = null,
    bool Suspended = false,
    DateTimeOffset? VacationUntil = null);

public sealed record ShopStatus(
    ShopOpenState State,
    ShopClosedReason Reason,
    DateTimeOffset? Until,
    DateTimeOffset? NextOpenAt)
{
    public bool IsOpen => State is ShopOpenState.Open or ShopOpenState.Busy;
    public bool AcceptingOrders => State == ShopOpenState.Open;
}

public sealed record TimeWindow(DateTimeOffset Start, DateTimeOffset End);
