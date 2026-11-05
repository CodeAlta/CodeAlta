namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Captures the provider/model identity that produced replayable reasoning content.
/// </summary>
/// <param name="ProtocolFamily">The provider protocol family used for the turn.</param>
/// <param name="ProviderKey">The configured provider key used for the turn.</param>
/// <param name="TransportKind">The provider transport kind used for the turn.</param>
/// <param name="ModelId">The model identifier used for the turn.</param>
public sealed record AgentReasoningProvenance(
    string ProtocolFamily,
    string ProviderKey,
    AgentTransportKind TransportKind,
    string? ModelId);

/// <summary>
/// Provides helpers for safe provider reasoning replay.
/// </summary>
public static class AgentReasoningReplay
{
    private const string LegacyReasoningSummaryStartTag = "<assistant_reasoning_summary>";
    private const string LegacyReasoningSummaryEndTag = "</assistant_reasoning_summary>";

    /// <summary>
    /// Creates reasoning provenance for a provider turn request.
    /// </summary>
    /// <param name="request">The provider turn request.</param>
    /// <returns>The provider/model provenance for reasoning emitted by the turn.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <see langword="null" />.</exception>
    public static AgentReasoningProvenance CreateProvenance(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AgentReasoningProvenance(
            request.Provider.ProtocolFamily,
            request.Provider.ProviderKey,
            request.Provider.TransportKind,
            request.ModelId);
    }

    /// <summary>
    /// Returns a provider-safe conversation where reasoning parts from incompatible or unknown providers/models are downgraded.
    /// </summary>
    /// <param name="conversation">The replayable conversation.</param>
    /// <param name="request">The provider turn request.</param>
    /// <returns>A conversation safe to send to the provider/model in <paramref name="request" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conversation" /> or <paramref name="request" /> is <see langword="null" />.</exception>
    public static IReadOnlyList<AgentConversationMessage> SanitizeForRequest(
        IReadOnlyList<AgentConversationMessage> conversation,
        AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(request);

        AgentConversationMessage[]? sanitizedMessages = null;
        for (var messageIndex = 0; messageIndex < conversation.Count; messageIndex++)
        {
            var message = conversation[messageIndex];
            var sanitizedParts = SanitizeParts(message.Parts, request, out var changed);
            if (!changed)
            {
                if (sanitizedMessages is not null)
                {
                    sanitizedMessages[messageIndex] = message;
                }

                continue;
            }

            sanitizedMessages ??= CopyPrefix(conversation, messageIndex);
            sanitizedMessages[messageIndex] = message with { Parts = sanitizedParts };
        }

        return sanitizedMessages ?? conversation;
    }

    private static AgentConversationMessage[] CopyPrefix(
        IReadOnlyList<AgentConversationMessage> conversation,
        int count)
    {
        var copy = new AgentConversationMessage[conversation.Count];
        for (var index = 0; index < count; index++)
        {
            copy[index] = conversation[index];
        }

        return copy;
    }

    private static IReadOnlyList<AgentMessagePart> SanitizeParts(
        IReadOnlyList<AgentMessagePart> parts,
        AgentTurnRequest request,
        out bool changed)
    {
        List<AgentMessagePart>? sanitizedParts = null;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            var sanitized = SanitizePart(part, request, out var partChanged);
            if (!partChanged)
            {
                sanitizedParts?.Add(part);
                continue;
            }

            sanitizedParts ??= CopyPrefix(parts, partIndex);
            if (sanitized is not null)
            {
                sanitizedParts.Add(sanitized);
            }
        }

