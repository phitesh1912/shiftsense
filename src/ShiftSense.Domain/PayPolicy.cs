namespace ShiftSense.Domain;

/// <summary>
/// Region-specific pay parameters. The *computation* lives in the Scheduling project's
/// IOvertimePolicy strategies; this is just the data those strategies read.
/// </summary>
public sealed record PayPolicy(
    Region Region,
    string Currency,
    /// <summary>Hours per day above which overtime applies (null = no daily rule).</summary>
    double? DailyOvertimeThresholdHours,
    /// <summary>Hours per ISO week above which overtime applies.</summary>
    double WeeklyOvertimeThresholdHours,
    /// <summary>Multiplier on the hourly rate for overtime hours.</summary>
    decimal OvertimeMultiplier,
    /// <summary>Monthly accrual of annual leave, in days.</summary>
    decimal AnnualLeaveAccrualPerMonth,
    /// <summary>Maximum annual-leave balance that survives the year boundary.</summary>
    decimal AnnualLeaveCarryForwardCap)
{
    public static readonly PayPolicy India = new(
        Region.IN,
        "INR",
        DailyOvertimeThresholdHours: 9,
        WeeklyOvertimeThresholdHours: 48,
        OvertimeMultiplier: 2.0m,
        AnnualLeaveAccrualPerMonth: 1.5m,
        AnnualLeaveCarryForwardCap: 45m);

    public static readonly PayPolicy UnitedStates = new(
        Region.US,
        "USD",
        DailyOvertimeThresholdHours: null,
        WeeklyOvertimeThresholdHours: 40,
        OvertimeMultiplier: 1.5m,
        AnnualLeaveAccrualPerMonth: 1.25m,
        AnnualLeaveCarryForwardCap: 5m);

    public static PayPolicy For(Region region) => region switch
    {
        Region.IN => India,
        Region.US => UnitedStates,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, null),
    };
}
