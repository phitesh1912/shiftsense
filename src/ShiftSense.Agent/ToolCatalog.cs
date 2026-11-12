using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using ShiftSense.Agent.Tools;

namespace ShiftSense.Agent;

/// <summary>
/// Turns the <see cref="SchedulingTools"/> methods into <see cref="AIFunction"/>s for the agent.
/// The same <c>[McpServerTool]</c> attribute drives both hosts, so tool names are identical in the
/// chat UI, the eval and Claude Desktop.
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<string> Names { get; } =
        Methods().Select(m => m.Attr.Name!).ToList();

    public static IList<AITool> Create(SchedulingTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return Methods()
            .Select(m => (AITool)AIFunctionFactory.Create(m.Method, tools, new AIFunctionFactoryOptions
            {
                Name = m.Attr.Name,
                Description = m.Method.GetCustomAttribute<DescriptionAttribute>()?.Description,
            }))
            .ToList();
    }

    private static IEnumerable<(MethodInfo Method, McpServerToolAttribute Attr)> Methods() =>
        typeof(SchedulingTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (m, m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(t => t.Item2?.Name is not null)
            .Select(t => (t.m, t.Item2!))
            .OrderBy(t => t.Item2.Name, StringComparer.Ordinal);
}
