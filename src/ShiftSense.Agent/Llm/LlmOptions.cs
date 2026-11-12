using Microsoft.Extensions.Configuration;

namespace ShiftSense.Agent.Llm;

/// <summary>
/// LLM connection settings from environment variables. With no API key the deterministic
/// <see cref="FakeChatClient"/> is used, so tests, CI and the offline demo need nothing.
/// </summary>
public sealed record LlmOptions(string Provider, string Endpoint, string Model, string? ApiKey)
{
    public const string GeminiOpenAIEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public static LlmOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var key = config["LLM_API_KEY"];
        var endpoint = config["LLM_ENDPOINT"] ?? GeminiOpenAIEndpoint;
        var model = config["LLM_MODEL"] ?? "gemini-2.5-flash";
        var provider = string.IsNullOrWhiteSpace(key) ? "fake" : "openai-compatible";
        return new LlmOptions(provider, endpoint, model, string.IsNullOrWhiteSpace(key) ? null : key);
    }

    public bool IsFake => Provider == "fake";
}
