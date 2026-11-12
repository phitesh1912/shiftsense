using ShiftSense.Domain;
using ShiftSense.Scheduling;
using static ShiftSense.Tests.Scheduling.TestRoster;

namespace ShiftSense.Tests.Scheduling;

public class SwapCandidateFinderTests
{
    private static readonly Shift Target = S("target", "orig", 2, 14); // Wed 14-22

    [Fact]
    public void Rejects_missing_skills_with_reason()
    {
        var roster = Snapshot([Emp("orig"), Emp("bob", skills: Skills.Forklift)], [Target]);
        var eval = new SwapCandidateFinder(roster).EvaluateAll(Target).Single();
        Assert.False(eval.Eligible);
        Assert.Contains(eval.Violations, v => v.Contains("missing skills: cashier", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_overlap()
    {
        var roster = Snapshot([Emp("orig"), Emp("bob")], [Target, S("b1", "bob", 2, 10)]);
        var eval = new SwapCandidateFinder(roster).EvaluateAll(Target).Single();
        Assert.False(eval.Eligible);
        Assert.Contains("overlaps an existing shift", eval.Violations);
    }

    [Fact]
    public void Rejects_less_than_11h_rest_but_allows_exactly_11h()
    {
        // bob works Thu 06:00; target ends Wed 22:00 -> 8h rest
        var tight = Snapshot([Emp("orig"), Emp("bob")], [Target, S("b1", "bob", 3, 6)]);
        var e1 = new SwapCandidateFinder(tight).EvaluateAll(Target).Single();
        Assert.False(e1.Eligible);
        Assert.Contains(e1.Violations, v => v.Contains("8h rest", StringComparison.Ordinal));

        // bob works Thu 09:00 -> exactly 11h rest: allowed
        var ok = Snapshot([Emp("orig"), Emp("bob")], [Target, S("b1", "bob", 3, 9)]);
        var e2 = new SwapCandidateFinder(ok).EvaluateAll(Target).Single();
        Assert.True(e2.Eligible, string.Join("; ", e2.Violations));
        Assert.Equal(TimeSpan.FromHours(11), e2.MinRest);
    }

    [Fact]
    public void Rejects_weekly_hours_cap()
    {
        // bob already has 5 x 8h = 40h in the week (Mon..Sat except Wed); cap 40.
        // Shifts run 10-18 so the Wed 14-22 target keeps >= 11h rest on both sides and the
        // *only* violation under test is the weekly cap.
        var shifts = new List<Shift> { Target };
        foreach (var d in new[] { 0, 1, 3, 4, 5 })
        {
            shifts.Add(S($"b{d}", "bob", d, 10));
        }
        var roster = Snapshot([Emp("orig"), Emp("bob", maxWeekly: 40)], shifts);
        var eval = new SwapCandidateFinder(roster).EvaluateAll(Target).Single();
        Assert.False(eval.Eligible);
        Assert.Equal(40, eval.WeeklyHoursBefore);
        Assert.Equal(48, eval.WeeklyHoursAfter);
        Assert.Contains(eval.Violations, v => v.Contains("cap 40h", StringComparison.Ordinal));

        // raise the cap and the same roster is fine
        var relaxed = Snapshot([Emp("orig"), Emp("bob", maxWeekly: 48)], shifts);
        Assert.True(new SwapCandidateFinder(relaxed).EvaluateAll(Target).Single().Eligible);
    }

    [Fact]
    public void Rejects_employee_on_approved_leave()
    {
        var roster = Snapshot([Emp("orig"), Emp("bob")], [Target], [Approved("bob", 2)]);
        var eval = new SwapCandidateFinder(roster).EvaluateAll(Target).Single();
        Assert.False(eval.Eligible);
        Assert.Contains(eval.Violations, v => v.StartsWith("on approved leave", StringComparison.Ordinal));
    }

    [Fact]
    public void Excludes_other_teams_managers_and_the_original_holder()
    {
        var mgr = new Employee
        {
            Id = "M1", Name = "m", Email = "m@x", Region = Region.US, Role = Role.Manager, TeamId = "T1",
            JoinDate = new DateOnly(2020, 1, 1), HourlyRate = 40m, MaxWeeklyHours = 40, SkillIds = [Skills.Cashier],
        };
        var roster = Snapshot([Emp("orig"), Emp("bob"), Emp("zed", team: "T2"), mgr], [Target]);
        var ids = new SwapCandidateFinder(roster).EvaluateAll(Target).Select(e => e.Employee.Id).ToList();
        Assert.Equal(["bob"], ids);
    }

    [Fact]
    public void Ranks_fewest_hours_first_then_skill_match_then_id()
    {
        var shifts = new List<Shift> { Target };
        shifts.Add(S("a1", "amy", 0, 6));                 // amy: 8h
        shifts.Add(S("c1", "cal", 0, 6)); shifts.Add(S("c2", "cal", 1, 6)); // cal: 16h
        // ben: 8h, same as amy, but has first-aid as well -> wins skill tie-break only if shift needs it
        shifts.Add(S("b1", "ben", 0, 6));
        var target = S("t", "orig", 3, 14, skills: [Skills.Cashier, Skills.FirstAid]);
        shifts.Add(target);
        var roster = Snapshot(
            [Emp("orig"), Emp("amy"), Emp("ben", skills: [Skills.Cashier, Skills.FirstAid]), Emp("cal", skills: [Skills.Cashier, Skills.FirstAid])],
            shifts);
        var best = new SwapCandidateFinder(roster).FindBest(target, top: 5);
        // amy lacks first-aid -> ineligible; ben (8h) before cal (16h)
        Assert.Equal(["ben", "cal"], best.Select(b => b.Employee.Id));

        var plain = new SwapCandidateFinder(roster).FindBest(Target, top: 5);
        // all eligible for a cashier-only shift: amy & ben at 8h tie on skill match (1) -> id order
        Assert.Equal(["amy", "ben", "cal"], plain.Select(b => b.Employee.Id));
        Assert.Equal(2, new SwapCandidateFinder(roster).FindBest(Target, top: 2).Count);
    }

    [Fact]
    public void Tentative_assignments_are_respected()
    {
        var roster = Snapshot([Emp("orig"), Emp("bob")], [Target]);
        var finder = new SwapCandidateFinder(roster);
        Assert.True(finder.EvaluateAll(Target).Single().Eligible);
        var tentative = new Dictionary<string, IReadOnlyList<Shift>> { ["bob"] = [S("tent", "bob", 2, 10)] };
        Assert.False(finder.EvaluateAll(Target, tentative: tentative).Single().Eligible);
    }

    [Fact]
    public void Seed_roster_has_candidates_for_most_shifts()
    {
        var emps = SeedData.Employees();
        var roster = new RosterSnapshot(emps, SeedData.Shifts(emps), SeedData.LeaveRequests());
        var finder = new SwapCandidateFinder(roster);
        // The 11h rest rule legitimately blocks most cross-slot swaps (an evening shift ends at
        // 22:00, a morning one starts at 06:00), so "most" here means a clear majority of shifts
        // have *someone*, not that everyone is swappable with everyone.
        var withCandidates = roster.Shifts.Count(s => finder.FindBest(s, top: 1).Count > 0);
        Assert.True(withCandidates > roster.Shifts.Count / 2, $"{withCandidates}/{roster.Shifts.Count}");
    }
}
