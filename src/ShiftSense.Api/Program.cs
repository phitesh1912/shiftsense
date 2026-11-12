using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Trace;
using ShiftSense.Agent;
using ShiftSense.Agent.Llm;
using ShiftSense.Agent.Persistence;
using ShiftSense.Agent.Tools;
using ShiftSense.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddShiftSense(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// Traces for agent runs and every model call (token usage, latency, tool calls). The console
// exporter is opt-in so logs stay readable; point OTEL_EXPORTER_OTLP_ENDPOINT at a collector
// in production.
if (string.Equals(builder.Configuration["OTEL_CONSOLE"], "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddOpenTelemetry().WithTracing(t => t
        .AddSource(ChatClientFactory.ActivitySourceName, "Microsoft.Extensions.AI", "Experimental.Microsoft.Extensions.AI", "*Microsoft.Agents.AI*")
        .AddConsoleExporter());
}

var app = builder.Build();
await app.Services.InitializeShiftSenseAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- identity (demo) ---------------------------------------------------------------------------
// The caller is identified by the X-Employee-Id header. In production this comes from the
// verified bearer token; everything downstream (CallerContext, tools, approvals) is unchanged.

static async Task<IResult?> ResolveCallerAsync(HttpContext http, ShiftSenseStore store, CallerAccessor accessor, string? conversationId)
{
    var id = http.Request.Headers["X-Employee-Id"].FirstOrDefault()?.Trim().ToUpperInvariant();
    if (string.IsNullOrEmpty(id))
    {
        return Results.Problem("missing X-Employee-Id header", statusCode: StatusCodes.Status401Unauthorized);
    }
    var emp = await store.GetEmployeeAsync(id, http.RequestAborted);
    if (emp is null)
    {
        return Results.Problem($"unknown employee {id}", statusCode: StatusCodes.Status401Unauthorized);
    }
    var conv = string.IsNullOrWhiteSpace(conversationId)
        ? http.Request.Headers["X-Conversation-Id"].FirstOrDefault() ?? "default"
        : conversationId.Trim();
    if (conv.Length > 64)
    {
        return Results.Problem("conversationId too long", statusCode: StatusCodes.Status400BadRequest);
    }
    accessor.Current = new CallerContext(emp.Id, emp.Name, emp.Role, emp.Region, emp.TeamId, conv);
    return null;
}

static IResult? RequireManager(CallerAccessor accessor) =>
    accessor.Current.IsManager ? null : Results.Problem("managers only", statusCode: StatusCodes.Status403Forbidden);

// --- endpoints ---------------------------------------------------------------------------------

app.MapGet("/health", (LlmOptions llm, IClock clock) => Results.Ok(new
{
    status = "ok",
    provider = llm.Provider,
    model = llm.IsFake ? "fake-rules-v1" : llm.Model,
    endpoint = llm.IsFake ? null : llm.Endpoint,
    now = clock.UtcNow,
    tools = ToolCatalog.Names,
}));

app.MapGet("/employees", async (ShiftSenseStore store) =>
{
    // Demo identity picker. Names only; no pay or balances.
    var roster = await store.LoadRosterAsync();
    return Results.Ok(roster.Employees.OrderBy(e => e.Id, StringComparer.Ordinal)
        .Select(e => new { e.Id, e.Name, Role = e.Role.ToString().ToLowerInvariant(), e.Region, e.TeamId }));
});

app.MapPost("/chat", async (HttpContext http, ChatRequest req, ShiftSenseStore store, CallerAccessor accessor, ShiftSenseAgent agent) =>
{
    if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 2000)
    {
        return Results.Problem("message must be 1-2000 characters", statusCode: StatusCodes.Status400BadRequest);
    }
    if (await ResolveCallerAsync(http, store, accessor, req.ConversationId) is { } fail)
    {
        return fail;
    }
    var reply = await agent.ChatAsync(req.Message, http.RequestAborted);
    return Results.Ok(reply);
});

