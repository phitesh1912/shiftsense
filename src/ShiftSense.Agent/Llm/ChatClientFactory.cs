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
    /// Both are wrapped with M.E.AI's OpenTelemetry instrumentation (GenAI semantic-convention
    /// spans with token usage per model call); the agent adds an <c>agent.run</c> span and every
    /// tool call a <c>tool.*</c> span. Note the package pins in the csproj: OpenTelemetry must stay
    /// on a release that depends on <c>DiagnosticSource</c> 10.x — see BUILD_NOTES.md #3.
    /// </remarks>
    public static IChatClient Create(LlmOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        IChatClient inner;
        if (options.IsFake)
        {
            inner = new FakeChatClient();
        }
        else
        {
            var client = new OpenAIClient(
                new ApiKeyCredential(options.ApiKey!),
                new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) });
            inner = client.GetChatClient(options.Model).AsIChatClient();
        }
        return inner.AsBuilder()
            .UseOpenTelemetry(loggerFactory, ActivitySourceName)
            .Build();
    }
}
