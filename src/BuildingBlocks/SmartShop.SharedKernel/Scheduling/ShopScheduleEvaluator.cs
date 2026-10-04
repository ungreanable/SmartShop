namespace SmartShop.SharedKernel.Scheduling;

/// <summary>
/// Pure functions that evaluate a shop's effective status.
/// Precedence: Suspended, Vacation, Closure period, Manual override, Weekly schedule; Busy is layered on top of "open".
/// </summary>
public static class ShopScheduleEvaluator
{
    private const int LookAheadDays = 8;

    public static ShopStatus Evaluate(ShopScheduleSnapshot s, DateTimeOffset now)
    {
        if (s.Suspended)
            return new(ShopOpenState.Closed, ShopClosedReason.Suspended, null, null);

        if (s.VacationUntil is { } vacation && vacation > now)
        {
            var indefinite = vacation == DateTimeOffset.MaxValue;
            return new(ShopOpenState.Closed, ShopClosedReason.Vacation, indefinite ? null : vacation,
                indefinite ? null : NextOpen(s, vacation));
        }

        var closure = s.Closures.FirstOrDefault(c => c.Start <= now && now < c.End);
        if (closure is not null)
            return new(ShopOpenState.Closed, ShopClosedReason.TemporaryClosed, closure.End, NextOpen(s, closure.End));

        if (s.OverrideMode != OverrideMode.None && (s.OverrideUntil is null || s.OverrideUntil > now))
        {
            if (s.OverrideMode == OverrideMode.ForceClosed)
                return new(ShopOpenState.Closed, ShopClosedReason.Manual, s.OverrideUntil,
                    s.OverrideUntil is { } u ? NextOpen(s, u) : null);
            return WithBusy(s, now, s.OverrideUntil);
        }

        var current = OpenIntervals(s, now.AddDays(-1), now.AddDays(1)).FirstOrDefault(w => w.Start <= now && now < w.End);
        if (current is null)
            return new(ShopOpenState.Closed, ShopClosedReason.OutsideHours, null, NextOpen(s, now));

        return WithBusy(s, now, current.End);
    }

    private static ShopStatus WithBusy(ShopScheduleSnapshot s, DateTimeOffset now, DateTimeOffset? until)
    {
        if (s.BusyUntil is { } busy && busy > now)
        {
            var busyUntil = until is { } u && u < busy ? u : busy;
            return new(ShopOpenState.Busy, ShopClosedReason.None, busyUntil, null);
        }
        return new(ShopOpenState.Open, ShopClosedReason.None, until, null);
    }

    /// <summary>Next moment (at or after <paramref name="from"/>) where the weekly schedule is open and no closure applies.</summary>
    public static DateTimeOffset? NextOpen(ShopScheduleSnapshot s, DateTimeOffset from)
    {
        foreach (var w in OpenIntervals(s, from.AddDays(-1), from.AddDays(LookAheadDays)))
        {
            if (w.End <= from) continue;
            var start = w.Start < from ? from : w.Start;
            var blocked = s.Closures.FirstOrDefault(c => c.Start <= start && start < c.End);
            if (blocked is null) return start;
            if (blocked.End < w.End) return blocked.End;
        }
        return null;
    }

    /// <summary>Earliest future instant at which the evaluated status may change. Used to schedule re-evaluation.</summary>
    public static DateTimeOffset? NextBoundary(ShopScheduleSnapshot s, DateTimeOffset now)
    {
        var candidates = new List<DateTimeOffset>();
        void Add(DateTimeOffset? t)
        {
            if (t is { } v && v > now && v != DateTimeOffset.MaxValue) candidates.Add(v);
        }

        Add(s.OverrideUntil);
        Add(s.BusyUntil);
        Add(s.VacationUntil);
        foreach (var c in s.Closures) { Add(c.Start); Add(c.End); }
        foreach (var w in OpenIntervals(s, now.AddDays(-1), now.AddDays(LookAheadDays))) { Add(w.Start); Add(w.End); }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    /// <summary>Concrete open intervals of the weekly schedule between two instants (closures not applied).</summary>
    public static IReadOnlyList<TimeWindow> OpenIntervals(ShopScheduleSnapshot s, DateTimeOffset from, DateTimeOffset to)
    {
        var tz = ResolveTimeZone(s.TimeZoneId);
        var localFrom = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, tz).DateTime).AddDays(-1);
        var localTo = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, tz).DateTime);
        var result = new List<TimeWindow>();

        for (var day = localFrom; day <= localTo; day = day.AddDays(1))
        {
            foreach (var slot in s.Hours.Where(h => h.Day == day.DayOfWeek))
            {
                var start = ToInstant(day, slot.OpenAt, tz);
                var endDay = slot.CloseAt <= slot.OpenAt ? day.AddDays(1) : day;
                var end = ToInstant(endDay, slot.CloseAt, tz);
                if (end > from && start < to) result.Add(new TimeWindow(start, end));
            }
        }
        return Merge(result);
    }

    /// <summary>
    /// Selectable pickup/delivery windows for checkout: fixed-size windows inside open hours, skipping closures,
    /// ending after now + preparation time.
    /// </summary>
    public static IReadOnlyList<TimeWindow> SelectableWindows(
        ShopScheduleSnapshot s, DateTimeOffset now, int days, int intervalMinutes, int prepMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 10, 240);
        var earliest = now.AddMinutes(prepMinutes);
        var result = new List<TimeWindow>();
        if (s.Suspended) return result;

        foreach (var open in OpenIntervals(s, now, now.AddDays(days)))
        {
            for (var start = open.Start; start.AddMinutes(intervalMinutes) <= open.End; start = start.AddMinutes(intervalMinutes))
            {
                var end = start.AddMinutes(intervalMinutes);
                if (end <= earliest) continue;
                if (s.VacationUntil is { } v && start < v) continue;
                if (s.Closures.Any(c => start < c.End && end > c.Start)) continue;
                result.Add(new TimeWindow(start, end));
            }
        }
        return result;
    }

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"); }
    }

    private static DateTimeOffset ToInstant(DateOnly day, TimeOnly time, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static List<TimeWindow> Merge(List<TimeWindow> windows)
    {
        var merged = new List<TimeWindow>();
        foreach (var w in windows.OrderBy(w => w.Start))
        {
            if (merged.Count > 0 && w.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { End = w.End > last.End ? w.End : last.End };
            }
            else merged.Add(w);
        }
        return merged;
    }
}
