using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

public sealed record CoverageResult(
    IReadOnlyDictionary<string, string> Assignments,
    IReadOnlyList<string> UnfilledShiftIds,
    string Strategy,
    int NodesExplored)
{
    public bool Complete => UnfilledShiftIds.Count == 0;
}

/// <summary>
/// Assigns a set of open shifts to employees so that every assignment satisfies the swap
/// constraints *including the other assignments made in the same solve* (an employee can't take
/// two open shifts 2 hours apart just because each looked fine alone).
///
/// Strategy: depth-first backtracking over shifts ordered by fewest candidates first (most
/// constrained variable), trying candidates in priority order (least-hours-first value ordering),
/// with forward checking — if any remaining shift has no eligible candidate under the current
/// partial assignment, prune. A node budget bounds the worst case; if it is exhausted or no
/// complete assignment exists, fall back to greedy best-effort so the manager still gets the
/// largest partial fill we found.
/// </summary>
public sealed class CoverageSolver(RosterSnapshot roster, SchedulingRules? rules = null)
{
    private readonly SwapCandidateFinder _finder = new(roster, rules);

    public CoverageResult Solve(IReadOnlyList<Shift> openShifts, int maxNodes = 5_000)
    {
        if (openShifts.Count == 0)
        {
            return new CoverageResult(new Dictionary<string, string>(), [], "none", 0);
        }

        var state = new SearchState(maxNodes);
        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
        var tentative = new Dictionary<string, List<Shift>>(StringComparer.Ordinal);

        var complete = Backtrack(openShifts.ToList(), assignment, tentative, state);
        if (complete)
        {
            return new CoverageResult(assignment, [], "backtracking", state.Nodes);
        }

        // Fallback: greedy in the same most-constrained-first order.
        var greedy = Greedy(openShifts);
        return greedy with { NodesExplored = state.Nodes + greedy.NodesExplored };
    }

    private bool Backtrack(
        List<Shift> remaining,
        Dictionary<string, string> assignment,
        Dictionary<string, List<Shift>> tentative,
        SearchState state)
    {
        if (remaining.Count == 0)
        {
            return true;
        }
        if (state.Nodes >= state.MaxNodes)
        {
            return false;
        }
        state.Nodes++;

        // Forward checking + MRV: compute candidates for every remaining shift; bail if any has
        // none; recurse into the one with the fewest.
        var view = View(tentative);
        Shift? pick = null;
        IReadOnlyList<CandidateEvaluation>? pickCandidates = null;
        foreach (var shift in remaining)
        {
            var cands = _finder.FindBest(shift, top: int.MaxValue, tentative: view);
            if (cands.Count == 0)
            {
                return false;
            }
            if (pickCandidates is null || cands.Count < pickCandidates.Count)
            {
                pick = shift;
                pickCandidates = cands;
            }
        }

        var rest = remaining.Where(s => s.Id != pick!.Id).ToList();
        foreach (var cand in pickCandidates!)
        {
            var emp = cand.Employee.Id;
            assignment[pick!.Id] = emp;
            var copy = Clone(pick, emp);
            if (!tentative.TryGetValue(emp, out var list))
            {
                tentative[emp] = list = [];
            }
            list.Add(copy);

            if (Backtrack(rest, assignment, tentative, state))
            {
                return true;
            }

            list.Remove(copy);
            assignment.Remove(pick.Id);
            if (state.Nodes >= state.MaxNodes)
            {
                return false;
            }
        }
        return false;
    }

    private CoverageResult Greedy(IReadOnlyList<Shift> openShifts)
    {
        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
        var tentative = new Dictionary<string, List<Shift>>(StringComparer.Ordinal);
        var unfilled = new List<string>();
        var nodes = 0;

        // Most constrained first, measured against the untouched roster.
        var ordered = openShifts
            .Select(s => (Shift: s, N: _finder.FindBest(s, top: int.MaxValue).Count))
            .OrderBy(t => t.N)
            .ThenBy(t => t.Shift.Start)
            .Select(t => t.Shift);

        foreach (var shift in ordered)
        {
            nodes++;
            var best = _finder.FindBest(shift, top: 1, tentative: View(tentative));
            if (best.Count == 0)
            {
                unfilled.Add(shift.Id);
                continue;
            }
            var emp = best[0].Employee.Id;
            assignment[shift.Id] = emp;
            if (!tentative.TryGetValue(emp, out var list))
            {
                tentative[emp] = list = [];
            }
            list.Add(Clone(shift, emp));
        }
        return new CoverageResult(assignment, unfilled, "greedy", nodes);
    }

    private static Shift Clone(Shift s, string employeeId) => new()
    {
        Id = s.Id,
        TeamId = s.TeamId,
        EmployeeId = employeeId,
        Start = s.Start,
        End = s.End,
        RequiredSkillIds = s.RequiredSkillIds,
    };

    private static Dictionary<string, IReadOnlyList<Shift>> View(Dictionary<string, List<Shift>> tentative) =>
        tentative.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Shift>)kv.Value, StringComparer.Ordinal);

    private sealed class SearchState(int maxNodes)
    {
        public int MaxNodes { get; } = maxNodes;
        public int Nodes { get; set; }
    }
}
