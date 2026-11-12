using ShiftSense.Domain;

namespace ShiftSense.Tests.Domain;

public class SeedDataTests
{
    private static readonly IReadOnlyList<Employee> Emps = SeedData.Employees();
    private static readonly IReadOnlyList<Shift> Shifts = SeedData.Shifts(Emps);

    [Fact]
    public void Has_25_employees_two_managers_two_regions()
    {
        Assert.Equal(25, Emps.Count);
        Assert.Equal(2, Emps.Count(e => e.IsManager));
        Assert.Equal(13, Emps.Count(e => e.Region == Region.IN));
        Assert.Equal(12, Emps.Count(e => e.Region == Region.US));
        Assert.Equal(Emps.Count, Emps.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Every_employee_reports_to_a_manager_in_their_region()
    {
        var byId = Emps.ToDictionary(e => e.Id);
        foreach (var e in Emps.Where(e => !e.IsManager))
        {
            var m = byId[e.ManagerId!];
            Assert.True(m.IsManager);
            Assert.Equal(e.Region, m.Region);
            Assert.True(m.Manages(e, id => byId.GetValueOrDefault(id)));
            Assert.False(e.Manages(m, id => byId.GetValueOrDefault(id)));
        }
    }

    [Fact]
    public void Seed_is_deterministic()
    {
        var again = SeedData.Employees();
        Assert.Equal(Emps.Select(e => (e.Id, e.Name, e.HourlyRate)), again.Select(e => (e.Id, e.Name, e.HourlyRate)));
        Assert.Equal(Shifts.Select(s => s.ToString()), SeedData.Shifts(again).Select(s => s.ToString()));
        // Balances must not depend on per-process string hashing (regression: they did).
        var b1 = SeedData.LeaveBalances(Emps).Select(b => (b.EmployeeId, b.Type, b.Days, b.UsedThisYear));
        var b2 = SeedData.LeaveBalances(again).Select(b => (b.EmployeeId, b.Type, b.Days, b.UsedThisYear));
        Assert.Equal(b1, b2);
        Assert.All(SeedData.LeaveBalances(Emps).Where(b => b.Type == LeaveType.Annual), b => Assert.True(b.Days >= 3));
    }

    [Fact]
    public void Seed_shifts_respect_their_own_rules()
    {
        var byId = Emps.ToDictionary(e => e.Id);
        Assert.True(Shifts.Count > 150, $"only {Shifts.Count} shifts");
        Assert.Equal(2, Shifts.Count(s => !s.IsAssigned));
        foreach (var s in Shifts.Where(s => s.IsAssigned))
        {
            var e = byId[s.EmployeeId!];
            Assert.True(e.HasSkills(s.RequiredSkillIds), $"{e.Id} lacks skills for {s}");
            Assert.Equal(e.TeamId, s.TeamId);
            Assert.Equal(8, s.Hours);
        }
        // no employee has two overlapping shifts
        foreach (var group in Shifts.Where(s => s.IsAssigned).GroupBy(s => s.EmployeeId))
        {
            var ordered = group.OrderBy(s => s.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.False(ordered[i].OverlapsWith(ordered[i - 1]), $"{ordered[i]} overlaps {ordered[i - 1]}");
            }
        }
        // weekly hours never exceed the cap
        foreach (var group in Shifts.Where(s => s.IsAssigned).GroupBy(s => (s.EmployeeId, s.IsoWeek)))
        {
            var cap = byId[group.Key.EmployeeId!].MaxWeeklyHours;
            Assert.True(group.Sum(s => s.Hours) <= cap, $"{group.Key} over {cap}h");
        }
    }

    [Fact]
    public void Shift_overlap_is_half_open()
    {
        var a = new Shift { Id = "a", TeamId = "t", Start = new DateTime(2026, 1, 1, 6, 0, 0), End = new DateTime(2026, 1, 1, 14, 0, 0) };
        var b = new Shift { Id = "b", TeamId = "t", Start = new DateTime(2026, 1, 1, 14, 0, 0), End = new DateTime(2026, 1, 1, 22, 0, 0) };
        var c = new Shift { Id = "c", TeamId = "t", Start = new DateTime(2026, 1, 1, 13, 0, 0), End = new DateTime(2026, 1, 1, 21, 0, 0) };
        Assert.False(a.OverlapsWith(b));
        Assert.True(a.OverlapsWith(c));
        Assert.True(c.OverlapsWith(b));
        Assert.Equal(TimeSpan.Zero, b.RestAfter(a));
    }

    [Fact]
    public void Pay_policies_are_region_specific()
    {
        Assert.Equal(2.0m, PayPolicy.For(Region.IN).OvertimeMultiplier);
        Assert.Equal(1.5m, PayPolicy.For(Region.US).OvertimeMultiplier);
        Assert.Equal(48, PayPolicy.India.WeeklyOvertimeThresholdHours);
        Assert.Null(PayPolicy.UnitedStates.DailyOvertimeThresholdHours);
    }
}
