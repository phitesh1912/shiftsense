namespace ShiftSense.Domain;

/// <summary>One row per tool invocation. Written by the agent layer, never by tools themselves.</summary>
public sealed class AuditEntry
{
    public long Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string ActorId { get; init; }
    public required string ActorRole { get; init; }
    public required string Tool { get; init; }
    public required string ArgumentsJson { get; init; }
    public required bool Authorized { get; init; }
    public required bool Succeeded { get; init; }
    public string? ResultSummary { get; init; }
    public required double LatencyMs { get; init; }
    public string? ConversationId { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
}
