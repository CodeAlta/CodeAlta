namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentMediaCompaction
{
    public static bool ContainsPrunableInlineImages(
        IReadOnlyList<AgentConversationMessage> messages,
        Func<AgentConversationMessage, bool>? preserveMessage = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            if (preserveMessage?.Invoke(message) == true)
            {
                continue;
            }

            foreach (var part in message.Parts)
            {
                if (part is AgentMessagePart.Data data && IsImage(data.MediaType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static AgentInlineMediaPruneResult PruneInlineImages(
        IReadOnlyList<AgentConversationMessage> messages,
        Func<AgentConversationMessage, bool>? preserveMessage = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        AgentConversationMessage[]? rewrittenMessages = null;
        var prunedImageCount = 0;
        var prunedBase64Characters = 0L;

        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (preserveMessage?.Invoke(message) == true)
            {
                rewrittenMessages?[messageIndex] = message;
                continue;
            }

            List<AgentMessagePart>? rewrittenParts = null;
            for (var partIndex = 0; partIndex < message.Parts.Count; partIndex++)
            {
                var part = message.Parts[partIndex];
                if (part is AgentMessagePart.Data data && IsImage(data.MediaType))
                {
                    rewrittenParts ??= CopyPriorParts(message.Parts, partIndex);
                    rewrittenParts.Add(CreateOmittedImagePlaceholder(data));
                    prunedImageCount++;
                    prunedBase64Characters += data.Base64Data.Length;
                    continue;
                }

                rewrittenParts?.Add(part);
            }

            if (rewrittenParts is null)
            {
                rewrittenMessages?[messageIndex] = message;
                continue;
            }

            rewrittenMessages ??= CopyPriorMessages(messages, messageIndex);
            rewrittenMessages[messageIndex] = new AgentConversationMessage(message.Role, rewrittenParts);
        }

        return new AgentInlineMediaPruneResult(
            rewrittenMessages ?? messages,
            prunedImageCount,
            prunedBase64Characters);
    }

    public static bool IsImage(string? mediaType)
        => mediaType is not null &&
           mediaType.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static List<AgentMessagePart> CopyPriorParts(
        IReadOnlyList<AgentMessagePart> parts,
        int count)
    {
        var rewrittenParts = new List<AgentMessagePart>(parts.Count);
        for (var index = 0; index < count; index++)
        {
            rewrittenParts.Add(parts[index]);
        }

        return rewrittenParts;
    }

    private static AgentConversationMessage[] CopyPriorMessages(
        IReadOnlyList<AgentConversationMessage> messages,
        int count)
    {
        var rewrittenMessages = new AgentConversationMessage[messages.Count];
        for (var index = 0; index < count; index++)
        {
            rewrittenMessages[index] = messages[index];
        }

        return rewrittenMessages;
    }

    private static AgentMessagePart.Text CreateOmittedImagePlaceholder(AgentMessagePart.Data data)
    {
        var name = string.IsNullOrWhiteSpace(data.Name) ? "image" : data.Name.Trim();
        var mediaType = string.IsNullOrWhiteSpace(data.MediaType) ? "image/*" : data.MediaType.Trim();
        var byteCount = EstimateDecodedByteCount(data.Base64Data);
        var sizeDescription = byteCount is > 0
            ? $"approximately {byteCount.Value} bytes"
            : $"{data.Base64Data.Length} base64 characters";
        return new AgentMessagePart.Text(
            $"[Image attachment omitted from retained context: {name}; mediaType={mediaType}; originalSize={sizeDescription}.]");
    }

    private static long? EstimateDecodedByteCount(string base64Data)
    {
        if (string.IsNullOrWhiteSpace(base64Data))
        {
            return 0;
        }

        var length = base64Data.Length;
        while (length > 0 && char.IsWhiteSpace(base64Data[length - 1]))
        {
            length--;
        }

        var padding = 0;
        for (var index = length - 1; index >= 0 && base64Data[index] == '='; index--)
        {
            padding++;
        }

        if (length < padding)
        {
            return null;
        }

        return Math.Max(0, (length * 3L / 4L) - padding);
    }
}

internal sealed record AgentInlineMediaPruneResult(
    IReadOnlyList<AgentConversationMessage> Messages,
    int PrunedImageCount,
    long PrunedBase64Characters);
