using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShiftSense.Agent;
using ShiftSense.Agent.Llm;
using ShiftSense.Agent.Persistence;
using ShiftSense.Agent.Tools;
using ShiftSense.Domain;
using ShiftSense.Scheduling;
using static JsonOpts;

// Scenario eval: runs eval/scenarios.json through the real agent (fake model by default, a real
// model when LLM_API_KEY is set) against a fresh seeded database, and reports tool-selection
// accuracy and task success. Numbers in the README come only from this program's output.
//
//   dotnet run --project eval/ShiftSense.Eval              # fake model, offline
//   LLM_API_KEY=... dotnet run --project eval/ShiftSense.Eval   # real model

var root = FindRoot();
var scenariosPath = Path.Combine(root, "eval", "scenarios.json");
var resultsDir = Path.Combine(root, "eval", "results");
var dbPath = Path.Combine(Path.GetTempPath(), $"shiftsense-eval-{Guid.NewGuid():N}.db");

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["SHIFTSENSE_DB"] = dbPath })
    .Build();
var services = new ServiceCollection().AddLogging().AddShiftSense(config).BuildServiceProvider();
await services.InitializeShiftSenseAsync();
var llm = services.GetRequiredService<LlmOptions>();

var doc = JsonDocument.Parse(await File.ReadAllTextAsync(scenariosPath));
var scenarios = doc.RootElement.GetProperty("scenarios").EnumerateArray().Select(Scenario.From).ToList();

RosterSnapshot roster;
using (var scope = services.CreateScope())
{
    roster = await scope.ServiceProvider.GetRequiredService<ShiftSenseStore>().LoadRosterAsync();
}

var rows = new List<Row>();
foreach (var s in scenarios)
{
    var (message, skipReason) = Resolve(s.Message, roster);
    if (skipReason is not null)
    {
        rows.Add(new Row(s, message, [], "skipped", false, false, 0, 0, 0, skipReason));
        continue;
    }
    using var scope = services.CreateScope();
    var sp = scope.ServiceProvider;
    var store = sp.GetRequiredService<ShiftSenseStore>();
    var emp = await store.GetEmployeeAsync(s.Employee) ?? throw new InvalidOperationException($"{s.Id}: unknown employee {s.Employee}");
    sp.GetRequiredService<CallerAccessor>().Current = new CallerContext(emp.Id, emp.Name, emp.Role, emp.Region, emp.TeamId, $"eval-{s.Id}");
    var agent = sp.GetRequiredService<ShiftSenseAgent>();

    AgentReply reply;
    try
    {
        reply = await agent.ChatAsync(message);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        rows.Add(new Row(s, message, [], "exception", false, false, 0, 0, 0, ex.GetType().Name + ": " + ex.Message));
        continue;
    }

    var tools = reply.ToolCalls.Select(t => t.Tool).ToList();
    var toolOk = tools.ToHashSet(StringComparer.Ordinal).SetEquals(s.ExpectedTools);
    var (observed, outcomeOk) = Judge(s.Outcome, reply);
    rows.Add(new Row(s, message, tools, observed, toolOk, outcomeOk, reply.LatencyMs, reply.PromptTokens ?? 0, reply.CompletionTokens ?? 0, null));
    Console.WriteLine($"{s.Id,-7} tools={(toolOk ? "ok " : "MISS")} outcome={(outcomeOk ? "ok " : "MISS")}  [{string.Join(",", tools)}] -> {observed}");
}

