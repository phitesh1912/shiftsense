using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace ShiftSense.Agent;

/// <summary>
/// In-memory chat history per conversation, keyed by (employee, conversation) so one employee
/// can never continue another's thread. Bounded so a long demo session can't grow unbounded.
/// </summary>
public sealed class ConversationStore(int maxMessages = 40)
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _threads = new(StringComparer.Ordinal);

    private static string Key(string employeeId, string conversationId) => $"{employeeId}|{conversationId}";

    public IReadOnlyList<ChatMessage> Get(string employeeId, string conversationId) =>
        _threads.TryGetValue(Key(employeeId, conversationId), out var list) ? list.ToList() : [];

    public void Append(string employeeId, string conversationId, IEnumerable<ChatMessage> messages)
    {
        var list = _threads.GetOrAdd(Key(employeeId, conversationId), _ => []);
        lock (list)
        {
            list.AddRange(messages);
            if (list.Count > maxMessages)
            {
                list.RemoveRange(0, list.Count - maxMessages);
            }
        }
    }

    public void Clear(string employeeId, string conversationId) => _threads.TryRemove(Key(employeeId, conversationId), out _);
}
