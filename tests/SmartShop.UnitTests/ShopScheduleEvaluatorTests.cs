using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.UnitTests;

public class ShopScheduleEvaluatorTests
{
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    // 2026-10-05 is a Monday.
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, Bangkok);

    private static ShopScheduleSnapshot Weekdays(params (TimeOnly Open, TimeOnly Close)[] slots) => new(
        "Asia/Bangkok",
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .SelectMany(d => slots.Select(s => new OpeningSlot(d, s.Open, s.Close)))],
        []);

    private static readonly ShopScheduleSnapshot MorningAndEvening =
        Weekdays((new TimeOnly(6, 0), new TimeOnly(9, 0)), (new TimeOnly(16, 0), new TimeOnly(19, 0)));

    [Fact]
    public void Open_inside_a_slot_until_its_close()
    {
        var status = ShopScheduleEvaluator.Evaluate(MorningAndEvening, At(5, 7));

        status.State.ShouldBe(ShopOpenState.Open);
        status.AcceptingOrders.ShouldBeTrue();
        status.Until.ShouldBe(At(5, 9));
    }

    [Fact]
    public void Closed_between_slots_reports_next_opening()
    {
        var status = ShopScheduleEvaluator.Evaluate(MorningAndEvening, At(5, 12));

        status.State.ShouldBe(ShopOpenState.Closed);
        status.Reason.ShouldBe(ShopClosedReason.OutsideHours);
        status.NextOpenAt.ShouldBe(At(5, 16));
    }

    [Fact]
    public void Weekend_next_opening_is_monday_morning()
    {
        var status = ShopScheduleEvaluator.Evaluate(MorningAndEvening, At(10, 10)); // Saturday

        status.NextOpenAt.ShouldBe(At(12, 6));
    }

    [Fact]
    public void Overnight_slot_spans_midnight()
    {
        var lateNight = Weekdays((new TimeOnly(22, 0), new TimeOnly(2, 0)));

        ShopScheduleEvaluator.Evaluate(lateNight, At(6, 1)).State.ShouldBe(ShopOpenState.Open); // Tuesday 01:00 from Monday's slot
        ShopScheduleEvaluator.Evaluate(lateNight, At(6, 3)).State.ShouldBe(ShopOpenState.Closed);
    }

    [Fact]
    public void Manual_force_open_overrides_schedule_until_given_time()
    {
        var s = MorningAndEvening with { OverrideMode = OverrideMode.ForceOpen, OverrideUntil = At(5, 14) };

        ShopScheduleEvaluator.Evaluate(s, At(5, 12)).State.ShouldBe(ShopOpenState.Open);
        ShopScheduleEvaluator.Evaluate(s, At(5, 15)).State.ShouldBe(ShopOpenState.Closed);
    }

    [Fact]
    public void Manual_force_closed_without_end_stays_closed()
    {
        var s = MorningAndEvening with { OverrideMode = OverrideMode.ForceClosed };

        var status = ShopScheduleEvaluator.Evaluate(s, At(5, 7));
        status.Reason.ShouldBe(ShopClosedReason.Manual);
        status.Until.ShouldBeNull();
    }

    [Fact]
    public void Busy_mode_is_open_but_not_accepting_orders()
    {
        var s = MorningAndEvening with { BusyUntil = At(5, 7, 30) };

        var status = ShopScheduleEvaluator.Evaluate(s, At(5, 7));
        status.State.ShouldBe(ShopOpenState.Busy);
        status.IsOpen.ShouldBeTrue();
        status.AcceptingOrders.ShouldBeFalse();
        status.Until.ShouldBe(At(5, 7, 30));
    }

    [Fact]
    public void Closure_period_beats_manual_override()
    {
        var s = MorningAndEvening with
        {
            OverrideMode = OverrideMode.ForceOpen,
            Closures = [new ClosureWindow(At(5, 0), At(7, 0), "trip")],
        };

        var status = ShopScheduleEvaluator.Evaluate(s, At(5, 7));
        status.Reason.ShouldBe(ShopClosedReason.TemporaryClosed);
        status.NextOpenAt.ShouldBe(At(7, 6));
    }

    [Fact]
    public void Suspended_beats_everything()
    {
        var s = MorningAndEvening with { Suspended = true, OverrideMode = OverrideMode.ForceOpen };

        ShopScheduleEvaluator.Evaluate(s, At(5, 7)).Reason.ShouldBe(ShopClosedReason.Suspended);
    }

    [Fact]
    public void Indefinite_vacation_has_no_next_opening()
    {
        var s = MorningAndEvening with { VacationUntil = DateTimeOffset.MaxValue };

        var status = ShopScheduleEvaluator.Evaluate(s, At(5, 7));
        status.Reason.ShouldBe(ShopClosedReason.Vacation);
        status.NextOpenAt.ShouldBeNull();
    }

    [Fact]
    public void Next_boundary_is_the_nearest_change()
    {
        ShopScheduleEvaluator.NextBoundary(MorningAndEvening, At(5, 7)).ShouldBe(At(5, 9));
        ShopScheduleEvaluator.NextBoundary(MorningAndEvening with { BusyUntil = At(5, 7, 15) }, At(5, 7)).ShouldBe(At(5, 7, 15));
    }

    [Fact]
    public void Selectable_windows_respect_prep_time_and_closures()
    {
        var s = MorningAndEvening with { Closures = [new ClosureWindow(At(5, 16), At(5, 17), null)] };

        var windows = ShopScheduleEvaluator.SelectableWindows(s, At(5, 7, 50), days: 1, intervalMinutes: 30, prepMinutes: 20);

        windows[0].Start.ShouldBe(At(5, 8));
        windows.ShouldNotContain(w => w.Start == At(5, 16) || w.Start == At(5, 16, 30));
        windows.ShouldContain(w => w.Start == At(5, 17));
    }
}