var md = Report(rows, llm);
Directory.CreateDirectory(resultsDir);
var provider = llm.IsFake ? "fake" : "llm";
await File.WriteAllTextAsync(Path.Combine(resultsDir, $"results_{provider}.md"), md, new UTF8Encoding(false));
await File.WriteAllTextAsync(
    Path.Combine(resultsDir, $"results_{provider}.json"),
    JsonSerializer.Serialize(rows.Select(r => new { r.Scenario.Id, r.Scenario.Category, r.Message, r.Tools, r.Observed, r.ToolOk, r.OutcomeOk, r.LatencyMs, r.PromptTokens, r.CompletionTokens, r.Note }), Pretty),
    new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine(md);
try
{
    File.Delete(dbPath);
}
catch (IOException)
{
}
return rows.Any(r => r.Observed == "exception") ? 1 : 0;

// ---------------------------------------------------------------------------------------------

static (string Message, string? Skip) Resolve(string message, RosterSnapshot roster)
{
    var finder = new SwapCandidateFinder(roster);
    string? skip = null;
    var resolved = Regex.Replace(message, @"\{\{(shift|candidate):(E\d{3})\}\}", m =>
    {
        var emp = m.Groups[2].Value;
        var shifts = roster.ShiftsOf(emp).Where(s => s.Start > SeedData.DemoNow).OrderBy(s => s.Start).ToList();
        if (m.Groups[1].Value == "shift")
        {
            if (shifts.Count == 0)
            {
                skip = $"{emp} has no upcoming shift";
                return m.Value;
            }
            return shifts[0].Id;
        }
        foreach (var sh in shifts)
        {
            var best = finder.FindBest(sh, top: 1);
            if (best.Count > 0)
            {
                return best[0].Employee.Id;
            }
        }
        skip = $"no eligible swap candidate for any upcoming shift of {emp}";
        return m.Value;
    });
    return (resolved, skip);
}

static (string Observed, bool Ok) Judge(string expected, AgentReply reply)
{
    var results = reply.ToolCalls.Select(t => JsonDocument.Parse(t.ResultJson).RootElement).ToList();
    string? Code(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True
            ? e.GetProperty("code").GetString()
            : null;
    var codes = results.Select(Code).Where(c => c is not null).ToList();
    string observed;
    if (reply.ToolCalls.Count == 0)
    {
        observed = "no_tool";
    }
    else if (codes.Contains("not_authorized"))
    {
        observed = "refused";
    }
    else if (codes.Contains("invalid_argument"))
    {
        observed = "invalid";
    }
    else if (codes.Contains("not_eligible"))
    {
        observed = "not_eligible";
    }
    else if (results.Any(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("status", out var st) && st.GetString() == "pending"))
    {
        observed = "pending";
    }
    else
    {
        observed = string.IsNullOrWhiteSpace(reply.Text) ? "empty" : "answer";
    }
    return (observed, observed == expected);
}

static string Report(List<Row> rows, LlmOptions llm)
{
    var scored = rows.Where(r => r.Observed is not "skipped").ToList();
    var sb = new StringBuilder();
    sb.AppendLine(CultureInfo.InvariantCulture, $"# ShiftSense scenario eval — provider `{llm.Provider}`{(llm.IsFake ? "" : $" ({llm.Model})")}");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"Run at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} over {rows.Count} scenarios ({scored.Count} scored, {rows.Count - scored.Count} skipped).");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"- **Tool-selection accuracy:** {Pct(scored.Count(r => r.ToolOk), scored.Count)} ({scored.Count(r => r.ToolOk)}/{scored.Count})");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- **Task success:** {Pct(scored.Count(r => r.OutcomeOk), scored.Count)} ({scored.Count(r => r.OutcomeOk)}/{scored.Count})");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- **Avg latency:** {scored.Average(r => r.LatencyMs):0} ms · **avg tokens:** {scored.Average(r => r.PromptTokens):0} in / {scored.Average(r => r.CompletionTokens):0} out");
    sb.AppendLine();
    sb.AppendLine("| category | n | tool-selection | task success |");
    sb.AppendLine("|---|---:|---:|---:|");
    foreach (var g in scored.GroupBy(r => r.Scenario.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {Pct(g.Count(r => r.ToolOk), g.Count())} | {Pct(g.Count(r => r.OutcomeOk), g.Count())} |");
    }
    sb.AppendLine();
    sb.AppendLine("| id | employee | message | expected tools | actual tools | expected | observed | ok |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|");
    foreach (var r in rows)
    {
        var ok = r.Observed == "skipped" ? "skip" : (r.ToolOk && r.OutcomeOk ? "✓" : "✗");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Scenario.Id} | {r.Scenario.Employee} | {r.Message.Replace("|", "\\|", StringComparison.Ordinal)} | {string.Join(", ", r.Scenario.ExpectedTools)} | {string.Join(", ", r.Tools)} | {r.Scenario.Outcome} | {r.Observed}{(r.Note is null ? "" : " — " + r.Note)} | {ok} |");
    }
    sb.AppendLine();
    if (llm.IsFake)
    {
        sb.AppendLine("**Fake-model caveat.** The fake chat client picks tools by keyword rules, so these numbers validate the agent loop, tools, authorization and approval paths end to end — not a model's ability to choose tools. Run with `LLM_API_KEY` set for real numbers.");
    }
    return sb.ToString();
}

static string Pct(int n, int d) => d == 0 ? "–" : (100.0 * n / d).ToString("0.#", CultureInfo.InvariantCulture) + "%";

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShiftSense.sln")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("could not find ShiftSense.sln above " + AppContext.BaseDirectory);
}

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
}

internal sealed record Scenario(string Id, string Category, string Employee, string Message, HashSet<string> ExpectedTools, string Outcome)
{
    public static Scenario From(JsonElement e) => new(
        e.GetProperty("id").GetString()!,
        e.GetProperty("category").GetString()!,
        e.GetProperty("employee").GetString()!,
        e.GetProperty("message").GetString()!,
        e.GetProperty("expected_tools").EnumerateArray().Select(t => t.GetString()!).ToHashSet(StringComparer.Ordinal),
        e.GetProperty("outcome").GetString()!);
}

internal sealed record Row(Scenario Scenario, string Message, List<string> Tools, string Observed, bool ToolOk, bool OutcomeOk, double LatencyMs, long PromptTokens, long CompletionTokens, string? Note);
