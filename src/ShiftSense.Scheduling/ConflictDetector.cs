using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

/// <summary>Interval conflict detection over sorted shifts.</summary>
public static class ConflictDetector
{
    /// <summary>
    /// All pairs of overlapping shifts. Sort by start, then each shift only needs to be compared
    /// with the running "latest end" window: O(n log n) sort + O(n + k) scan for k overlaps.
    /// </summary>
    public static IReadOnlyList<(Shift A, Shift B)> FindOverlaps(IEnumerable<Shift> shifts)
    {
        var sorted = shifts.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        var result = new List<(Shift, Shift)>();
        // Active window: shifts whose End is after the current Start. Kept small because shifts
        // are short relative to the roster; a balanced tree would be the next step at scale.
        var active = new List<Shift>();
        foreach (var s in sorted)
        {
            active.RemoveAll(a => a.End <= s.Start);
            foreach (var a in active)
            {
                result.Add((a, s));
            }
            active.Add(s);
        }
        return result;
    }

    /// <summary>
    /// Would <paramref name="candidate"/> overlap any shift in <paramref name="sortedExisting"/>?
    /// Binary-searches for the insertion point and checks only the neighbours: O(log n).
    /// </summary>
    public static bool WouldOverlap(IReadOnlyList<Shift> sortedExisting, Shift candidate)
    {
        var (prev, next) = Neighbours(sortedExisting, candidate);
        return (prev is not null && prev.OverlapsWith(candidate))
            || (next is not null && next.OverlapsWith(candidate));
    }

    /// <summary>
    /// Smallest rest gap the candidate would have to its neighbouring shifts, or null if it has
    /// no neighbours. A negative value means overlap.
    /// </summary>
    public static TimeSpan? MinRestAround(IReadOnlyList<Shift> sortedExisting, Shift candidate)
    {
        var (prev, next) = Neighbours(sortedExisting, candidate);
        TimeSpan? best = null;
        if (prev is not null)
        {
            best = candidate.RestAfter(prev);
        }
        if (next is not null)
        {
            var gap = next.RestAfter(candidate);
            best = best is null ? gap : TimeSpan.FromTicks(Math.Min(best.Value.Ticks, gap.Ticks));
        }
        return best;
    }

    private static (Shift? Prev, Shift? Next) Neighbours(IReadOnlyList<Shift> sorted, Shift candidate)
    {
        // lower bound: first index whose Start >= candidate.Start
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].Start < candidate.Start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        var prev = lo > 0 ? sorted[lo - 1] : null;
        var next = lo < sorted.Count ? sorted[lo] : null;
        // Exclude the candidate itself if it is already in the list (re-evaluating an assignment).
        if (prev is not null && prev.Id == candidate.Id)
        {
            prev = lo > 1 ? sorted[lo - 2] : null;
        }
        if (next is not null && next.Id == candidate.Id)
        {
            next = lo + 1 < sorted.Count ? sorted[lo + 1] : null;
        }
        return (prev, next);
    }
}
