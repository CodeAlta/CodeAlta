namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentCompactionChunker
{
    public static IReadOnlyList<IReadOnlyList<AgentConversationMessage>> CreateChunks(
        IReadOnlyList<AgentConversationMessage> messages,
        int maxInputTokens,
        Func<IReadOnlyList<AgentConversationMessage>, long> estimateChunkTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(estimateChunkTokens);

        if (messages.Count == 0)
        {
            return [];
        }

        var normalizedMessages = ExpandOversizedTextMessages(messages, maxInputTokens);
        var normalizedUnits = AgentCompactionCanonicalizer.Normalize(normalizedMessages);
        var chunks = new List<IReadOnlyList<AgentConversationMessage>>();
        var currentChunk = new List<AgentCompactionUnit>();

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

    private static IReadOnlyList<AgentConversationMessage> ExpandOversizedTextMessages(
        IReadOnlyList<AgentConversationMessage> messages,
        int maxInputTokens)
    {
        if (maxInputTokens <= 0)
        {
            return messages;
        }

        var maxChunkCharacters = Math.Max(maxInputTokens * 4, 256);
        var expanded = new List<AgentConversationMessage>(messages.Count);
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

    private static IReadOnlyList<AgentConversationMessage> FlattenUnits(IReadOnlyList<AgentCompactionUnit> units)
        => units.SelectMany(static unit => unit.SourceMessages).ToArray();

    private static bool TrySplitMessage(
        AgentConversationMessage message,
        int maxChunkCharacters,
        out IReadOnlyList<AgentConversationMessage> splitMessages)
    {
        if (message.Parts.Count != 1)
        {
            splitMessages = [];
            return false;
        }

        switch (message.Parts[0])
        {
            case AgentMessagePart.Text text when text.Value.Length > maxChunkCharacters:
                splitMessages = SplitTextMessage(message.Role, text.Value, maxChunkCharacters);
                return true;
            case AgentMessagePart.Reasoning reasoning when !string.IsNullOrWhiteSpace(reasoning.Value) && reasoning.Value.Length > maxChunkCharacters:
                splitMessages = SplitReasoningMessage(message.Role, reasoning.Value!, reasoning.ProtectedData, maxChunkCharacters);
                return true;
            default:
                splitMessages = [];
                return false;
        }
    }

    private static IReadOnlyList<AgentConversationMessage> SplitTextMessage(
        AgentConversationRole role,
        string value,
        int maxChunkCharacters)
        => SplitByCharacterBudget(
            value,
            maxChunkCharacters,
            chunk => new AgentConversationMessage(
                role,
                [new AgentMessagePart.Text(chunk)]));

    private static IReadOnlyList<AgentConversationMessage> SplitReasoningMessage(
        AgentConversationRole role,
        string value,
        string? protectedData,
        int maxChunkCharacters)
        => SplitByCharacterBudget(
            value,
            maxChunkCharacters,
            chunk => new AgentConversationMessage(
                role,
                [new AgentMessagePart.Reasoning(chunk, ProtectedData: null)]));

    private static IReadOnlyList<AgentConversationMessage> SplitByCharacterBudget(
        string value,
        int maxChunkCharacters,
        Func<string, AgentConversationMessage> createMessage)
    {
        var parts = new List<AgentConversationMessage>();
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
