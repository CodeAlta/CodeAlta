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
        return string.IsNullOrWhiteSpace(reasoning.Value)
            ? null
            : new AgentMessagePart.Text(CreateReasoningSummaryText(reasoning.Value));
    }

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
        => $"<assistant_reasoning_summary>{value}</assistant_reasoning_summary>";
}
