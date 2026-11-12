using ShiftSense.Agent.Tools;

namespace ShiftSense.Api;

/// <summary>
/// Caller identity for everything that runs inside an HTTP request — REST endpoints and MCP tool
/// calls alike. The identity is stored on <see cref="HttpContext.Items"/> by the request pipeline
/// after validating the <c>X-Employee-Id</c> header, so it flows into whatever DI scope the MCP
/// server creates for a tool invocation.
/// </summary>
public sealed class HttpContextCallerAccessor(IHttpContextAccessor http) : ICallerAccessor
{
    public const string ItemKey = "ShiftSense.Caller";

    public CallerContext Current =>
        http.HttpContext?.Items[ItemKey] as CallerContext
        ?? throw new InvalidOperationException("no caller resolved for this request");
}
