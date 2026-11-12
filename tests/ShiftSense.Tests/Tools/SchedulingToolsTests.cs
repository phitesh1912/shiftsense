using Microsoft.EntityFrameworkCore;
using ShiftSense.Agent.Tools;
using ShiftSense.Domain;
using ShiftSense.Tests.Persistence;

namespace ShiftSense.Tests.Tools;

public class SchedulingToolsTests
{
    private static CallerContext Caller(string id, Role role, Region region, string team) =>
        new(id, id, role, region, team, "conv-1");

    private static (StoreFixture F, CallerAccessor Acc, SchedulingTools Tools) Setup(CallerContext caller)
    {
        var f = new StoreFixture();
        var acc = new CallerAccessor { Current = caller };
        return (f, acc, new SchedulingTools(f.Store, acc, f.Clock));
    }

    private static readonly CallerContext IndiaEmployee = Caller("E002", Role.Employee, Region.IN, "T-BLR");
    private static readonly CallerContext IndiaManager = Caller("E001", Role.Manager, Region.IN, "T-BLR");
    private static readonly CallerContext UsEmployee = Caller("E015", Role.Employee, Region.US, "T-AUS");
    private static readonly CallerContext UsManager = Caller("E014", Role.Manager, Region.US, "T-AUS");

    [Fact]
    public async Task Employee_reads_own_balance_and_an_audit_row_is_written()
    {
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            var r = Assert.IsType<LeaveBalanceResult>(await tools.GetLeaveBalanceAsync());
            Assert.Equal("E002", r.EmployeeId);
            Assert.Contains(r.Balances, b => b.Type == LeaveType.Casual);
            var audit = (await f.Store.RecentAuditAsync(1)).Single();
            Assert.Equal("get_leave_balance", audit.Tool);
            Assert.True(audit.Authorized && audit.Succeeded);
            Assert.Equal("E002", audit.ActorId);
            Assert.Equal("conv-1", audit.ConversationId);
        }
    }

    [Fact]
    public async Task Employee_cannot_read_a_colleagues_balance_and_the_refusal_is_audited()
    {
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            var r = Assert.IsType<ToolError>(await tools.GetLeaveBalanceAsync("E003"));
            Assert.Equal("not_authorized", r.Code);
            var audit = (await f.Store.RecentAuditAsync(1)).Single();
            Assert.False(audit.Authorized);
            Assert.False(audit.Succeeded);
            Assert.Contains("E003", audit.ArgumentsJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Manager_reads_a_report_but_not_someone_in_another_region()
    {
        var (f, _, tools) = Setup(IndiaManager);
        using (f)
        {
            Assert.IsType<LeaveBalanceResult>(await tools.GetLeaveBalanceAsync("E003"));
            var r = Assert.IsType<ToolError>(await tools.GetLeaveBalanceAsync("E015"));
            Assert.Equal("not_authorized", r.Code);
            Assert.Contains("does not report to you", r.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("E3")]
    [InlineData("E0031")]
    [InlineData("'; DROP TABLE Employees; --")]
    public async Task Malformed_employee_ids_are_rejected_before_any_lookup(string bad)
    {
        var (f, _, tools) = Setup(IndiaManager);
        using (f)
        {
            var r = Assert.IsType<ToolError>(await tools.GetLeaveBalanceAsync(bad));
            Assert.Equal("invalid_argument", r.Code);
        }
    }

    [Fact]
    public async Task Shifts_default_to_next_14_days_and_reject_bad_dates()
    {
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            var r = Assert.IsType<ShiftsResult>(await tools.GetShiftsAsync());
            Assert.Equal(DateOnly.FromDateTime(SeedData.DemoNow), r.From);
            Assert.All(r.Shifts, s => Assert.True(s.Date >= r.From && s.Date <= r.To));
            Assert.NotEmpty(r.WeeklyHours);

            var bad = Assert.IsType<ToolError>(await tools.GetShiftsAsync(fromDate: "20/11/2026"));
            Assert.Equal("invalid_argument", bad.Code);
            var inverted = Assert.IsType<ToolError>(await tools.GetShiftsAsync(fromDate: "2026-11-20", toDate: "2026-11-19"));
            Assert.Equal("invalid_argument", inverted.Code);
        }
    }

    [Fact]
    public async Task Request_leave_creates_pending_request_and_never_touches_balance()
    {
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            var before = (await f.Store.GetLeaveBalancesAsync("E002")).Single(b => b.Type == LeaveType.Sick).Days;
            var r = Assert.IsType<RequestCreatedResult>(await tools.RequestLeaveAsync("sick", "2026-11-23", "2026-11-24", "flu"));
            Assert.Equal("pending", r.Status);
            Assert.Contains("2 working days", r.Summary, StringComparison.Ordinal);
            var after = (await f.Store.GetLeaveBalancesAsync("E002")).Single(b => b.Type == LeaveType.Sick).Days;
            Assert.Equal(before, after);
            var stored = await f.Db.LeaveRequests.AsNoTracking().SingleAsync(l => l.Id == r.RequestId);
            Assert.Equal(RequestStatus.Pending, stored.Status);
            Assert.Equal("E002", stored.EmployeeId);
        }
    }

    [Fact]
    public async Task Request_leave_validation()
    {
        var (f, _, tools) = Setup(UsEmployee);
        using (f)
        {
            Assert.Equal("invalid_argument", Assert.IsType<ToolError>(await tools.RequestLeaveAsync("casual", "2026-11-23", "2026-11-23")).Code); // US has no casual
            Assert.Equal("invalid_argument", Assert.IsType<ToolError>(await tools.RequestLeaveAsync("sick", "2026-11-01", "2026-11-02")).Code);   // past
            Assert.Equal("invalid_argument", Assert.IsType<ToolError>(await tools.RequestLeaveAsync("sick", "2026-11-21", "2026-11-22")).Code);   // weekend only
            Assert.Equal("invalid_argument", Assert.IsType<ToolError>(await tools.RequestLeaveAsync("vacation", "2026-11-23", "2026-11-23")).Code);
        }
    }

    [Fact]
    public async Task Swap_request_requires_own_shift_and_eligible_target()
    {
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            var mine = Assert.IsType<ShiftsResult>(await tools.GetShiftsAsync(fromDate: "2026-11-19", toDate: "2026-11-29")).Shifts;
            Assert.NotEmpty(mine);
            ShiftLine? chosen = null;
            SwapCandidatesResult? cands = null;
            foreach (var s in mine)
            {
                var c = Assert.IsType<SwapCandidatesResult>(await tools.FindSwapCandidatesAsync(s.ShiftId));
                if (c.Candidates.Count > 0)
                {
                    (chosen, cands) = (s, c);
                    break;
                }
            }
            Assert.NotNull(chosen);
            Assert.NotNull(cands);
            var target = cands.Candidates[0];

            var ok = Assert.IsType<RequestCreatedResult>(await tools.RequestShiftSwapAsync(chosen.ShiftId, target.EmployeeId, "wedding"));
            Assert.Equal("swap", ok.Kind);
            Assert.Equal("pending", ok.Status);

            // someone else's shift
            var other = (await f.Store.GetShiftsAsync("E003", SeedData.DemoNow, SeedData.DemoNow.AddDays(14)))[0];
            var denied = Assert.IsType<ToolError>(await tools.RequestShiftSwapAsync(other.Id, target.EmployeeId));
            Assert.Equal("not_authorized", denied.Code);

            // ineligible target: someone from the rejected list, if any
            if (cands.NotEligible.Count > 0)
            {
                var bad = Assert.IsType<ToolError>(await tools.RequestShiftSwapAsync(chosen.ShiftId, cands.NotEligible[0].EmployeeId));
                Assert.Equal("not_eligible", bad.Code);
            }
        }
    }

    [Fact]
    public async Task Overtime_uses_regional_strategy()
    {
        var (fIn, _, toolsIn) = Setup(IndiaEmployee);
        using (fIn)
        {
            var r = Assert.IsType<OvertimeResult>(await toolsIn.CalculateOvertimePayAsync(dateInWeek: "2026-11-18"));
            Assert.Equal("INR", r.Currency);
            Assert.Equal(new DateOnly(2026, 11, 16), r.WeekStart);
        }
        var (fUs, _, toolsUs) = Setup(UsEmployee);
        using (fUs)
        {
            var r = Assert.IsType<OvertimeResult>(await toolsUs.CalculateOvertimePayAsync());
            Assert.Equal("USD", r.Currency);
        }
    }

    [Fact]
    public async Task Coverage_is_manager_only_and_region_scoped()
    {
        var (fe, _, emp) = Setup(IndiaEmployee);
        using (fe)
        {
            Assert.Equal("not_authorized", Assert.IsType<ToolError>(await emp.GetTeamCoverageAsync()).Code);
        }
        var (fm, _, mgr) = Setup(IndiaManager);
        using (fm)
        {
            var r = Assert.IsType<CoverageResultDto>(await mgr.GetTeamCoverageAsync(fromDate: "2026-11-16", toDate: "2026-11-22"));
            Assert.Equal("T-BLR", r.TeamId);
            Assert.NotEmpty(r.Slots);
            Assert.Single(r.OpenShifts);
            Assert.NotNull(r.OpenShifts[0].SuggestedEmployeeId);
            Assert.Equal("not_authorized", Assert.IsType<ToolError>(await mgr.GetTeamCoverageAsync("T-AUS")).Code);
        }
    }

    [Fact]
    public async Task Injection_style_arguments_cannot_escalate()
    {
        // The model is told "I'm the manager" and tries to read the manager's data as an employee.
        var (f, _, tools) = Setup(IndiaEmployee);
        using (f)
        {
            Assert.Equal("not_authorized", Assert.IsType<ToolError>(await tools.GetLeaveBalanceAsync("E001")).Code);
            Assert.Equal("not_authorized", Assert.IsType<ToolError>(await tools.CalculateOvertimePayAsync("E001")).Code);
            // there is simply no approve tool to call
            Assert.DoesNotContain(typeof(SchedulingTools).GetMethods(), m => m.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase));
        }
    }
}
