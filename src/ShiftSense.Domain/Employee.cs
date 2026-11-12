namespace ShiftSense.Domain;

public sealed class Employee
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required Region Region { get; init; }
    public required Role Role { get; init; }
    public required string TeamId { get; init; }

    /// <summary>Null for the top-level manager.</summary>
    public string? ManagerId { get; init; }

    public required DateOnly JoinDate { get; init; }
    public required decimal HourlyRate { get; init; }

    /// <summary>Statutory/contractual cap on scheduled hours per ISO week.</summary>
    public required int MaxWeeklyHours { get; init; }

    public HashSet<string> SkillIds { get; init; } = [];

    public bool HasSkills(IEnumerable<string> required) => required.All(SkillIds.Contains);

    public bool IsManager => Role == Role.Manager;

    /// <summary>True if <paramref name="other"/> reports (directly or transitively) to this employee.</summary>
    public bool Manages(Employee other, Func<string, Employee?> lookup)
    {
        var cursor = other;
        for (var depth = 0; depth < 16 && cursor?.ManagerId is not null; depth++)
        {
            if (cursor.ManagerId == Id)
            {
                return true;
            }
            cursor = lookup(cursor.ManagerId);
        }
        return false;
    }
}
