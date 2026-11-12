using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShiftSense.Agent;
using ShiftSense.Agent.Tools;
using ShiftSense.Domain;
using Role = ShiftSense.Domain.Role;

namespace ShiftSense.Tests.Mcp;

/// <summary>
/// Drives the MCP server through the official client over an in-process stream transport —
/// the same framing the stdio host (ShiftSense.Mcp) uses with Claude Desktop — so tool
/// discovery, argument binding, identity and authorization are all exercised at protocol level.
/// </summary>
public class McpServerTests
{
    [Fact]
    public async Task Serves_the_seven_tools_with_identity_and_authorization()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"shiftsense-mcp-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SHIFTSENSE_DB"] = dbPath })
            .Build();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection().AddLogging().AddShiftSense(config);
        var accessor = new CallerAccessor();
        services.AddSingleton<ICallerAccessor>(accessor);
        services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiftsense", Version = "test" })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<SchedulingTools>();

        await using var sp = services.BuildServiceProvider();
        await sp.InitializeShiftSenseAsync();
        accessor.Current = new CallerContext("E002", "Arjun Mehta", Role.Employee, Region.IN, "T-HYD", "mcp-test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var server = sp.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), loggerFactory: null),
            cancellationToken: cts.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            Assert.Equal(ToolCatalog.Names, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
            Assert.Contains("PENDING", tools.Single(t => t.Name == "request_shift_swap").Description, StringComparison.Ordinal);

            var mine = await client.CallToolAsync("get_leave_balance", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            var json = JsonDocument.Parse(Text(mine)).RootElement;
            Assert.Equal("E002", json.GetProperty("employeeId").GetString());
            Assert.True(json.GetProperty("balances").GetArrayLength() >= 2);

            var denied = await client.CallToolAsync("get_leave_balance", new Dictionary<string, object?> { ["employeeId"] = "E001" }, cancellationToken: cts.Token);
            Assert.Contains("not_authorized", Text(denied), StringComparison.Ordinal);

            var bad = await client.CallToolAsync("get_my_shifts", new Dictionary<string, object?> { ["fromDate"] = "yesterday" }, cancellationToken: cts.Token);
            Assert.Contains("invalid_argument", Text(bad), StringComparison.Ordinal);
        }

        await cts.CancelAsync();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        await sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // pooled connections keep the file open
        try
        {
            File.Delete(dbPath);
        }
        catch (IOException)
        {
            // temp file; the OS will reclaim it
        }
    }

    private static string Text(CallToolResult r) =>
        string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
