namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentCompactionChunker
{
    public static IReadOnlyList<IReadOnlyList<LocalAgentConversationMessage>> CreateChunks(
        IReadOnlyList<LocalAgentConversationMessage> messages,
        int maxInputTokens,
        Func<IReadOnlyList<LocalAgentConversationMessage>, long> estimateChunkTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(estimateChunkTokens);

        if (messages.Count == 0)
        {
            return [];
        }

        var normalizedMessages = ExpandOversizedTextMessages(messages, maxInputTokens);
        var normalizedUnits = LocalAgentCompactionCanonicalizer.Normalize(normalizedMessages);
        var chunks = new List<IReadOnlyList<LocalAgentConversationMessage>>();
        var currentChunk = new List<LocalAgentCompactionUnit>();

        foreach (var unit in normalizedUnits)
        {
            currentChunk.Add(unit);
            if (currentChunk.Count == 1)
            {
                continue;
            }

            if (estimateChunkTokens(FlattenUnits(currentChunk)) <= maxInputTokens)
            {
                continue;
            }

            currentChunk.RemoveAt(currentChunk.Count - 1);
            chunks.Add(FlattenUnits(currentChunk));
            currentChunk = [unit];
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(FlattenUnits(currentChunk));
        }

        return chunks;
    }

    private static IReadOnlyList<LocalAgentConversationMessage> ExpandOversizedTextMessages(
        IReadOnlyList<LocalAgentConversationMessage> messages,
        int maxInputTokens)
    {
        if (maxInputTokens <= 0)
        {
            return messages;
        }

        var maxChunkCharacters = Math.Max(maxInputTokens * 4, 256);
        var expanded = new List<LocalAgentConversationMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (!TrySplitMessage(message, maxChunkCharacters, out var splitMessages))
            {
                expanded.Add(message);
                continue;
            }

            expanded.AddRange(splitMessages);
        }

        return expanded;
    }

    private static IReadOnlyList<LocalAgentConversationMessage> FlattenUnits(IReadOnlyList<LocalAgentCompactionUnit> units)
        => units.SelectMany(static unit => unit.SourceMessages).ToArray();

    private static bool TrySplitMessage(
        LocalAgentConversationMessage message,
        int maxChunkCharacters,
        out IReadOnlyList<LocalAgentConversationMessage> splitMessages)
    {
        if (message.Parts.Count != 1)
        {
            splitMessages = [];
            return false;
        }

        switch (message.Parts[0])
        {
            case LocalAgentMessagePart.Text text when text.Value.Length > maxChunkCharacters:
                splitMessages = SplitTextMessage(message.Role, text.Value, maxChunkCharacters);
                return true;
            case LocalAgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value) && reasoning.Value.Length > maxChunkCharacters:
                splitMessages = SplitReasoningMessage(message.Role, reasoning.Value!, reasoning.ProtectedData, maxChunkCharacters);
                return true;
            default:
                splitMessages = [];
                return false;
        }
    }

    private static IReadOnlyList<LocalAgentConversationMessage> SplitTextMessage(
        LocalAgentConversationRole role,
        string value,
        int maxChunkCharacters)
        => SplitByCharacterBudget(
            value,
            maxChunkCharacters,
            chunk => new LocalAgentConversationMessage(
                role,
                [new LocalAgentMessagePart.Text(chunk)]));

    private static IReadOnlyList<LocalAgentConversationMessage> SplitReasoningMessage(
        LocalAgentConversationRole role,
        string value,
        string? protectedData,
        int maxChunkCharacters)
        => SplitByCharacterBudget(
            value,
            maxChunkCharacters,
            chunk => new LocalAgentConversationMessage(
                role,
                [new LocalAgentMessagePart.Reasoning(chunk, ProtectedData: null)]));

    private static IReadOnlyList<LocalAgentConversationMessage> SplitByCharacterBudget(
        string value,
        int maxChunkCharacters,
        Func<string, LocalAgentConversationMessage> createMessage)
    {
        var parts = new List<LocalAgentConversationMessage>();
        var start = 0;
        while (start < value.Length)
        {
            var length = Math.Min(maxChunkCharacters, value.Length - start);
            var end = start + length;
            if (end < value.Length)
            {
                var breakIndex = value.LastIndexOfAny(['\n', '\r', ' ', '\t'], end - 1, length);
                if (breakIndex > start + (length / 2))
                {
                    end = breakIndex + 1;
                }
            }

            var chunk = value[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                parts.Add(createMessage(chunk));
            }

            start = end;
        }

        return parts;
    }
}
