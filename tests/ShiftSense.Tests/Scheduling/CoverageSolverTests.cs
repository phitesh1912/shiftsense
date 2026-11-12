using ShiftSense.Domain;
using ShiftSense.Scheduling;
using static ShiftSense.Tests.Scheduling.TestRoster;

namespace ShiftSense.Tests.Scheduling;

public class CoverageSolverTests
{
    [Fact]
    public void Empty_input_is_trivially_complete()
    {
        var r = new CoverageSolver(Snapshot([Emp("a")], [])).Solve([]);
        Assert.True(r.Complete);
        Assert.Equal("none", r.Strategy);
    }

    [Fact]
    public void Backtracking_is_needed_when_greedy_choice_blocks_a_later_shift()
    {
        // Two open shifts on Wed: X 06-14 (cashier) and Y 14-22 (cashier + first-aid).
        // amy: cashier+first-aid, 0h this week. bob: cashier only, 0h.
        // Greedy by fewest-hours then id picks amy for X (tie on hours, amy < bob by id),
        // leaving nobody with first-aid for Y. Backtracking must give X to bob and Y to amy.
        var x = S("X", null, 2, 6);
        var y = S("Y", null, 2, 14, skills: [Skills.Cashier, Skills.FirstAid]);
        var roster = Snapshot([Emp("amy", skills: [Skills.Cashier, Skills.FirstAid]), Emp("bob")], [x, y]);

        var r = new CoverageSolver(roster).Solve([x, y]);
        Assert.True(r.Complete, string.Join(",", r.UnfilledShiftIds));
        Assert.Equal("backtracking", r.Strategy);
        Assert.Equal("bob", r.Assignments["X"]);
        Assert.Equal("amy", r.Assignments["Y"]);
    }

    [Fact]
    public void Same_employee_cannot_take_two_shifts_that_conflict_with_each_other()
    {
        var x = S("X", null, 2, 6);   // 06-14
        var y = S("Y", null, 2, 16);  // 16-00 -> only 2h after X
        var roster = Snapshot([Emp("solo")], [x, y]);
        var r = new CoverageSolver(roster).Solve([x, y]);
        Assert.False(r.Complete);
        Assert.Equal("greedy", r.Strategy);
        Assert.Single(r.Assignments);
        Assert.Single(r.UnfilledShiftIds);
    }

    [Fact]
    public void Greedy_fallback_reports_unfilled_when_no_complete_assignment_exists()
    {
        var x = S("X", null, 2, 6, skills: [Skills.Pharmacy]);
        var y = S("Y", null, 3, 6);
        var roster = Snapshot([Emp("bob")], [x, y]);
        var r = new CoverageSolver(roster).Solve([x, y]);
        Assert.Equal("greedy", r.Strategy);
        Assert.Equal(["X"], r.UnfilledShiftIds);
        Assert.Equal("bob", r.Assignments["Y"]);
    }

    [Fact]
    public void Node_budget_bounds_the_search()
    {
        // 6 open shifts across 6 days, 6 interchangeable employees -> huge tree; budget 3 nodes.
        var shifts = Enumerable.Range(0, 6).Select(d => S($"S{d}", null, d, 6)).ToList();
        var emps = Enumerable.Range(0, 6).Select(i => Emp($"e{i}")).ToList();
        var r = new CoverageSolver(Snapshot(emps, shifts)).Solve(shifts, maxNodes: 3);
        Assert.True(r.Complete); // greedy fills it
        Assert.Equal("greedy", r.Strategy);
        Assert.True(r.NodesExplored <= 3 + 6);
    }

    [Fact]
    public void Solves_the_seed_rosters_open_shifts()
    {
        var emps = SeedData.Employees();
        var roster = new RosterSnapshot(emps, SeedData.Shifts(emps), SeedData.LeaveRequests());
        var open = roster.OpenShifts().ToList();
        Assert.Equal(2, open.Count);
        var r = new CoverageSolver(roster).Solve(open);
        Assert.True(r.Complete, string.Join(",", r.UnfilledShiftIds));
        foreach (var (shiftId, empId) in r.Assignments)
        {
            var e = roster.Employee(empId)!;
            var s = roster.Shift(shiftId)!;
            Assert.Equal(e.TeamId, s.TeamId);
            Assert.True(e.HasSkills(s.RequiredSkillIds));
        }
    }
}
