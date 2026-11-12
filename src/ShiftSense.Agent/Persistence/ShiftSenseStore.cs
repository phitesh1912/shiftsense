using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShiftSense.Domain;
using ShiftSense.Scheduling;

namespace ShiftSense.Agent.Persistence;

public sealed record PendingRequests(IReadOnlyList<LeaveRequest> Leave, IReadOnlyList<SwapRequest> Swaps);

public sealed record Decision(bool Applied, string Message);

/// <summary>
/// The only class that talks to the DbContext. Tools and endpoints go through here, which keeps
/// the write paths (create pending request, approve/reject) small enough to audit by eye.
/// </summary>
public sealed class ShiftSenseStore(ShiftSenseDbContext db, IClock clock)
{
    // --- bootstrap ----------------------------------------------------------------------------

    public async Task<int> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        if (await db.Employees.AnyAsync(ct))
        {
            return 0;
        }
        var employees = SeedData.Employees();
        db.Teams.AddRange(SeedData.Teams);
        db.Employees.AddRange(employees);
        db.Shifts.AddRange(SeedData.Shifts(employees));
        db.LeaveBalances.AddRange(SeedData.LeaveBalances(employees));
        db.LeaveRequests.AddRange(SeedData.LeaveRequests());
        return await db.SaveChangesAsync(ct);
    }

    // --- reads -------------------------------------------------------------------------------

    public Task<Employee?> GetEmployeeAsync(string id, CancellationToken ct = default) =>
        db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<List<Employee>> GetTeamMembersAsync(string teamId, CancellationToken ct = default) =>
        db.Employees.AsNoTracking().Where(e => e.TeamId == teamId).OrderBy(e => e.Id).ToListAsync(ct);

    public Task<Team?> GetTeamAsync(string teamId, CancellationToken ct = default) =>
        db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId, ct);

    public Task<List<LeaveBalance>> GetLeaveBalancesAsync(string employeeId, CancellationToken ct = default) =>
        db.LeaveBalances.AsNoTracking().Where(b => b.EmployeeId == employeeId).OrderBy(b => b.Type).ToListAsync(ct);

    public Task<List<Shift>> GetShiftsAsync(string employeeId, DateTime from, DateTime to, CancellationToken ct = default) =>
        db.Shifts.AsNoTracking()
            .Where(s => s.EmployeeId == employeeId && s.Start >= from && s.Start < to)
            .OrderBy(s => s.Start)
            .ToListAsync(ct);

    public Task<List<Shift>> GetTeamShiftsAsync(string teamId, DateTime from, DateTime to, CancellationToken ct = default) =>
        db.Shifts.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.Start >= from && s.Start < to)
            .OrderBy(s => s.Start)
            .ToListAsync(ct);

    public Task<Shift?> GetShiftAsync(string shiftId, CancellationToken ct = default) =>
        db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shiftId, ct);

    /// <summary>Everything the scheduling algorithms need, in one round trip per table.</summary>
    public async Task<RosterSnapshot> LoadRosterAsync(CancellationToken ct = default)
    {
        var employees = await db.Employees.AsNoTracking().ToListAsync(ct);
        var shifts = await db.Shifts.AsNoTracking().ToListAsync(ct);
        var leave = await db.LeaveRequests.AsNoTracking()
            .Where(l => l.Status == RequestStatus.Approved)
            .ToListAsync(ct);
        return new RosterSnapshot(employees, shifts, leave);
    }

    public async Task<PendingRequests> GetPendingAsync(CancellationToken ct = default)
    {
        var leave = await db.LeaveRequests.AsNoTracking()
            .Where(l => l.Status == RequestStatus.Pending).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var swaps = await db.SwapRequests.AsNoTracking()
            .Where(s => s.Status == RequestStatus.Pending).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return new PendingRequests(leave, swaps);
    }

    public Task<LeaveRequest?> GetLeaveRequestAsync(string id, CancellationToken ct = default) =>
        db.LeaveRequests.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<SwapRequest?> GetSwapRequestAsync(string id, CancellationToken ct = default) =>
        db.SwapRequests.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<List<LeaveRequest>> GetLeaveRequestsAsync(string employeeId, CancellationToken ct = default) =>
        db.LeaveRequests.AsNoTracking().Where(l => l.EmployeeId == employeeId).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);

    // --- writes (all create PENDING state; approval is a separate, manager-only path) ----------

    public async Task<LeaveRequest> CreateLeaveRequestAsync(
        string employeeId, LeaveType type, DateOnly start, DateOnly end, decimal days, string? reason,
        CancellationToken ct = default)
    {
        var request = new LeaveRequest
        {
            Id = await NextIdAsync("LR", db.LeaveRequests, ct),
            EmployeeId = employeeId,
            Type = type,
            StartDate = start,
            EndDate = end,
            Days = days,
            Reason = reason,
            Status = RequestStatus.Pending,
            CreatedAt = clock.UtcNow,
        };
        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<SwapRequest> CreateSwapRequestAsync(
        string requesterId, string shiftId, string targetEmployeeId, string? reason, CancellationToken ct = default)
    {
        var request = new SwapRequest
        {
            Id = await NextIdAsync("SW", db.SwapRequests, ct),
            RequesterId = requesterId,
            ShiftId = shiftId,
            TargetEmployeeId = targetEmployeeId,
            Reason = reason,
            Status = RequestStatus.Pending,
            CreatedAt = clock.UtcNow,
        };
        db.SwapRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    /// <summary>
    /// Approve or reject a leave request. Approval deducts the balance; both are idempotent on an
    /// already-decided request (returns Applied=false).
    /// </summary>
    public async Task<Decision> DecideLeaveAsync(string requestId, bool approve, string deciderId, string? note, CancellationToken ct = default)
    {
        var request = await db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == requestId, ct);
        if (request is null)
        {
            return new Decision(false, $"leave request {requestId} not found");
        }
        if (request.Status != RequestStatus.Pending)
        {
            return new Decision(false, $"leave request {requestId} is already {request.Status}");
        }

        if (approve)
        {
            var balance = await db.LeaveBalances.FirstOrDefaultAsync(
                b => b.EmployeeId == request.EmployeeId && b.Type == request.Type, ct);
            if (balance is null || balance.Days < request.Days)
            {
                request.Status = RequestStatus.Rejected;
                request.DecisionNote = $"insufficient {request.Type} balance ({balance?.Days ?? 0} days) for {request.Days} days";
                Stamp(request, deciderId);
                await db.SaveChangesAsync(ct);
                return new Decision(true, $"rejected: {request.DecisionNote}");
            }
            balance.Days -= request.Days;
            balance.UsedThisYear += request.Days;
            request.Status = RequestStatus.Approved;
        }
        else
        {
            request.Status = RequestStatus.Rejected;
        }
        request.DecisionNote = note;
        Stamp(request, deciderId);
        await db.SaveChangesAsync(ct);
        return new Decision(true, $"{request.Status.ToString().ToLowerInvariant()} {requestId}");
    }

    /// <summary>
    /// Approve or reject a swap. Approval re-validates every scheduling constraint against the
    /// *current* roster — the world may have changed since the request was created — and only
    /// reassigns the shift if the target is still eligible.
    /// </summary>
    public async Task<Decision> DecideSwapAsync(string requestId, bool approve, string deciderId, string? note, CancellationToken ct = default)
    {
        var request = await db.SwapRequests.FirstOrDefaultAsync(s => s.Id == requestId, ct);
        if (request is null)
        {
            return new Decision(false, $"swap request {requestId} not found");
        }
        if (request.Status != RequestStatus.Pending)
        {
            return new Decision(false, $"swap request {requestId} is already {request.Status}");
        }

        if (approve)
        {
            var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == request.ShiftId, ct);
            if (shift is null)
            {
                return new Decision(false, $"shift {request.ShiftId} not found");
            }
            var roster = await LoadRosterAsync(ct);
            var target = roster.Employee(request.TargetEmployeeId);
            if (target is null)
            {
                return new Decision(false, $"employee {request.TargetEmployeeId} not found");
            }
            var eval = new SwapCandidateFinder(roster).Evaluate(target, shift);
            if (!eval.Eligible)
            {
                request.Status = RequestStatus.Rejected;
                request.DecisionNote = "no longer eligible at approval time: " + string.Join("; ", eval.Violations);
                Stamp(request, deciderId);
                await db.SaveChangesAsync(ct);
                return new Decision(true, $"rejected: {request.DecisionNote}");
            }
            shift.EmployeeId = request.TargetEmployeeId;
            request.Status = RequestStatus.Approved;
        }
        else
        {
            request.Status = RequestStatus.Rejected;
        }
        request.DecisionNote = note;
        Stamp(request, deciderId);
        await db.SaveChangesAsync(ct);
        return new Decision(true, $"{request.Status.ToString().ToLowerInvariant()} {requestId}");
    }

    // --- audit -------------------------------------------------------------------------------

    public async Task AddAuditAsync(AuditEntry entry, CancellationToken ct = default)
    {
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<AuditEntry>> RecentAuditAsync(int take = 50, CancellationToken ct = default) =>
        db.AuditEntries.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);

    // --- helpers -----------------------------------------------------------------------------

    private void Stamp(LeaveRequest r, string deciderId)
    {
        r.DecidedAt = clock.UtcNow;
        r.DecidedBy = deciderId;
    }

    private void Stamp(SwapRequest r, string deciderId)
    {
        r.DecidedAt = clock.UtcNow;
        r.DecidedBy = deciderId;
    }

    private static async Task<string> NextIdAsync<T>(string prefix, DbSet<T> set, CancellationToken ct) where T : class
    {
        // Readable sequential ids for the demo ("LR-0003"). A real system would use the DB
        // sequence; a count-based id is fine for a single-writer SQLite demo.
        var n = await set.CountAsync(ct) + 1;
        return $"{prefix}-{n.ToString("0000", CultureInfo.InvariantCulture)}";
    }
}
