using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ShiftSense.Agent.Llm;

/// <summary>
/// Deterministic stand-in for a model. It plays the model's role in the tool-calling loop:
/// on a user turn it picks a tool by keyword rules and emits a <see cref="FunctionCallContent"/>;
/// when the last message is a tool result it writes a short natural-language summary. This is
/// what lets the whole agent — tools, authorization, audit, approval flow — run in CI with no key.
/// It is intentionally simple; the scenario eval measures the real model against the same rules.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("fake", null, "fake-rules-v1");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var last = list.LastOrDefault();
        ChatMessage reply;
        if (last is not null && last.Role == ChatRole.Tool)
        {
            reply = new ChatMessage(ChatRole.Assistant, Summarize(list));
        }
        else
        {
            var userText = last?.Text ?? string.Empty;
            reply = Decide(userText, options);
        }
        var response = new ChatResponse(reply)
        {
            ResponseId = Guid.NewGuid().ToString("N"),
            Usage = new UsageDetails
            {
                InputTokenCount = list.Sum(m => Math.Max(1, (m.Text?.Length ?? 0) / 4)),
                OutputTokenCount = Math.Max(1, (reply.Text?.Length ?? 0) / 4),
            },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }
        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    // --- intent rules --------------------------------------------------------------------------

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex DateRx();

    [GeneratedRegex(@"\bE\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmpRx();

    [GeneratedRegex(@"\bS\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShiftRx();

    [GeneratedRegex(@"\bT-[A-Z]{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex TeamRx();

    private static ChatMessage Decide(string text, ChatOptions? options)
    {
        var t = text.ToLowerInvariant();
        var dates = DateRx().Matches(text).Select(m => m.Value).ToList();
        var emp = EmpRx().Match(text) is { Success: true } em ? em.Value.ToUpperInvariant() : null;
        var shift = ShiftRx().Match(text) is { Success: true } sm ? sm.Value.ToUpperInvariant() : null;
        var team = TeamRx().Match(text) is { Success: true } tm ? tm.Value.ToUpperInvariant() : null;
        var available = options?.Tools?.OfType<AIFunction>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal) ?? [];

        if (Has(t, "approve", "reject", "authorise", "authorize"))
        {
            return Text("I can't approve or reject requests — that's done by a manager in the approval queue. I can submit a request for you, which will show up there as pending.");
        }

        if (Has(t, "swap", "take my shift", "cover my shift", "give my shift", "hand over"))
        {
            if (shift is not null && emp is not null)
            {
                return Call(available, "request_shift_swap", new() { ["shiftId"] = shift, ["targetEmployeeId"] = emp, ["reason"] = Reason(text) });
            }
            if (shift is not null)
            {
                return Call(available, "find_swap_candidates", new() { ["shiftId"] = shift });
            }
            return Call(available, "get_my_shifts", new());
        }

        if (Has(t, "coverage", "staffing", "understaffed", "gaps", "who is working"))
        {
            var args = new Dictionary<string, object?>();
            if (team is not null)
            {
                args["teamId"] = team;
            }
            if (dates.Count > 0)
            {
                args["fromDate"] = dates[0];
            }
            if (dates.Count > 1)
            {
                args["toDate"] = dates[1];
            }
            return Call(available, "get_team_coverage", args);
        }

        if (Has(t, "overtime", "how much will i be paid", "pay for", "earn"))
        {
            var args = new Dictionary<string, object?>();
            if (emp is not null)
            {
                args["employeeId"] = emp;
            }
            if (dates.Count > 0)
            {
                args["dateInWeek"] = dates[0];
            }
            return Call(available, "calculate_overtime_pay", args);
        }

        var leaveWord = Has(t, "leave", "sick", "vacation", "pto", "day off", "days off", "time off", "holiday");
        var requestWord = Has(t, "request", "apply", "book", "take", "want", "need", "put in", "submit", "file");
        if (leaveWord && requestWord && dates.Count > 0)
        {
            var type = Has(t, "sick", "ill", "unwell") ? "Sick" : Has(t, "casual") ? "Casual" : "Annual";
            return Call(available, "request_leave", new()
            {
                ["leaveType"] = type,
                ["startDate"] = dates[0],
                ["endDate"] = dates.Count > 1 ? dates[1] : dates[0],
                ["reason"] = Reason(text),
            });
        }

        if (Has(t, "balance", "how many days", "how much leave", "leave left", "remaining") || (leaveWord && !Has(t, "shift")))
        {
            var args = new Dictionary<string, object?>();
            if (emp is not null)
            {
                args["employeeId"] = emp;
            }
            return Call(available, "get_leave_balance", args);
        }

        if (Has(t, "shift", "schedule", "roster", "working", "when am i", "my week"))
        {
            var args = new Dictionary<string, object?>();
            if (emp is not null)
            {
                args["employeeId"] = emp;
            }
            if (dates.Count > 0)
            {
                args["fromDate"] = dates[0];
            }
            if (dates.Count > 1)
            {
                args["toDate"] = dates[1];
            }
            return Call(available, "get_my_shifts", args);
        }

        return Text("I can help with leave balances, your shifts, finding a swap partner, submitting leave or swap requests, overtime pay, and (for managers) team coverage. What would you like?");
    }

    private static bool Has(string t, params string[] words) => words.Any(w => t.Contains(w, StringComparison.Ordinal));

    private static string? Reason(string text)
    {
        var idx = text.IndexOf("because", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = text.IndexOf(" for ", StringComparison.OrdinalIgnoreCase);
        }
        return idx >= 0 ? text[idx..].Trim().TrimEnd('.') : null;
    }

    private static ChatMessage Text(string s) => new(ChatRole.Assistant, s);

    private static ChatMessage Call(HashSet<string> available, string tool, Dictionary<string, object?> args)
    {
        if (available.Count > 0 && !available.Contains(tool))
        {
            return Text($"The {tool} capability is not available in this context.");
        }
        var pruned = args.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, pruned)]);
    }

    // --- summarising a tool result ----------------------------------------------------------

    private static string Summarize(List<ChatMessage> messages)
    {
        var result = messages.Last().Contents.OfType<FunctionResultContent>().FirstOrDefault();
        var callName = messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .LastOrDefault(c => c.CallId == result?.CallId)?.Name ?? "tool";
        if (result?.Result is null)
        {
            return "The tool returned nothing.";
        }
        var json = result.Result is JsonElement je ? je : JsonSerializer.SerializeToElement(result.Result);
        if (json.ValueKind == JsonValueKind.String)
        {
            return json.GetString() ?? string.Empty;
        }
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
        {
            return $"I couldn't do that: {Str(json, "message")}";
        }
        return callName switch
        {
            "get_leave_balance" => LeaveBalance(json),
            "get_my_shifts" => Shifts(json),
            "find_swap_candidates" => Candidates(json),
            "request_leave" or "request_shift_swap" =>
                $"Submitted {Str(json, "kind")} request {Str(json, "requestId")} — status: {Str(json, "status")}. {Str(json, "summary")} {Str(json, "nextStep")}",
            "calculate_overtime_pay" =>
                $"{Str(json, "name")} worked {Num(json, "totalHours")}h in the week of {Str(json, "weekStart")}: {Num(json, "regularHours")}h regular and {Num(json, "overtimeHours")}h overtime, total {Str(json, "currency")} {Num(json, "totalPay")}.",
            "get_team_coverage" => Coverage(json),
            _ => json.GetRawText(),
        };
    }

    private static string LeaveBalance(JsonElement j)
    {
        var sb = new StringBuilder($"Leave balances for {Str(j, "name")} ({Str(j, "employeeId")}): ");
        if (j.TryGetProperty("balances", out var arr))
        {
            sb.Append(string.Join(", ", arr.EnumerateArray().Select(b => $"{Str(b, "type")} {Num(b, "days")} days")));
        }
        sb.Append(CultureInfo.InvariantCulture, $". Accrued so far this leave year: {Num(j, "accruedThisLeaveYear")} days.");
        return sb.ToString();
    }

    private static string Shifts(JsonElement j)
    {
        var items = j.TryGetProperty("shifts", out var arr) ? arr.EnumerateArray().ToList() : [];
        if (items.Count == 0)
        {
            return $"{Str(j, "name")} has no shifts between {Str(j, "from")} and {Str(j, "to")}.";
        }
        var lines = items.Take(8).Select(s => $"{Str(s, "date")} ({Str(s, "dayOfWeek")}) {Str(s, "start")}-{Str(s, "end")} [{Str(s, "shiftId")}]");
        return $"{Str(j, "name")} has {items.Count} shift(s) between {Str(j, "from")} and {Str(j, "to")}: {string.Join("; ", lines)}{(items.Count > 8 ? "; …" : string.Empty)}.";
    }

    private static string Candidates(JsonElement j)
    {
        var cands = j.TryGetProperty("candidates", out var arr) ? arr.EnumerateArray().ToList() : [];
        if (cands.Count == 0)
        {
            return $"Nobody is eligible to take {Str(j, "shiftId")} right now. {Str(j, "note")}";
        }
        var names = cands.Select(c => $"{Str(c, "name")} ({Str(c, "employeeId")}, {Num(c, "weeklyHoursBefore")}h this week)");
        return $"Eligible to take {Str(j, "shiftId")} on {Str(j, "date")}: {string.Join(", ", names)}. {Str(j, "note")}";
    }

    private static string Coverage(JsonElement j)
    {
        var gaps = j.TryGetProperty("gaps", out var g) ? g.GetArrayLength() : 0;
        var open = j.TryGetProperty("openShifts", out var o) ? o.EnumerateArray().ToList() : [];
        var openText = open.Count == 0
            ? "no open shifts"
            : string.Join("; ", open.Select(s => $"{Str(s, "shiftId")} on {Str(s, "date")} {Str(s, "start")} → suggest {Str(s, "suggestedName")}"));
        return $"{Str(j, "teamName")} from {Str(j, "from")} to {Str(j, "to")}: {gaps} understaffed slot(s); {openText}.";
    }

    private static string Str(JsonElement j, string prop) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : string.Empty;

    private static string Num(JsonElement j, string prop) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;
}
