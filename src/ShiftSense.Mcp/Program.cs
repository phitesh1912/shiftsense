using ModelContextProtocol.Protocol;
using ShiftSense.Agent;
using ShiftSense.Agent.Persistence;
using ShiftSense.Agent.Tools;

// ShiftSense MCP server over stdio — for Claude Desktop, VS Code and other local MCP clients.
// The HTTP transport is served by ShiftSense.Api at /mcp; this host exists so a desktop client
// can launch the tools as a child process with no network at all.
//
// Identity: the client cannot authenticate, so the employee the server acts as comes from the
// SHIFTSENSE_USER environment variable (default E002). Everything else — authorization, PENDING
// writes, audit — is the same code path as the web agent.

var builder = Host.CreateApplicationBuilder(args);

// stdout is the protocol channel; logs must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddShiftSense(builder.Configuration);
var accessor = new CallerAccessor();
builder.Services.AddSingleton<ICallerAccessor>(accessor);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiftsense", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<SchedulingTools>();

var host = builder.Build();
await host.Services.InitializeShiftSenseAsync();

var userId = (builder.Configuration["SHIFTSENSE_USER"] ?? "E002").Trim().ToUpperInvariant();
using (var scope = host.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<ShiftSenseStore>();
    var emp = await store.GetEmployeeAsync(userId)
        ?? throw new InvalidOperationException($"SHIFTSENSE_USER '{userId}' is not a known employee");
    accessor.Current = new CallerContext(emp.Id, emp.Name, emp.Role, emp.Region, emp.TeamId, "mcp-stdio");
    await Console.Error.WriteLineAsync($"ShiftSense MCP server acting as {emp.Name} ({emp.Id}, {emp.Role})");
}

await host.RunAsync();
