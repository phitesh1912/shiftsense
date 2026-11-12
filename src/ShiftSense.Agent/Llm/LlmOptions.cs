using Microsoft.Extensions.Configuration;

namespace ShiftSense.Agent.Llm;

/// <summary>
/// LLM connection settings from environment variables. Resolution order:
/// <list type="number">
/// <item><c>LLM_API_KEY</c> (+ optional <c>LLM_ENDPOINT</c>/<c>LLM_MODEL</c>) — any OpenAI-compatible endpoint.</item>
/// <item><c>GROQ_API_KEY</c> — Groq's free OpenAI-compatible endpoint with a tool-calling Llama model.</item>
/// <item>Nothing — the deterministic <see cref="FakeChatClient"/>, so tests, CI and the offline demo need no key.</item>
/// </list>
/// </summary>
public sealed record LlmOptions(string Provider, string Endpoint, string Model, string? ApiKey)
{
    public const string GeminiOpenAIEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public const string GroqEndpoint = "https://api.groq.com/openai/v1";
    // Groq retired its Llama 3.x endpoints in 2026; gpt-oss-120b supports tool calling well.
    public const string GroqDefaultModel = "openai/gpt-oss-120b";

    public static LlmOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var key = Clean(config["LLM_API_KEY"]);
        var endpoint = Clean(config["LLM_ENDPOINT"]);
        var model = Clean(config["LLM_MODEL"]);

        if (key is not null)
        {
            return new LlmOptions("openai-compatible", endpoint ?? GeminiOpenAIEndpoint, model ?? "gemini-2.5-flash", key);
        }
        var groq = Clean(config["GROQ_API_KEY"]);
        if (groq is not null)
        {
            return new LlmOptions("groq", endpoint ?? GroqEndpoint, model ?? GroqDefaultModel, groq);
        }
        return new LlmOptions("fake", endpoint ?? GroqEndpoint, model ?? GroqDefaultModel, null);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public bool IsFake => Provider == "fake";
}