app.MapGet("/requests/pending", async (HttpContext http, ShiftSenseStore store, CallerAccessor accessor) =>
{
    if (await ResolveCallerAsync(http, store, accessor, null) is { } fail)
    {
        return fail;
    }
    if (RequireManager(accessor) is { } forbidden)
    {
        return forbidden;
    }
    var me = accessor.Current;
    var pending = await store.GetPendingAsync(http.RequestAborted);
    var leave = new List<object>();
    foreach (var l in pending.Leave)
    {
        if (await ReportingLine.ManagesAsync(store, me.EmployeeId, l.EmployeeId, http.RequestAborted))
        {
            var emp = await store.GetEmployeeAsync(l.EmployeeId, http.RequestAborted);
            leave.Add(new { l.Id, Kind = "leave", l.EmployeeId, EmployeeName = emp?.Name, Type = l.Type.ToString(), l.StartDate, l.EndDate, l.Days, l.Reason, l.CreatedAt });
        }
    }
    var swaps = new List<object>();
    foreach (var s in pending.Swaps)
    {
        if (await ReportingLine.ManagesAsync(store, me.EmployeeId, s.RequesterId, http.RequestAborted))
        {
            var shift = await store.GetShiftAsync(s.ShiftId, http.RequestAborted);
            var from = await store.GetEmployeeAsync(s.RequesterId, http.RequestAborted);
            var to = await store.GetEmployeeAsync(s.TargetEmployeeId, http.RequestAborted);
            swaps.Add(new { s.Id, Kind = "swap", s.RequesterId, RequesterName = from?.Name, s.TargetEmployeeId, TargetName = to?.Name, s.ShiftId, ShiftStart = shift?.Start, ShiftEnd = shift?.End, s.Reason, s.CreatedAt });
        }
    }
    return Results.Ok(new { leave, swaps });
});

async Task<IResult> DecideAsync(HttpContext http, string id, bool approve, DecisionRequest? body, ShiftSenseStore store, CallerAccessor accessor, IClock clock)
{
    if (await ResolveCallerAsync(http, store, accessor, null) is { } fail)
    {
        return fail;
    }
    if (RequireManager(accessor) is { } forbidden)
    {
        return forbidden;
    }
    var me = accessor.Current;
    id = id.Trim().ToUpperInvariant();
    string requesterId;
    if (id.StartsWith("LR-", StringComparison.Ordinal))
    {
        var r = await store.GetLeaveRequestAsync(id, http.RequestAborted);
        if (r is null)
        {
            return Results.NotFound(new { message = $"{id} not found" });
        }
        requesterId = r.EmployeeId;
    }
    else if (id.StartsWith("SW-", StringComparison.Ordinal))
    {
        var r = await store.GetSwapRequestAsync(id, http.RequestAborted);
        if (r is null)
        {
            return Results.NotFound(new { message = $"{id} not found" });
        }
        requesterId = r.RequesterId;
    }
    else
    {
        return Results.BadRequest(new { message = "id must start with LR- or SW-" });
    }

    var authorized = await ReportingLine.ManagesAsync(store, me.EmployeeId, requesterId, http.RequestAborted);
    Decision decision = authorized
        ? id.StartsWith("LR-", StringComparison.Ordinal)
            ? await store.DecideLeaveAsync(id, approve, me.EmployeeId, body?.Note, http.RequestAborted)
            : await store.DecideSwapAsync(id, approve, me.EmployeeId, body?.Note, http.RequestAborted)
        : new Decision(false, $"{requesterId} does not report to you");

    await store.AddAuditAsync(new AuditEntry
    {
        Timestamp = clock.UtcNow,
        ActorId = me.EmployeeId,
        ActorRole = "manager",
        Tool = approve ? "approve_request" : "reject_request",
        ArgumentsJson = JsonSerializer.Serialize(new { id, body?.Note }),
        Authorized = authorized,
        Succeeded = decision.Applied,
        ResultSummary = decision.Message,
        LatencyMs = 0,
    }, http.RequestAborted);

    if (!authorized)
    {
        return Results.Problem(decision.Message, statusCode: StatusCodes.Status403Forbidden);
    }
    return decision.Applied ? Results.Ok(new { id, decision.Message }) : Results.Conflict(new { id, decision.Message });
}

app.MapPost("/requests/{id}/approve", (HttpContext http, string id, DecisionRequest? body, ShiftSenseStore store, CallerAccessor accessor, IClock clock) =>
    DecideAsync(http, id, true, body, store, accessor, clock));
app.MapPost("/requests/{id}/reject", (HttpContext http, string id, DecisionRequest? body, ShiftSenseStore store, CallerAccessor accessor, IClock clock) =>
    DecideAsync(http, id, false, body, store, accessor, clock));

app.MapGet("/audit", async (HttpContext http, [FromQuery] int? take, ShiftSenseStore store, CallerAccessor accessor) =>
{
    if (await ResolveCallerAsync(http, store, accessor, null) is { } fail)
    {
        return fail;
    }
    if (RequireManager(accessor) is { } forbidden)
    {
        return forbidden;
    }
    var rows = await store.RecentAuditAsync(Math.Clamp(take ?? 50, 1, 500), http.RequestAborted);
    return Results.Ok(rows);
});

app.Run();

internal sealed record ChatRequest(string Message, string? ConversationId);

internal sealed record DecisionRequest(string? Note);

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program
{
}