        changed = sanitizedParts is not null;
        return sanitizedParts ?? parts;
    }

    private static List<AgentMessagePart> CopyPrefix(IReadOnlyList<AgentMessagePart> parts, int count)
    {
        var copy = new List<AgentMessagePart>(parts.Count);
        for (var index = 0; index < count; index++)
        {
            copy.Add(parts[index]);
        }

        return copy;
    }

    private static AgentMessagePart? SanitizePart(
        AgentMessagePart part,
        AgentTurnRequest request,
        out bool changed)
    {
        if (part is AgentMessagePart.Text text && TryNormalizeLegacyReasoningSummaryText(text.Value, out var normalizedText))
        {
            changed = true;
            return new AgentMessagePart.Text(normalizedText);
        }

        if (part is not AgentMessagePart.Reasoning reasoning)
        {
            changed = false;
            return part;
        }

        if (IsCompatible(reasoning.Provenance, request))
        {
            changed = false;
            return part;
        }

        changed = true;
        return Downgrade(reasoning);
    }

    /// <summary>
    /// Returns the conversation where the reasoning with a provider payload that <paramref name="downgrade"/> selects is
    /// sent as another model would get it: its summary as text, or nothing.
    /// </summary>
    /// <param name="conversation">The conversation of a request, already sanitized for its provider.</param>
    /// <param name="downgrade">Whether a reasoning part with a payload is sent without it.</param>
    /// <returns>The conversation, the same instance when nothing changed.</returns>
    internal static IReadOnlyList<AgentConversationMessage> DowngradeReplayed(
        IReadOnlyList<AgentConversationMessage> conversation,
        Func<AgentMessagePart.Reasoning, bool> downgrade)
    {
        AgentConversationMessage[]? downgraded = null;
        for (var messageIndex = 0; messageIndex < conversation.Count; messageIndex++)
        {
            var message = conversation[messageIndex];
            if (!message.Parts.Any(part => IsReplayed(part, downgrade)))
            {
                if (downgraded is not null)
                {
                    downgraded[messageIndex] = message;
                }

                continue;
            }

            downgraded ??= CopyPrefix(conversation, messageIndex);
            var parts = new List<AgentMessagePart>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                if (!IsReplayed(part, downgrade))
                {
                    parts.Add(part);
                }
                else if (Downgrade((AgentMessagePart.Reasoning)part) is { } text)
                {
                    parts.Add(text);
                }
            }

            downgraded[messageIndex] = message with { Parts = parts };
        }

        return downgraded ?? conversation;
    }

    /// <summary>Gets whether a conversation replays reasoning with a provider payload (a signature, an encrypted block).</summary>
    /// <param name="conversation">The conversation of a request.</param>
    /// <returns>True when a reasoning part carries a payload.</returns>
    internal static bool HasReplayedReasoning(IReadOnlyList<AgentConversationMessage> conversation)
        => conversation.Any(static message => message.Parts.Any(static part => IsReplayed(part, static _ => true)));

    private static bool IsReplayed(AgentMessagePart part, Func<AgentMessagePart.Reasoning, bool> downgrade)
        => part is AgentMessagePart.Reasoning { ProtectedData.Length: > 0 } reasoning && downgrade(reasoning);

    // The reasoning a model wrote as its own text is not a summary: another model is not given it.
    private static AgentMessagePart.Text? Downgrade(AgentMessagePart.Reasoning reasoning)
        => string.IsNullOrWhiteSpace(reasoning.Value) || reasoning.RawText == true
            ? null
            : new AgentMessagePart.Text(CreateReasoningSummaryText(reasoning.Value));

    private static bool IsCompatible(AgentReasoningProvenance? provenance, AgentTurnRequest request)
    {
        if (provenance is null || string.IsNullOrWhiteSpace(provenance.ModelId) || string.IsNullOrWhiteSpace(request.ModelId))
        {
            return false;
        }

        return provenance.TransportKind == request.Provider.TransportKind &&
               string.Equals(provenance.ProtocolFamily, request.Provider.ProtocolFamily, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(provenance.ProviderKey, request.Provider.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(provenance.ModelId, request.ModelId, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateReasoningSummaryText(string value)
        => $"Assistant reasoning summary:\n{value.Trim()}";

    private static bool TryNormalizeLegacyReasoningSummaryText(string value, out string normalizedText)
    {
        normalizedText = value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(LegacyReasoningSummaryStartTag, StringComparison.Ordinal) ||
            !trimmed.EndsWith(LegacyReasoningSummaryEndTag, StringComparison.Ordinal))
        {
            return false;
        }

        var summary = trimmed.Substring(
            LegacyReasoningSummaryStartTag.Length,
            trimmed.Length - LegacyReasoningSummaryStartTag.Length - LegacyReasoningSummaryEndTag.Length);
        normalizedText = CreateReasoningSummaryText(summary);
        return true;
    }
}
