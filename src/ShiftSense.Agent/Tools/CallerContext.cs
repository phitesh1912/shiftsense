using ShiftSense.Domain;

namespace ShiftSense.Agent.Tools;

/// <summary>
/// Who is calling. Built by the host (API or MCP server) from the *authenticated* identity —
/// never from anything the model says. Tools treat "me" as this employee and check every other
/// employee id against the manager relationship.
/// </summary>
public sealed record CallerContext(
    string EmployeeId,
    string Name,
    Role Role,
    Region Region,
    string TeamId,
    string ConversationId)
{
    public bool IsManager => Role == Role.Manager;
}

public interface ICallerAccessor
{
    CallerContext Current { get; }
}

/// <summary>Simple holder; the host sets it per request (scoped) or per process (stdio MCP).</summary>
public sealed class CallerAccessor : ICallerAccessor
{
    private CallerContext? _current;

    public CallerContext Current
    {
        get => _current ?? throw new InvalidOperationException("no caller context set for this request");
        set => _current = value;
    }
}
