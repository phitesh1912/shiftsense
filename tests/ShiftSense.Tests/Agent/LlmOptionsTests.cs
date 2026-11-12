using Microsoft.Extensions.Configuration;
using ShiftSense.Agent.Llm;

namespace ShiftSense.Tests.Agent;

public class LlmOptionsTests
{
    private static LlmOptions From(params (string Key, string? Value)[] pairs) =>
        LlmOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build());

    [Fact]
    public void No_keys_means_fake()
    {
        var o = From(("LLM_API_KEY", ""), ("GROQ_API_KEY", " "));
        Assert.True(o.IsFake);
        Assert.Null(o.ApiKey);
    }

    [Fact]
    public void Groq_key_selects_groq_endpoint_and_tool_capable_default_model()
    {
        var o = From(("GROQ_API_KEY", "gsk_test"));
        Assert.Equal("groq", o.Provider);
        Assert.Equal(LlmOptions.GroqEndpoint, o.Endpoint);
        Assert.Equal(LlmOptions.GroqDefaultModel, o.Model);
        Assert.Equal("gsk_test", o.ApiKey);
    }

    [Fact]
    public void Explicit_llm_key_wins_over_groq_and_keeps_overrides()
    {
        var o = From(("LLM_API_KEY", "k"), ("GROQ_API_KEY", "g"), ("LLM_MODEL", "gpt-4o-mini"), ("LLM_ENDPOINT", "https://api.openai.com/v1/"));
        Assert.Equal("openai-compatible", o.Provider);
        Assert.Equal("gpt-4o-mini", o.Model);
        Assert.Equal("https://api.openai.com/v1/", o.Endpoint);
        Assert.Equal("k", o.ApiKey);
    }
}
