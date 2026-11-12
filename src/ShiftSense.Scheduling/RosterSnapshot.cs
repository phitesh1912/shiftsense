using ShiftSense.Domain;

namespace ShiftSense.Scheduling;

/// <summary>
/// Immutable, pre-indexed view of the roster that the algorithms operate on. The persistence
/// layer builds one per request; tests build one from seed data. Keeping the algorithms free of
/// EF means they are trivially unit-testable and reusable from the MCP server.
/// </summary>
public sealed class RosterSnapshot
{
    private readonly Dictionary<string, Employee> _employees;
    private readonly Dictionary<string, Shift> _shifts;
    private readonly Dictionary<string, List<Shift>> _shiftsByEmployee;
    private readonly List<LeaveRequest> _approvedLeave;

    public RosterSnapshot(
        IEnumerable<Employee> employees,
        IEnumerable<Shift> shifts,
        IEnumerable<LeaveRequest>? leaveRequests = null)
    {
        _employees = employees.ToDictionary(e => e.Id, StringComparer.Ordinal);
        _shifts = shifts.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _shiftsByEmployee = _shifts.Values
            .Where(s => s.IsAssigned)
            .GroupBy(s => s.EmployeeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToList(), StringComparer.Ordinal);
        _approvedLeave = (leaveRequests ?? [])
            .Where(l => l.Status == RequestStatus.Approved)
            .ToList();
    }

    public IReadOnlyCollection<Employee> Employees => _employees.Values;
    public IReadOnlyCollection<Shift> Shifts => _shifts.Values;

    public Employee? Employee(string id) => _employees.GetValueOrDefault(id);
    public Shift? Shift(string id) => _shifts.GetValueOrDefault(id);

    /// <summary>An employee's assigned shifts, sorted by start time.</summary>
    public IReadOnlyList<Shift> ShiftsOf(string employeeId) =>
        _shiftsByEmployee.GetValueOrDefault(employeeId) ?? (IReadOnlyList<Shift>)[];

    public IEnumerable<Shift> OpenShifts() => _shifts.Values.Where(s => !s.IsAssigned);

    public bool IsOnLeave(string employeeId, DateOnly day) =>
        _approvedLeave.Any(l => l.EmployeeId == employeeId && l.Covers(day));

    public double WeeklyHours(string employeeId, (int Year, int Week) isoWeek) =>
        ShiftsOf(employeeId).Where(s => s.IsoWeek == isoWeek).Sum(s => s.Hours);
}
