namespace ShiftSense.Domain;

public sealed class LeaveBalance
{
    public required string EmployeeId { get; init; }
    public required LeaveType Type { get; init; }

    /// <summary>Days available right now (accrued + carried − used − pending holds).</summary>
    public decimal Days { get; set; }

    public decimal UsedThisYear { get; set; }
}

public sealed class LeaveRequest
{
    public required string Id { get; init; }
    public required string EmployeeId { get; init; }
    public required LeaveType Type { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required decimal Days { get; init; }
    public string? Reason { get; init; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public required DateTime CreatedAt { get; init; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public bool Covers(DateOnly day) => day >= StartDate && day <= EndDate;
}

public sealed class SwapRequest
{
    public required string Id { get; init; }
    public required string RequesterId { get; init; }
    public required string ShiftId { get; init; }

    /// <summary>The colleague proposed to take the shift.</summary>
    public required string TargetEmployeeId { get; init; }

    public string? Reason { get; init; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public required DateTime CreatedAt { get; init; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
}
