using ShiftSense.Agent.Persistence;
using ShiftSense.Domain;

namespace ShiftSense.Agent.Tools;

/// <summary>Walks the manager chain. Shared by the tools and the approval endpoints.</summary>
public static class ReportingLine
{
    public static async Task<bool> ManagesAsync(ShiftSenseStore store, string managerId, Employee target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(target);
        var cursor = target;
        for (var depth = 0; depth < 16 && cursor.ManagerId is not null; depth++)
        {
            if (cursor.ManagerId == managerId)
            {
                return true;
            }
            cursor = await store.GetEmployeeAsync(cursor.ManagerId, ct);
            if (cursor is null)
            {
                return false;
            }
        }
        return false;
    }

    public static async Task<bool> ManagesAsync(ShiftSenseStore store, string managerId, string targetEmployeeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var target = await store.GetEmployeeAsync(targetEmployeeId, ct);
        return target is not null && await ManagesAsync(store, managerId, target, ct);
    }
}
