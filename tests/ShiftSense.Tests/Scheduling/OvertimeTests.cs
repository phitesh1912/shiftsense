using ShiftSense.Domain;
using ShiftSense.Scheduling;
using static ShiftSense.Tests.Scheduling.TestRoster;

namespace ShiftSense.Tests.Scheduling;

public class OvertimeTests
{
    [Fact]
    public void US_pays_1_5x_over_40()
    {
        var shifts = Enumerable.Range(0, 5).Select(d => S($"s{d}", "e", d, 6, hours: 9)).ToList(); // 45h
        var b = OvertimePolicies.For(Region.US).Calculate(shifts, 20m);
        Assert.Equal(45, b.TotalHours);
        Assert.Equal(40, b.RegularHours);
        Assert.Equal(5, b.OvertimeHours);
        Assert.Equal(800m, b.RegularPay);
        Assert.Equal(150m, b.OvertimePay);
        Assert.Equal(950m, b.TotalPay);
        Assert.Equal("USD", b.Currency);
        Assert.Single(b.Notes);
    }

    [Fact]
    public void US_has_no_daily_rule()
    {
        var b = OvertimePolicies.For(Region.US).Calculate([S("s", "e", 0, 6, hours: 12)], 20m);
        Assert.Equal(0, b.OvertimeHours);
        Assert.Empty(b.Notes);
    }

    [Fact]
    public void India_pays_2x_over_9_per_day()
    {
        var b = OvertimePolicies.For(Region.IN).Calculate([S("s", "e", 0, 6, hours: 12)], 250m);
        Assert.Equal(3, b.OvertimeHours);
        Assert.Equal(9 * 250m, b.RegularPay);
        Assert.Equal(3 * 250m * 2, b.OvertimePay);
        Assert.Equal("INR", b.Currency);
    }

    [Fact]
    public void India_weekly_threshold_does_not_double_count_daily_overtime()
    {
        // 6 days x 10h = 60h. Daily OT = 6h. Remaining 54h vs 48 weekly -> 6h. Total OT 12h.
        var shifts = Enumerable.Range(0, 6).Select(d => S($"s{d}", "e", d, 6, hours: 10)).ToList();
        var b = OvertimePolicies.For(Region.IN).Calculate(shifts, 100m);
        Assert.Equal(60, b.TotalHours);
        Assert.Equal(12, b.OvertimeHours);
        Assert.Equal(48, b.RegularHours);
        Assert.Equal(4800m, b.RegularPay);
        Assert.Equal(2400m, b.OvertimePay);
        Assert.Equal(2, b.Notes.Count);
    }

    [Fact]
    public void Strategies_are_selected_by_region()
    {
        Assert.IsType<IndiaOvertimePolicy>(OvertimePolicies.For(Region.IN));
        Assert.IsType<USOvertimePolicy>(OvertimePolicies.For(Region.US));
        Assert.Throws<ArgumentOutOfRangeException>(() => OvertimePolicies.For((Region)99));
    }

    [Fact]
    public void Seed_employees_have_no_overtime_in_week_one()
    {
        var emps = SeedData.Employees();
        var roster = new RosterSnapshot(emps, SeedData.Shifts(emps));
        var week = roster.Shifts.First().IsoWeek;
        foreach (var e in emps.Where(e => !e.IsManager))
        {
            var shifts = roster.ShiftsOf(e.Id).Where(s => s.IsoWeek == week).ToList();
            var b = OvertimePolicies.For(e.Region).Calculate(shifts, e.HourlyRate);
            Assert.Equal(0, b.OvertimeHours);
        }
    }
}
