using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

public sealed record OvertimeBreakdown(
    Region Region,
    string Currency,
    double TotalHours,
    double RegularHours,
    double OvertimeHours,
    decimal HourlyRate,
    decimal RegularPay,
    decimal OvertimePay,
    IReadOnlyList<string> Notes)
{
    public decimal TotalPay => RegularPay + OvertimePay;
}

/// <summary>
/// Strategy pattern: each region's overtime law is one class. Adding a region (or a state rule
/// like California daily OT) means adding a strategy, not editing a switch statement that
/// touches every payroll calculation.
/// </summary>
public interface IOvertimePolicy
{
    Region Region { get; }

    /// <summary>Compute pay for one employee's shifts within one ISO week.</summary>
    OvertimeBreakdown Calculate(IReadOnlyList<Shift> shiftsInWeek, decimal hourlyRate);
}

/// <summary>India: 2× for hours beyond 9/day or 48/week (whichever yields more OT, no double count).</summary>
public sealed class IndiaOvertimePolicy : IOvertimePolicy
{
    private static readonly PayPolicy Policy = PayPolicy.India;
    public Region Region => Region.IN;

    public OvertimeBreakdown Calculate(IReadOnlyList<Shift> shiftsInWeek, decimal hourlyRate)
    {
        ArgumentNullException.ThrowIfNull(shiftsInWeek);
        var notes = new List<string>();
        var total = shiftsInWeek.Sum(s => s.Hours);

        var dailyOt = shiftsInWeek
            .GroupBy(s => s.Date)
            .Sum(g => Math.Max(0, g.Sum(s => s.Hours) - Policy.DailyOvertimeThresholdHours!.Value));
        if (dailyOt > 0)
        {
            notes.Add($"{dailyOt:0.#}h over the {Policy.DailyOvertimeThresholdHours:0}h/day threshold");
        }

        // Hours already counted as daily OT must not also count toward the weekly threshold.
        var weeklyOt = Math.Max(0, (total - dailyOt) - Policy.WeeklyOvertimeThresholdHours);
        if (weeklyOt > 0)
        {
            notes.Add($"{weeklyOt:0.#}h over the {Policy.WeeklyOvertimeThresholdHours:0}h/week threshold");
        }

        var ot = dailyOt + weeklyOt;
        return Build(total, ot, hourlyRate, notes);
    }

    private OvertimeBreakdown Build(double total, double ot, decimal rate, List<string> notes) =>
        new(Region, Policy.Currency, total, total - ot, ot, rate,
            Money((decimal)(total - ot) * rate), Money((decimal)ot * rate * Policy.OvertimeMultiplier), notes);

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>US (FLSA baseline): 1.5× for hours beyond 40 in the workweek. No daily rule.</summary>
public sealed class USOvertimePolicy : IOvertimePolicy
{
    private static readonly PayPolicy Policy = PayPolicy.UnitedStates;
    public Region Region => Region.US;

    public OvertimeBreakdown Calculate(IReadOnlyList<Shift> shiftsInWeek, decimal hourlyRate)
    {
        ArgumentNullException.ThrowIfNull(shiftsInWeek);
        var total = shiftsInWeek.Sum(s => s.Hours);
        var ot = Math.Max(0, total - Policy.WeeklyOvertimeThresholdHours);
        var notes = new List<string>();
        if (ot > 0)
        {
            notes.Add($"{ot:0.#}h over the {Policy.WeeklyOvertimeThresholdHours:0}h/week threshold (FLSA)");
        }
        var regular = total - ot;
        return new OvertimeBreakdown(Region, Policy.Currency, total, regular, ot, hourlyRate,
            Money((decimal)regular * hourlyRate), Money((decimal)ot * hourlyRate * Policy.OvertimeMultiplier), notes);
    }

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

public static class OvertimePolicies
{
    private static readonly Dictionary<Region, IOvertimePolicy> Registry = new()
    {
        [Region.IN] = new IndiaOvertimePolicy(),
        [Region.US] = new USOvertimePolicy(),
    };

    public static IOvertimePolicy For(Region region) =>
        Registry.TryGetValue(region, out var p)
            ? p
            : throw new ArgumentOutOfRangeException(nameof(region), region, "no overtime policy registered");
}
