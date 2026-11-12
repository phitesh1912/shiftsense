using ShiftSense.Domain;

namespace ShiftSense.Agent.Tools;

/// <summary>
/// Returned instead of throwing so the model can explain the refusal to the user. The code is
/// stable for tests and the UI; the message is for humans.
/// </summary>
public sealed record ToolError(string Code, string Message)
{
    /// <summary>Always true; lets the model (and the UI) spot an error object without a schema.</summary>
    public bool Error { get; init; } = true;
}

public sealed record LeaveBalanceLine(LeaveType Type, decimal Days, decimal UsedThisYear);

public sealed record LeaveBalanceResult(
    string EmployeeId,
    string Name,
    Region Region,
    IReadOnlyList<LeaveBalanceLine> Balances,
    decimal AccruedThisLeaveYear,
    string Note);

public sealed record ShiftLine(
    string ShiftId,
    DateOnly Date,
    string DayOfWeek,
    string Start,
    string End,
    double Hours,
    string TeamId,
    IReadOnlyList<string> RequiredSkills);

public sealed record WeekTotal(int IsoWeek, double Hours, int Cap);

public sealed record ShiftsResult(
    string EmployeeId,
    string Name,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ShiftLine> Shifts,
    IReadOnlyList<WeekTotal> WeeklyHours);

public sealed record CandidateLine(
    string EmployeeId,
    string Name,
    double WeeklyHoursBefore,
    double WeeklyHoursAfter,
    IReadOnlyList<string> Skills);

public sealed record RejectedLine(string EmployeeId, string Name, IReadOnlyList<string> Reasons);

public sealed record SwapCandidatesResult(
    string ShiftId,
    DateOnly Date,
    string Start,
    string End,
    string CurrentHolder,
    IReadOnlyList<CandidateLine> Candidates,
    IReadOnlyList<RejectedLine> NotEligible,
    string Note);

public sealed record RequestCreatedResult(
    string RequestId,
    string Kind,
    string Status,
    string Summary,
    string NextStep);

public sealed record OvertimeResult(
    string EmployeeId,
    string Name,
    DateOnly WeekStart,
    Region Region,
    string Currency,
    double TotalHours,
    double RegularHours,
    double OvertimeHours,
    decimal RegularPay,
    decimal OvertimePay,
    decimal TotalPay,
    IReadOnlyList<string> Notes);

public sealed record CoverageSlot(DateOnly Date, string Slot, int Staffed, int Required, bool Covered);

public sealed record OpenShiftSuggestion(string ShiftId, DateOnly Date, string Start, string? SuggestedEmployeeId, string? SuggestedName);

public sealed record CoverageResultDto(
    string TeamId,
    string TeamName,
    DateOnly From,
    DateOnly To,
    int MinHeadcountPerSlot,
    IReadOnlyList<CoverageSlot> Slots,
    IReadOnlyList<CoverageSlot> Gaps,
    IReadOnlyList<OpenShiftSuggestion> OpenShifts,
    string SolverStrategy);
