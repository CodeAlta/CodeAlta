using System.Collections.Concurrent;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The replayed reasoning an endpoint refused, for as long as its provider runs: a thinking block whose signature the
/// endpoint cannot verify (it was signed for another account or backend) fails every request that replays it, and the
/// conversation of a session replays it on each request. A payload refused once is sent as text from then on, while
/// the reasoning written after the refusal keeps its signature.
/// </summary>
internal sealed class AgentRefusedReasoning
{
    // The payloads of many long sessions; past it the record starts again, and a session refused again costs one more
    // request.
    private const int Capacity = 4096;

    // A hash stands for a payload of a few kilobytes: two payloads of one hash only send one more as text.
    private readonly ConcurrentDictionary<int, byte> _payloads = new();

    /// <summary>Gets whether no payload was refused.</summary>
    public bool IsEmpty => _payloads.IsEmpty;

    /// <summary>Gets whether the endpoint refused the payload of a reasoning part.</summary>
    /// <param name="reasoning">The reasoning part.</param>
    /// <returns>True when its payload was in a request the endpoint refused.</returns>
    public bool Contains(AgentMessagePart.Reasoning reasoning)
        => !string.IsNullOrEmpty(reasoning.ProtectedData) && _payloads.ContainsKey(KeyOf(reasoning.ProtectedData));

    /// <summary>Records the payloads of the reasoning a refused request replayed.</summary>
    /// <param name="conversation">The conversation the request sent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="conversation"/> is null.</exception>
    public void Add(IReadOnlyList<AgentConversationMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (_payloads.Count > Capacity)
        {
            _payloads.Clear();
        }

        foreach (var message in conversation)
        {
            foreach (var part in message.Parts)
            {
                if (part is AgentMessagePart.Reasoning { ProtectedData: { Length: > 0 } payload })
                {
                    _payloads.TryAdd(KeyOf(payload), 0);
                }
            }
        }
    }

    private static int KeyOf(string payload) => payload.GetHashCode(StringComparison.Ordinal);
}
