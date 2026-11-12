namespace ShiftSense.Domain;

/// <summary>
/// A scheduled block of work. Times are site-local wall-clock times (no offset); all scheduling
/// rules compare shifts within one region, so no cross-zone arithmetic is needed.
/// </summary>
public sealed class Shift
{
    public required string Id { get; init; }
    public required string TeamId { get; init; }

    /// <summary>Null when the shift is unassigned (e.g. dropped by the original employee).</summary>
    public string? EmployeeId { get; set; }

    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public HashSet<string> RequiredSkillIds { get; init; } = [];

    public TimeSpan Duration => End - Start;
    public double Hours => Duration.TotalHours;
    public DateOnly Date => DateOnly.FromDateTime(Start);
    public bool IsAssigned => EmployeeId is not null;

    /// <summary>Half-open interval overlap: [Start, End) ∩ [other.Start, other.End) ≠ ∅.</summary>
    public bool OverlapsWith(Shift other) => Start < other.End && other.Start < End;

    /// <summary>Rest between the end of <paramref name="earlier"/> and the start of this shift.</summary>
    public TimeSpan RestAfter(Shift earlier) => Start - earlier.End;

    /// <summary>ISO-8601 week key (year, week) used for weekly-hours caps.</summary>
    public (int Year, int Week) IsoWeek
    {
        get
        {
            var week = System.Globalization.ISOWeek.GetWeekOfYear(Start);
            var year = System.Globalization.ISOWeek.GetYear(Start);
            return (year, week);
        }
    }

    public override string ToString() =>
        $"{Id} {Start:yyyy-MM-dd HH:mm}-{End:HH:mm} team={TeamId} emp={EmployeeId ?? "-"}";
}
