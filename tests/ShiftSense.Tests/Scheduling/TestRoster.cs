using ShiftSense.Domain;
using ShiftSense.Scheduling;

namespace ShiftSense.Tests.Scheduling;

/// <summary>Small hand-built roster so each test states its own facts.</summary>
internal static class TestRoster
{
    public static readonly DateOnly Monday = new(2026, 11, 16); // ISO week 47

    public static Employee Emp(string id, string team = "T1", int maxWeekly = 40, params string[] skills) => new()
    {
        Id = id, Name = id, Email = $"{id}@x", Region = Region.US, Role = Role.Employee, TeamId = team,
        ManagerId = "M1", JoinDate = new DateOnly(2022, 1, 1), HourlyRate = 20m, MaxWeeklyHours = maxWeekly,
        SkillIds = [.. (skills.Length == 0 ? [Skills.Cashier] : skills)],
    };

    public static Shift S(string id, string? emp, int dayOffset, int startHour, int hours = 8, string team = "T1", params string[] skills) => new()
    {
        Id = id, TeamId = team, EmployeeId = emp,
        Start = Monday.AddDays(dayOffset).ToDateTime(new TimeOnly(startHour, 0)),
        End = Monday.AddDays(dayOffset).ToDateTime(new TimeOnly(startHour, 0)).AddHours(hours),
        RequiredSkillIds = [.. (skills.Length == 0 ? [Skills.Cashier] : skills)],
    };

    public static RosterSnapshot Snapshot(IEnumerable<Employee> emps, IEnumerable<Shift> shifts, IEnumerable<LeaveRequest>? leave = null) =>
        new(emps, shifts, leave);

    public static LeaveRequest Approved(string emp, int dayOffset, int days = 1) => new()
    {
        Id = $"L-{emp}-{dayOffset}", EmployeeId = emp, Type = LeaveType.Annual,
        StartDate = Monday.AddDays(dayOffset), EndDate = Monday.AddDays(dayOffset + days - 1), Days = days,
        Status = RequestStatus.Approved, CreatedAt = DateTime.UtcNow,
    };
}
