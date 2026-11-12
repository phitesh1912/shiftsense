using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using ShiftSense.Agent.Persistence;
using ShiftSense.Domain;
using ShiftSense.Scheduling;

namespace ShiftSense.Agent.Tools;

/// <summary>
/// The seven tools. One class serves both hosts: the agent wraps these methods with
/// <c>AIFunctionFactory</c>, the MCP server discovers them through the attributes. Every method:
/// 1. validates its arguments strictly,
/// 2. authorizes the caller against the target employee/team,
/// 3. does the work through the store and scheduling algorithms,
/// 4. writes one audit row — whether it succeeded, was refused, or was rejected.
/// Write tools only ever create PENDING requests. There is deliberately no approve tool.
/// </summary>
[McpServerToolType]
public sealed class SchedulingTools(ShiftSenseStore store, ICallerAccessor caller, IClock clock)
{
    private static readonly JsonSerializerOptions ArgsJson = new(JsonSerializerDefaults.Web);

    // --- read tools ---------------------------------------------------------------------------

    [McpServerTool(Name = "get_leave_balance", ReadOnly = true, Idempotent = true)]
    [Description("Get leave balances (annual/PTO, sick, casual) for yourself, or for a direct report if you are a manager.")]
    public Task<object> GetLeaveBalanceAsync(
        [Description("Employee id like E004. Leave empty for yourself.")] string? employeeId = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("get_leave_balance", new { employeeId }, async () =>
        {
            var target = await ResolveTargetAsync(employeeId, cancellationToken);
            var balances = await store.GetLeaveBalancesAsync(target.Id, cancellationToken);
            var policy = PayPolicy.For(target.Region);
            var yearStart = target.Region == Region.IN
                ? new DateOnly(clock.Today.Month >= 4 ? clock.Today.Year : clock.Today.Year - 1, 4, 1)
                : new DateOnly(clock.Today.Year, 1, 1);
            var accrued = LeaveAccrualEngine.AccruedDays(policy, target.JoinDate, clock.Today, yearStart);
            return (object)new LeaveBalanceResult(
                target.Id, target.Name, target.Region,
                balances.Select(b => new LeaveBalanceLine(b.Type, b.Days, b.UsedThisYear)).ToList(),
                accrued,
                $"Annual leave accrues {policy.AnnualLeaveAccrualPerMonth} days/month; carry-forward cap {policy.AnnualLeaveCarryForwardCap} days.");
        }, cancellationToken);

    [McpServerTool(Name = "get_my_shifts", ReadOnly = true, Idempotent = true)]
    [Description("List scheduled shifts for yourself (or a direct report if you are a manager) in a date range. Defaults to the next 14 days.")]
    public Task<object> GetShiftsAsync(
        [Description("Employee id like E004. Leave empty for yourself.")] string? employeeId = null,
        [Description("Start date, yyyy-MM-dd. Defaults to today.")] string? fromDate = null,
        [Description("End date inclusive, yyyy-MM-dd. Defaults to 14 days after fromDate.")] string? toDate = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("get_my_shifts", new { employeeId, fromDate, toDate }, async () =>
        {
            var target = await ResolveTargetAsync(employeeId, cancellationToken);
            var from = ToolGuards.OptionalDate(fromDate, nameof(fromDate)) ?? clock.Today;
            var to = ToolGuards.OptionalDate(toDate, nameof(toDate)) ?? from.AddDays(14);
            if (to < from)
            {
                throw new ToolValidationException("toDate must be on or after fromDate");
            }
            if (to.DayNumber - from.DayNumber > 62)
            {
                throw new ToolValidationException("date range must be 62 days or fewer");
            }
            var shifts = await store.GetShiftsAsync(target.Id, from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue), cancellationToken);
            var weekly = shifts.GroupBy(s => s.IsoWeek)
                .Select(g => new WeekTotal(g.Key.Week, g.Sum(s => s.Hours), target.MaxWeeklyHours))
                .OrderBy(w => w.IsoWeek).ToList();
            return (object)new ShiftsResult(target.Id, target.Name, from, to, shifts.Select(Line).ToList(), weekly);
        }, cancellationToken);

    [McpServerTool(Name = "find_swap_candidates", ReadOnly = true, Idempotent = true)]
    [Description("Find colleagues who could take one of your shifts: same team, required skills, under their weekly-hours cap, at least 11h rest, not on leave. Explains why others are not eligible.")]
    public Task<object> FindSwapCandidatesAsync(
        [Description("Shift id like S0042. Must be your own shift (or a report's, for managers).")] string shiftId,
        [Description("How many candidates to return (1-20). Default 5.")] int? top = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("find_swap_candidates", new { shiftId, top }, async () =>
        {
            var id = ToolGuards.ShiftId(shiftId, nameof(shiftId));
            var shift = await store.GetShiftAsync(id, cancellationToken)
                ?? throw new ToolValidationException($"shift {id} does not exist");
            await AuthorizeShiftAsync(shift, cancellationToken);
            var roster = await store.LoadRosterAsync(cancellationToken);
            var finder = new SwapCandidateFinder(roster);
            var all = finder.EvaluateAll(shift);
            var best = finder.FindBest(shift, top: ToolGuards.Top(top));
            var holder = shift.EmployeeId is null ? "(unassigned)" : roster.Employee(shift.EmployeeId)?.Name ?? shift.EmployeeId;
            return (object)new SwapCandidatesResult(
                shift.Id, shift.Date, Hm(shift.Start), Hm(shift.End), holder,
                best.Select(c => new CandidateLine(c.Employee.Id, c.Employee.Name, c.WeeklyHoursBefore, c.WeeklyHoursAfter, c.Employee.SkillIds.Order(StringComparer.Ordinal).ToList())).ToList(),
                all.Where(c => !c.Eligible).Take(5).Select(c => new RejectedLine(c.Employee.Id, c.Employee.Name, c.Violations)).ToList(),
                best.Count == 0 ? "No eligible colleague right now; a manager may need to use the coverage solver." : "Use request_shift_swap with one of the candidate ids to create a request.");
        }, cancellationToken);

    [McpServerTool(Name = "calculate_overtime_pay", ReadOnly = true, Idempotent = true)]
    [Description("Calculate regular and overtime pay for one ISO week using the region's rules (India: 2x beyond 9h/day or 48h/week; US: 1.5x beyond 40h/week). Yourself, or a direct report if you are a manager.")]
    public Task<object> CalculateOvertimePayAsync(
        [Description("Employee id like E004. Leave empty for yourself.")] string? employeeId = null,
        [Description("Any date inside the week, yyyy-MM-dd. Defaults to the current week.")] string? dateInWeek = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("calculate_overtime_pay", new { employeeId, dateInWeek }, async () =>
        {
            var target = await ResolveTargetAsync(employeeId, cancellationToken);
            var day = ToolGuards.OptionalDate(dateInWeek, nameof(dateInWeek)) ?? clock.Today;
            var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            var shifts = await store.GetShiftsAsync(target.Id, monday.ToDateTime(TimeOnly.MinValue), monday.AddDays(7).ToDateTime(TimeOnly.MinValue), cancellationToken);
            var b = OvertimePolicies.For(target.Region).Calculate(shifts, target.HourlyRate);
            return (object)new OvertimeResult(target.Id, target.Name, monday, b.Region, b.Currency, b.TotalHours, b.RegularHours, b.OvertimeHours, b.RegularPay, b.OvertimePay, b.TotalPay, b.Notes);
        }, cancellationToken);

    [McpServerTool(Name = "get_team_coverage", ReadOnly = true, Idempotent = true)]
    [Description("Show staffing per day and slot for a team against its minimum headcount, list gaps and open shifts, and suggest who could fill the open shifts. Managers only, for their own team.")]
    public Task<object> GetTeamCoverageAsync(
        [Description("Team id like T-BLR. Leave empty for your own team.")] string? teamId = null,
        [Description("Start date, yyyy-MM-dd. Defaults to today.")] string? fromDate = null,
        [Description("End date inclusive, yyyy-MM-dd. Defaults to 7 days after fromDate.")] string? toDate = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("get_team_coverage", new { teamId, fromDate, toDate }, async () =>
        {
            var me = caller.Current;
            if (!me.IsManager)
            {
                throw new ToolAuthorizationException("only managers can view team coverage");
            }
            var tid = teamId is null ? me.TeamId : ToolGuards.TeamId(teamId, nameof(teamId));
            var team = await store.GetTeamAsync(tid, cancellationToken) ?? throw new ToolValidationException($"team {tid} does not exist");
            if (team.Region != me.Region)
            {
                throw new ToolAuthorizationException($"you may only view coverage for teams in {me.Region}");
            }
            var from = ToolGuards.OptionalDate(fromDate, nameof(fromDate)) ?? clock.Today;
            var to = ToolGuards.OptionalDate(toDate, nameof(toDate)) ?? from.AddDays(7);
            if (to < from || to.DayNumber - from.DayNumber > 31)
            {
                throw new ToolValidationException("date range must be between 0 and 31 days");
            }
            var shifts = await store.GetTeamShiftsAsync(tid, from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue), cancellationToken);
            var slots = shifts.GroupBy(s => (s.Date, Slot: Hm(s.Start)))
                .Select(g => new CoverageSlot(g.Key.Date, g.Key.Slot, g.Count(s => s.IsAssigned), team.MinHeadcountPerSlot, g.Count(s => s.IsAssigned) >= team.MinHeadcountPerSlot))
                .OrderBy(c => c.Date).ThenBy(c => c.Slot, StringComparer.Ordinal).ToList();
            var open = shifts.Where(s => !s.IsAssigned).ToList();
            var roster = await store.LoadRosterAsync(cancellationToken);
            var solved = new CoverageSolver(roster).Solve(open);
            var suggestions = open.Select(s =>
            {
                var emp = solved.Assignments.TryGetValue(s.Id, out var eid) ? roster.Employee(eid) : null;
                return new OpenShiftSuggestion(s.Id, s.Date, Hm(s.Start), emp?.Id, emp?.Name);
            }).ToList();
            return (object)new CoverageResultDto(team.Id, team.Name, from, to, team.MinHeadcountPerSlot, slots, slots.Where(s => !s.Covered).ToList(), suggestions, solved.Strategy);
        }, cancellationToken);

    // --- write tools (PENDING only) -----------------------------------------------------------

    [McpServerTool(Name = "request_leave", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Submit a leave request for yourself. Creates a PENDING request that a manager must approve; nothing is deducted until then.")]
    public Task<object> RequestLeaveAsync(
        [Description("Annual, Sick or Casual (Casual is India only).")] string leaveType,
        [Description("First day, yyyy-MM-dd.")] string startDate,
        [Description("Last day inclusive, yyyy-MM-dd.")] string endDate,
        [Description("Optional short reason.")] string? reason = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("request_leave", new { leaveType, startDate, endDate, reason }, async () =>
        {
            var me = caller.Current;
            var type = ToolGuards.LeaveType(leaveType);
            var start = ToolGuards.Date(startDate, nameof(startDate));
            var end = ToolGuards.Date(endDate, nameof(endDate));
            var why = ToolGuards.Reason(reason);
            if (end < start)
            {
                throw new ToolValidationException("endDate must be on or after startDate");
            }
            if (start < clock.Today)
            {
                throw new ToolValidationException("leave cannot start in the past");
            }
            if (end.DayNumber - start.DayNumber > 60)
            {
                throw new ToolValidationException("a single request may cover at most 60 days");
            }
            if (type == LeaveType.Casual && me.Region != Region.IN)
            {
                throw new ToolValidationException("casual leave exists only in the India policy");
            }
            var days = LeaveAccrualEngine.WorkingDays(start, end);
            if (days == 0)
            {
                throw new ToolValidationException("the range contains no working days (Mon-Fri)");
            }
            var balance = (await store.GetLeaveBalancesAsync(me.EmployeeId, cancellationToken)).FirstOrDefault(b => b.Type == type);
            var request = await store.CreateLeaveRequestAsync(me.EmployeeId, type, start, end, days, why, cancellationToken);
            var warning = balance is null || balance.Days < days
                ? $" Warning: current {type} balance is {balance?.Days ?? 0} days, which is less than {days}; the manager will likely reject."
                : string.Empty;
            return (object)new RequestCreatedResult(
                request.Id, "leave", "pending",
                $"{type} leave {start:yyyy-MM-dd} to {end:yyyy-MM-dd} ({days} working days).{warning}",
                "Your manager will see this in the approval queue. Nothing changes until it is approved.");
        }, cancellationToken);

    [McpServerTool(Name = "request_shift_swap", ReadOnly = false, Idempotent = false, Destructive = false)]
    [Description("Ask to hand one of your shifts to a named colleague. The colleague is checked against all scheduling rules first; if eligible, a PENDING request is created for a manager to approve.")]
    public Task<object> RequestShiftSwapAsync(
        [Description("Your shift id like S0042.")] string shiftId,
        [Description("Colleague's employee id like E007 (use find_swap_candidates first).")] string targetEmployeeId,
        [Description("Optional short reason.")] string? reason = null,
        CancellationToken cancellationToken = default) =>
        AuditedAsync("request_shift_swap", new { shiftId, targetEmployeeId, reason }, async () =>
        {
            var me = caller.Current;
            var sid = ToolGuards.ShiftId(shiftId, nameof(shiftId));
            var tid = ToolGuards.EmployeeId(targetEmployeeId, nameof(targetEmployeeId));
            var why = ToolGuards.Reason(reason);
            var shift = await store.GetShiftAsync(sid, cancellationToken) ?? throw new ToolValidationException($"shift {sid} does not exist");
            if (shift.EmployeeId != me.EmployeeId)
            {
                throw new ToolAuthorizationException($"shift {sid} is not yours; you can only swap your own shifts");
            }
            if (tid == me.EmployeeId)
            {
                throw new ToolValidationException("you cannot swap a shift with yourself");
            }
            if (shift.Start < clock.UtcNow)
            {
                throw new ToolValidationException("that shift has already started");
            }
            var roster = await store.LoadRosterAsync(cancellationToken);
            var target = roster.Employee(tid) ?? throw new ToolValidationException($"employee {tid} does not exist");
            if (target.TeamId != shift.TeamId)
            {
                throw new ToolValidationException($"{target.Name} is not on team {shift.TeamId}");
            }
            var eval = new SwapCandidateFinder(roster).Evaluate(target, shift);
            if (!eval.Eligible)
            {
                return (object)new ToolError("not_eligible", $"{target.Name} cannot take {sid}: {string.Join("; ", eval.Violations)}");
            }
            var request = await store.CreateSwapRequestAsync(me.EmployeeId, sid, tid, why, cancellationToken);
            return (object)new RequestCreatedResult(
                request.Id, "swap", "pending",
                $"Hand {sid} ({shift.Date:yyyy-MM-dd} {Hm(shift.Start)}-{Hm(shift.End)}) to {target.Name} ({tid}); they would be at {eval.WeeklyHoursAfter:0.#}h that week.",
                "A manager must approve; the rules are re-checked at approval time.");
        }, cancellationToken);

    // --- authorization ------------------------------------------------------------------------

    /// <summary>"Me" if no id was given; otherwise the id must be the caller or one of their reports.</summary>
    private async Task<Employee> ResolveTargetAsync(string? employeeId, CancellationToken ct)
    {
        var me = caller.Current;
        if (string.IsNullOrWhiteSpace(employeeId) || string.Equals(employeeId.Trim(), me.EmployeeId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(employeeId.Trim(), "me", StringComparison.OrdinalIgnoreCase))
        {
            return await store.GetEmployeeAsync(me.EmployeeId, ct)
                ?? throw new ToolAuthorizationException($"caller {me.EmployeeId} is not a known employee");
        }
        var id = ToolGuards.EmployeeId(employeeId, nameof(employeeId));
        if (!me.IsManager)
        {
            throw new ToolAuthorizationException($"you can only view your own data, not {id}'s");
        }
        var target = await store.GetEmployeeAsync(id, ct) ?? throw new ToolValidationException($"employee {id} does not exist");
        if (!await ManagesAsync(me.EmployeeId, target, ct))
        {
            throw new ToolAuthorizationException($"{id} does not report to you");
        }
        return target;
    }

    private async Task AuthorizeShiftAsync(Shift shift, CancellationToken ct)
    {
        var me = caller.Current;
        if (shift.EmployeeId == me.EmployeeId)
        {
            return;
        }
        if (!me.IsManager)
        {
            throw new ToolAuthorizationException($"shift {shift.Id} is not yours");
        }
        var team = await store.GetTeamAsync(shift.TeamId, ct);
        if (team is null || team.Region != me.Region)
        {
            throw new ToolAuthorizationException($"shift {shift.Id} is outside your region");
        }
        if (shift.EmployeeId is not null)
        {
            var holder = await store.GetEmployeeAsync(shift.EmployeeId, ct);
            if (holder is null || !await ManagesAsync(me.EmployeeId, holder, ct))
            {
                throw new ToolAuthorizationException($"the holder of {shift.Id} does not report to you");
            }
        }
    }

    private async Task<bool> ManagesAsync(string managerId, Employee target, CancellationToken ct)
    {
        var cursor = target;
        for (var depth = 0; depth < 16 && cursor.ManagerId is not null; depth++)
        {
            if (cursor.ManagerId == managerId)
            {
                return true;
            }
            cursor = await store.GetEmployeeAsync(cursor.ManagerId, ct) ?? throw new ToolValidationException("broken reporting chain");
        }
        return false;
    }

    // --- audit wrapper ------------------------------------------------------------------------

    private async Task<object> AuditedAsync(string tool, object args, Func<Task<object>> body, CancellationToken ct)
    {
        var me = caller.Current;
        var sw = Stopwatch.StartNew();
        var authorized = true;
        var succeeded = false;
        string? summary = null;
        object result;
        try
        {
            result = await body();
            succeeded = result is not ToolError;
            summary = result is ToolError te ? $"{te.Code}: {te.Message}" : result.GetType().Name;
        }
        catch (ToolAuthorizationException ex)
        {
            authorized = false;
            summary = ex.Message;
            result = new ToolError("not_authorized", ex.Message);
        }
        catch (ToolValidationException ex)
        {
            summary = ex.Message;
            result = new ToolError("invalid_argument", ex.Message);
        }
        finally
        {
            sw.Stop();
            await store.AddAuditAsync(new AuditEntry
            {
                Timestamp = clock.UtcNow,
                ActorId = me.EmployeeId,
                ActorRole = me.Role.ToString().ToLowerInvariant(),
                Tool = tool,
                ArgumentsJson = JsonSerializer.Serialize(args, ArgsJson),
                Authorized = authorized,
                Succeeded = succeeded,
                ResultSummary = summary,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                ConversationId = me.ConversationId,
            }, ct);
        }
        return result;
    }

    private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static ShiftLine Line(Shift s) => new(
        s.Id, s.Date, s.Start.DayOfWeek.ToString(), Hm(s.Start), Hm(s.End), s.Hours, s.TeamId,
        s.RequiredSkillIds.Order(StringComparer.Ordinal).ToList());
}
