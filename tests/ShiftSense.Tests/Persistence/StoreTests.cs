using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShiftSense.Agent.Persistence;
using ShiftSense.Domain;

namespace ShiftSense.Tests.Persistence;

/// <summary>Each test gets a fresh in-memory SQLite database seeded with the demo data.</summary>
public sealed class StoreFixture : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");

    public StoreFixture()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<ShiftSenseDbContext>().UseSqlite(_conn).Options;
        Db = new ShiftSenseDbContext(options);
        Clock = new FixedClock(SeedData.DemoNow);
        Store = new ShiftSenseStore(Db, Clock);
        Store.SeedIfEmptyAsync().GetAwaiter().GetResult();
    }

    public ShiftSenseDbContext Db { get; }
    public FixedClock Clock { get; }
    public ShiftSenseStore Store { get; }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}

public class StoreTests
{
    [Fact]
    public async Task Seeds_once_and_round_trips_skill_sets()
    {
        using var f = new StoreFixture();
        Assert.Equal(0, await f.Store.SeedIfEmptyAsync());
        Assert.Equal(25, await f.Db.Employees.CountAsync());
        var e = await f.Store.GetEmployeeAsync("E001");
        Assert.NotNull(e);
        Assert.Contains(Skills.Supervisor, e.SkillIds);
        Assert.Equal(4, e.SkillIds.Count);
        var roster = await f.Store.LoadRosterAsync();
        Assert.Equal(25, roster.Employees.Count);
        Assert.True(roster.IsOnLeave("E004", new DateOnly(2026, 11, 24)));
    }

    [Fact]
    public async Task Leave_request_is_created_pending_and_approval_deducts_balance()
    {
        using var f = new StoreFixture();
        var before = (await f.Store.GetLeaveBalancesAsync("E002")).Single(b => b.Type == LeaveType.Sick).Days;
        var r = await f.Store.CreateLeaveRequestAsync("E002", LeaveType.Sick, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), 2, "dentist");
        Assert.Equal(RequestStatus.Pending, r.Status);
        Assert.StartsWith("LR-", r.Id, StringComparison.Ordinal);
        Assert.Equal(SeedData.DemoNow, r.CreatedAt);

        var pending = await f.Store.GetPendingAsync();
        Assert.Contains(pending.Leave, l => l.Id == r.Id);

        var d = await f.Store.DecideLeaveAsync(r.Id, approve: true, deciderId: "E001", note: "ok");
        Assert.True(d.Applied);
        var after = (await f.Store.GetLeaveBalancesAsync("E002")).Single(b => b.Type == LeaveType.Sick).Days;
        Assert.Equal(before - 2, after);

        // second decision is a no-op
        var again = await f.Store.DecideLeaveAsync(r.Id, approve: false, deciderId: "E001", note: null);
        Assert.False(again.Applied);
    }

    [Fact]
    public async Task Leave_approval_with_insufficient_balance_is_rejected()
    {
        using var f = new StoreFixture();
        var r = await f.Store.CreateLeaveRequestAsync("E002", LeaveType.Sick, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), 99, null);
        var d = await f.Store.DecideLeaveAsync(r.Id, approve: true, deciderId: "E001", note: null);
        Assert.True(d.Applied);
        Assert.StartsWith("rejected: insufficient", d.Message, StringComparison.Ordinal);
        var stored = await f.Db.LeaveRequests.AsNoTracking().SingleAsync(l => l.Id == r.Id);
        Assert.Equal(RequestStatus.Rejected, stored.Status);
    }

    [Fact]
    public async Task Swap_approval_reassigns_shift_only_if_still_eligible()
    {
        using var f = new StoreFixture();
        var (shift, cand) = await FirstSwappableAsync(f);

        var r = await f.Store.CreateSwapRequestAsync(shift.EmployeeId!, shift.Id, cand.Employee.Id, "swap");
        Assert.Equal(RequestStatus.Pending, r.Status);

        var d = await f.Store.DecideSwapAsync(r.Id, approve: true, deciderId: "E001", note: null);
        Assert.True(d.Applied);
        Assert.StartsWith("approved", d.Message, StringComparison.Ordinal);
        var updated = await f.Store.GetShiftAsync(shift.Id);
        Assert.Equal(cand.Employee.Id, updated!.EmployeeId);
    }

    [Fact]
    public async Task Swap_approval_rejects_when_target_became_ineligible()
    {
        using var f = new StoreFixture();
        var (shift, cand) = await FirstSwappableAsync(f);
        var r = await f.Store.CreateSwapRequestAsync(shift.EmployeeId!, shift.Id, cand.Employee.Id, null);

        // World changes: the target goes on approved leave that day.
        var leave = await f.Store.CreateLeaveRequestAsync(cand.Employee.Id, LeaveType.Sick, shift.Date, shift.Date, 1, null);
        await f.Store.DecideLeaveAsync(leave.Id, approve: true, deciderId: "E001", note: null);

        var d = await f.Store.DecideSwapAsync(r.Id, approve: true, deciderId: "E001", note: null);
        Assert.True(d.Applied);
        Assert.Contains("no longer eligible", d.Message, StringComparison.Ordinal);
        Assert.Contains("on approved leave", d.Message, StringComparison.Ordinal);
        var unchanged = await f.Store.GetShiftAsync(shift.Id);
        Assert.Equal(shift.EmployeeId, unchanged!.EmployeeId);
    }

    /// <summary>Any assigned seed shift that has at least one eligible swap candidate.</summary>
    private static async Task<(Shift Shift, ShiftSense.Scheduling.CandidateEvaluation Candidate)> FirstSwappableAsync(StoreFixture f)
    {
        var roster = await f.Store.LoadRosterAsync();
        var finder = new ShiftSense.Scheduling.SwapCandidateFinder(roster);
        foreach (var s in roster.Shifts.Where(s => s.IsAssigned).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var best = finder.FindBest(s, top: 1);
            if (best.Count > 0)
            {
                return (s, best[0]);
            }
        }
        throw new InvalidOperationException("seed roster has no swappable shift");
    }

    [Fact]
    public async Task Audit_entries_are_appended_and_read_newest_first()
    {
        using var f = new StoreFixture();
        for (var i = 0; i < 3; i++)
        {
            await f.Store.AddAuditAsync(new AuditEntry
            {
                Timestamp = SeedData.DemoNow.AddMinutes(i), ActorId = "E002", ActorRole = "employee",
                Tool = $"tool{i}", ArgumentsJson = "{}", Authorized = true, Succeeded = true, LatencyMs = 1,
            });
        }
        var recent = await f.Store.RecentAuditAsync(2);
        Assert.Equal(["tool2", "tool1"], recent.Select(a => a.Tool));
        Assert.True(recent[0].Id > recent[1].Id);
    }
}
