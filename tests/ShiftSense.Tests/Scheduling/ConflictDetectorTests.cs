using ShiftSense.Scheduling;
using static ShiftSense.Tests.Scheduling.TestRoster;

namespace ShiftSense.Tests.Scheduling;

public class ConflictDetectorTests
{
    [Fact]
    public void Finds_all_overlapping_pairs_and_nothing_else()
    {
        var a = S("a", "e", 0, 6);          // 06-14
        var b = S("b", "e", 0, 14);         // 14-22 (touches a, no overlap)
        var c = S("c", "e", 0, 13);         // 13-21 overlaps a and b
        var d = S("d", "e", 1, 6);          // next day
        var pairs = ConflictDetector.FindOverlaps([d, b, a, c]);
        var ids = pairs.Select(p => (p.A.Id, p.B.Id)).OrderBy(x => x).ToList();
        Assert.Equal([("a", "c"), ("c", "b")], ids);
    }

    [Fact]
    public void Overlap_check_uses_neighbours_only()
    {
        var existing = new[] { S("a", "e", 0, 6), S("b", "e", 1, 6), S("c", "e", 2, 6) };
        Assert.True(ConflictDetector.WouldOverlap(existing, S("x", null, 1, 10)));   // inside b
        Assert.False(ConflictDetector.WouldOverlap(existing, S("y", null, 1, 14)));  // after b, before c
        Assert.False(ConflictDetector.WouldOverlap(existing, S("z", null, 5, 6)));   // after all
        Assert.False(ConflictDetector.WouldOverlap([], S("w", null, 0, 6)));
    }

    [Fact]
    public void Min_rest_is_smallest_gap_to_either_neighbour()
    {
        var existing = new[] { S("a", "e", 0, 6), S("c", "e", 1, 14) }; // a ends Mon 14:00, c starts Tue 14:00
        var cand = S("x", null, 0, 22);                                   // Mon 22:00 - Tue 06:00
        var rest = ConflictDetector.MinRestAround(existing, cand);
        Assert.Equal(TimeSpan.FromHours(8), rest); // 8h after a; 8h before c -> min 8
        Assert.Null(ConflictDetector.MinRestAround([], cand));
    }

    [Fact]
    public void Re_evaluating_an_assigned_shift_ignores_itself()
    {
        var self = S("self", "e", 1, 6);
        var existing = new[] { S("a", "e", 0, 6), self, S("c", "e", 2, 6) };
        Assert.False(ConflictDetector.WouldOverlap(existing, self));
        Assert.Equal(TimeSpan.FromHours(16), ConflictDetector.MinRestAround(existing, self));
    }
}
