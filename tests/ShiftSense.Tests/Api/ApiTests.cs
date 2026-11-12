using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ShiftSense.Tests.Api;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"shiftsense-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SHIFTSENSE_DB"] = _dbPath,
            ["LLM_API_KEY"] = null,
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    public HttpClient As(string employeeId, string? conversation = null)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Employee-Id", employeeId);
        if (conversation is not null)
        {
            c.DefaultRequestHeaders.Add("X-Conversation-Id", conversation);
        }
        return c;
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_reports_fake_provider_and_tools()
    {
        var r = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal("fake", r.GetProperty("provider").GetString());
        Assert.Equal(7, r.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Chat_requires_identity()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/chat", new { message = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var bad = await _factory.As("E999").PostAsJsonAsync("/chat", new { message = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Chat_returns_reply_with_tool_trace()
    {
        var resp = await _factory.As("E003", "t1").PostAsJsonAsync("/chat", new { message = "what shifts am I working this week?" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("get_my_shifts", body.GetProperty("toolCalls")[0].GetProperty("tool").GetString());
        Assert.Contains("shift", body.GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("t1", body.GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task Full_approval_flow_employee_requests_manager_approves()
    {
        var emp = _factory.As("E005", "flow");
        var mgr = _factory.As("E001");

        // Annual: every seed employee has several days; sick balances can be as low as 1.
        var balanceBefore = await BalanceAsync(emp, "Annual");

        var chat = await emp.PostAsJsonAsync("/chat", new { message = "please book annual leave from 2026-12-07 to 2026-12-08" });
        chat.EnsureSuccessStatusCode();
        var reply = await chat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request_leave", reply.GetProperty("toolCalls")[0].GetProperty("tool").GetString());
        var requestId = JsonDocument.Parse(reply.GetProperty("toolCalls")[0].GetProperty("resultJson").GetString()!)
            .RootElement.GetProperty("requestId").GetString()!;

        // employee can't see the queue or approve
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync("/requests/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.PostAsJsonAsync($"/requests/{requestId}/approve", new { })).StatusCode);

        // a manager from the other region can't approve it either
        var otherMgr = _factory.As("E014");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherMgr.PostAsJsonAsync($"/requests/{requestId}/approve", new { })).StatusCode);

        // the right manager sees it and approves
        var pending = await mgr.GetFromJsonAsync<JsonElement>("/requests/pending");
        Assert.Contains(pending.GetProperty("leave").EnumerateArray(), l => l.GetProperty("id").GetString() == requestId);
        var approve = await mgr.PostAsJsonAsync($"/requests/{requestId}/approve", new { note = "feel better" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // approving twice is a conflict, and the balance went down by 2 working days
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.PostAsJsonAsync($"/requests/{requestId}/approve", new { })).StatusCode);
        Assert.Equal(balanceBefore - 2, await BalanceAsync(emp, "Annual"));

        // audit is manager-only and contains the decision
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync("/audit")).StatusCode);
        var audit = await mgr.GetFromJsonAsync<JsonElement>("/audit?take=20");
        Assert.Contains(audit.EnumerateArray(), a => a.GetProperty("tool").GetString() == "approve_request" && a.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Unknown_or_malformed_request_ids()
    {
        var mgr = _factory.As("E001");
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.PostAsJsonAsync("/requests/LR-9999/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.PostAsJsonAsync("/requests/XX-1/reject", new { })).StatusCode);
    }

    private static async Task<decimal> BalanceAsync(HttpClient emp, string type)
    {
        var resp = await emp.PostAsJsonAsync("/chat", new { message = "what is my leave balance?" });
        resp.EnsureSuccessStatusCode();
        var reply = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var result = JsonDocument.Parse(reply.GetProperty("toolCalls")[0].GetProperty("resultJson").GetString()!).RootElement;
        return result.GetProperty("balances").EnumerateArray()
            .Single(b => b.GetProperty("type").GetString() == type)
            .GetProperty("days").GetDecimal();
    }
}
