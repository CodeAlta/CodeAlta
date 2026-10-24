namespace CodeAlta.Agent;

/// <summary>
/// The model suggested what the user may write next in a session, once a turn ended: the
/// <see cref="AgentNextPromptTag"/> that ends its final answer. Like the background tasks, it is told to those who
/// listen to the session and is never recorded: a suggestion answers the turn that just ended, and says nothing once
/// another one started.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">Event timestamp.</param>
/// <param name="Suggestion">The suggested prompt.</param>
/// <remarks>
/// A suggestion is one line of at most <see cref="MaximumLength"/> characters: what somebody would send as it is.
/// </remarks>
public sealed record AgentPromptSuggestionEvent(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    string Suggestion)
    : AgentEvent(ProviderId, SessionId, Timestamp, null)
{
    /// <summary>The longest suggestion a session tells.</summary>
    public const int MaximumLength = 256;

    /// <summary>
    /// Returns what a model suggested as a session tells it: trimmed, or <see langword="null" /> when it is not
    /// one line of text of at most <see cref="MaximumLength"/> characters.
    /// </summary>
    /// <param name="suggestion">What the model suggested.</param>
    public static string? Normalize(string? suggestion)
    {
        var text = suggestion?.Trim();
        if (text is not { Length: > 0 and <= MaximumLength })
        {
            return null;
        }

        foreach (var character in text)
        {
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
            {
                return null;
            }
        }

        return text;
    }
}
