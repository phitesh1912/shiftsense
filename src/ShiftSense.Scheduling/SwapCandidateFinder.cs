using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

public sealed record SchedulingRules(TimeSpan MinRest)
{
    public static readonly SchedulingRules Default = new(TimeSpan.FromHours(11));
}

public sealed record CandidateEvaluation(
    Employee Employee,
    bool Eligible,
    IReadOnlyList<string> Violations,
    double WeeklyHoursBefore,
    double WeeklyHoursAfter,
    int SkillMatch,
    TimeSpan? MinRest);

/// <summary>
/// Finds colleagues who could legally take a shift. Every hard constraint is checked and the
/// reason for rejection recorded, so the agent can explain "why not Ravi?" instead of silently
/// dropping people. Eligible candidates are ranked with a priority queue.
/// </summary>
public sealed class SwapCandidateFinder(RosterSnapshot roster, SchedulingRules? rules = null)
{
    private readonly SchedulingRules _rules = rules ?? SchedulingRules.Default;

    /// <summary>
    /// Evaluate every employee on the shift's team except <paramref name="excludeEmployeeId"/>.
    /// <paramref name="tentative"/> lets the coverage solver overlay assignments it is exploring.
    /// </summary>
    public IReadOnlyList<CandidateEvaluation> EvaluateAll(
        Shift shift,
        string? excludeEmployeeId = null,
        IReadOnlyDictionary<string, IReadOnlyList<Shift>>? tentative = null)
    {
        var results = new List<CandidateEvaluation>();
        foreach (var emp in roster.Employees.Where(e => e.TeamId == shift.TeamId && !e.IsManager))
        {
            if (emp.Id == excludeEmployeeId || emp.Id == shift.EmployeeId)
            {
                continue;
            }
            results.Add(Evaluate(emp, shift, tentative));
        }
        return results;
    }

    /// <summary>Top-<paramref name="top"/> eligible candidates, best first.</summary>
    public IReadOnlyList<CandidateEvaluation> FindBest(
        Shift shift,
        string? excludeEmployeeId = null,
        int top = 5,
        IReadOnlyDictionary<string, IReadOnlyList<Shift>>? tentative = null)
    {
        // Priority: fewest hours already worked that week (fairness + overtime avoidance), then
        // most relevant skills, then id for determinism. ValueTuple ordering gives us a
        // lexicographic comparer for free.
        var queue = new PriorityQueue<CandidateEvaluation, (double Hours, int NegSkills, string Id)>();
        foreach (var c in EvaluateAll(shift, excludeEmployeeId, tentative).Where(c => c.Eligible))
        {
            queue.Enqueue(c, (c.WeeklyHoursBefore, -c.SkillMatch, c.Employee.Id));
        }
        var best = new List<CandidateEvaluation>(Math.Min(top, queue.Count));
        while (best.Count < top && queue.TryDequeue(out var c, out _))
        {
            best.Add(c);
        }
        return best;
    }

    public CandidateEvaluation Evaluate(
        Employee emp,
        Shift shift,
        IReadOnlyDictionary<string, IReadOnlyList<Shift>>? tentative = null)
    {
        var violations = new List<string>();

        var existing = roster.ShiftsOf(emp.Id);
        if (tentative is not null && tentative.TryGetValue(emp.Id, out var extra) && extra.Count > 0)
        {
            existing = existing.Concat(extra).OrderBy(s => s.Start).ToList();
        }

        if (!emp.HasSkills(shift.RequiredSkillIds))
        {
            var missing = shift.RequiredSkillIds.Where(s => !emp.SkillIds.Contains(s));
            violations.Add($"missing skills: {string.Join(", ", missing)}");
        }

        if (roster.IsOnLeave(emp.Id, shift.Date))
        {
            violations.Add($"on approved leave on {shift.Date:yyyy-MM-dd}");
        }

        if (ConflictDetector.WouldOverlap(existing, shift))
        {
            violations.Add("overlaps an existing shift");
        }

        var minRest = ConflictDetector.MinRestAround(existing, shift);
        if (minRest is not null && minRest.Value >= TimeSpan.Zero && minRest.Value < _rules.MinRest)
        {
            violations.Add(
                $"only {minRest.Value.TotalHours:0.#}h rest to an adjacent shift (minimum {_rules.MinRest.TotalHours:0}h)");
        }

        var week = shift.IsoWeek;
        var before = existing.Where(s => s.IsoWeek == week && s.Id != shift.Id).Sum(s => s.Hours);
        var after = before + shift.Hours;
        if (after > emp.MaxWeeklyHours)
        {
            violations.Add($"would reach {after:0.#}h in week {week.Week} (cap {emp.MaxWeeklyHours}h)");
        }

        var skillMatch = shift.RequiredSkillIds.Count(emp.SkillIds.Contains);
        return new CandidateEvaluation(emp, violations.Count == 0, violations, before, after, skillMatch, minRest);
    }
}
