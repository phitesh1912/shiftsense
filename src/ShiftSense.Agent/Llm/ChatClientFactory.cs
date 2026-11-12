using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace ShiftSense.Agent.Llm;

public static class ChatClientFactory
{
    public const string ActivitySourceName = "ShiftSense.Agent";

    /// <summary>
    /// Builds the <see cref="IChatClient"/> the agent runs on. Real models go through the OpenAI
    /// SDK pointed at any OpenAI-compatible endpoint (Gemini by default); without a key the
    /// deterministic fake is used.
    /// </summary>
    /// <remarks>
    /// Tracing is done at the agent layer (<see cref="ShiftSenseAgent"/> emits an <c>agent.run</c>
    /// span with token usage, and every tool call gets a <c>tool.*</c> span) rather than with
    /// <c>UseOpenTelemetry()</c>: on .NET 8 the M.E.AI 10.x/11.0 instrumentation binds to a
    /// <c>DiagnosticSource</c> 10 setter that became init-only in <c>DiagnosticSource</c> 11, which
    /// OpenTelemetry 1.21 pulls in. See BUILD_NOTES.md #3.
    /// </remarks>
    public static IChatClient Create(LlmOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsFake)
        {
            return new FakeChatClient();
        }
        var client = new OpenAIClient(
            new ApiKeyCredential(options.ApiKey!),
            new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
        IChatClient chat = client.GetChatClient(options.Model).AsIChatClient();
        return loggerFactory is null ? chat : chat.AsBuilder().UseLogging(loggerFactory).Build();
    }
}
