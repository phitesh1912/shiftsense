using ShiftSense.Domain;
using ShiftSense.Scheduling;

namespace ShiftSense.Tests.Scheduling;

public class LeaveAccrualTests
{
    private static readonly DateOnly YearStart = new(2026, 4, 1); // India leave year

    [Fact]
    public void Full_months_accrue_at_policy_rate()
    {
        // joined long ago; Apr..Oct complete as of 1 Nov -> 7 months x 1.5
        var d = LeaveAccrualEngine.AccruedDays(PayPolicy.India, new DateOnly(2020, 1, 1), new DateOnly(2026, 11, 1), YearStart);
        Assert.Equal(10.5m, d);
    }

    [Fact]
    public void Incomplete_month_is_not_credited_yet()
    {
        var d = LeaveAccrualEngine.AccruedDays(PayPolicy.India, new DateOnly(2020, 1, 1), new DateOnly(2026, 11, 30), YearStart);
        Assert.Equal(10.5m, d); // November not complete until 1 Dec
        var e = LeaveAccrualEngine.AccruedDays(PayPolicy.India, new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 1), YearStart);
        Assert.Equal(12m, e);
    }

    [Fact]
    public void Joining_mid_month_is_pro_rated()
    {
        // joined 16 Sep (15 of 30 days worked) -> Sep credit 0.75; Oct full 1.5; as of 1 Nov
        var d = LeaveAccrualEngine.AccruedDays(PayPolicy.India, new DateOnly(2026, 9, 16), new DateOnly(2026, 11, 1), YearStart);
        Assert.Equal(2.25m, d);
    }

    [Fact]
    public void Nothing_before_join_or_before_year_start()
    {
        Assert.Equal(0m, LeaveAccrualEngine.AccruedDays(PayPolicy.India, new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 1), YearStart));
        // joined before year start: counts from year start only
        var d = LeaveAccrualEngine.AccruedDays(PayPolicy.UnitedStates, new DateOnly(2025, 6, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 1, 1));
        Assert.Equal(2.5m, d); // Jan + Feb at 1.25
    }

    [Fact]
    public void Carry_forward_is_capped_and_never_negative()
    {
        Assert.Equal(45m, LeaveAccrualEngine.CarryForward(PayPolicy.India, 60m));
        Assert.Equal(12m, LeaveAccrualEngine.CarryForward(PayPolicy.India, 12m));
        Assert.Equal(5m, LeaveAccrualEngine.CarryForward(PayPolicy.UnitedStates, 9m));
        Assert.Equal(0m, LeaveAccrualEngine.CarryForward(PayPolicy.UnitedStates, -3m));
    }

    [Fact]
    public void Working_days_skip_weekends()
    {
        // Fri 20 Nov .. Tue 24 Nov 2026 = Fri, Mon, Tue
        Assert.Equal(3, LeaveAccrualEngine.WorkingDays(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 24)));
        Assert.Equal(0, LeaveAccrualEngine.WorkingDays(new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 22)));
        Assert.Equal(0, LeaveAccrualEngine.WorkingDays(new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 20)));
    }
}
