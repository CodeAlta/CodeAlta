namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentCompactionPlanner
{
    public static LocalAgentCompactionPreparation? Prepare(
        LocalAgentCompactionTrigger trigger,
        string? systemMessage,
        string? developerInstructions,
        IReadOnlyList<LocalAgentConversationMessage> conversation,
        AgentSessionUsage? usage,
        LocalAgentTokenBudget budget,
        LocalAgentCompactionSettings settings,
        string? anchorContentId = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(settings);

        var previousSummary = ExtractLeadingCheckpointSummary(conversation, out var startIndex);
        var effectiveConversation = conversation.Skip(startIndex).ToArray();
        if (effectiveConversation.Length == 0)
        {
            return null;
        }

        var tokensBefore = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            conversation,
            usage);

        if (effectiveConversation.Length < 2)
        {
            return null;
        }

        var targetPromptBudget = budget.UsablePromptBudget.HasValue
            ? Math.Max((long)Math.Floor(budget.UsablePromptBudget.Value * settings.TargetThreshold), 1)
            : Math.Max(tokensBefore.Tokens / 2, 1);
        var fixedTokenCost = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            [],
            usage: null).Tokens;
        var estimatedCheckpointTokens = previousSummary is null
            ? 64L
            : Math.Max(LocalAgentTokenEstimator.EstimateCheckpointTokens(previousSummary), 64L);
        var availableForKeep = budget.UsablePromptBudget.HasValue
            ? Math.Max(targetPromptBudget - fixedTokenCost - estimatedCheckpointTokens, 0)
            : Math.Max(targetPromptBudget / 2, 64);

        var groups = BuildGroups(effectiveConversation);
        var keepGroupIndexes = new SortedSet<int>();
        var keepTokens = 0L;
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            var candidateTokens = keepTokens + groups[index].Tokens;
            if (candidateTokens > availableForKeep)
            {
                continue;
            }

            keepGroupIndexes.Add(index);
            keepTokens = candidateTokens;
        }

        var anchorGroupIndex = settings.KeepLastUserMessage
            ? FindLatestUserGroupIndex(groups)
            : null;
        if (anchorGroupIndex is not null)
        {
            keepGroupIndexes.Add(anchorGroupIndex.Value);
            keepTokens = SumTokens(groups, keepGroupIndexes);

            while (keepTokens > availableForKeep)
            {
                var removableIndex = keepGroupIndexes
                    .Where(index => index != anchorGroupIndex.Value)
                    .DefaultIfEmpty(-1)
                    .First();
                if (removableIndex < 0)
                {
                    break;
                }

                keepGroupIndexes.Remove(removableIndex);
                keepTokens = SumTokens(groups, keepGroupIndexes);
            }

            if (budget.UsablePromptBudget is not null &&
                keepTokens > availableForKeep &&
                groups[anchorGroupIndex.Value].Tokens > availableForKeep)
            {
                throw new InvalidOperationException("The latest user message is too large to keep within the resolved prompt budget.");
            }
        }

        if (keepGroupIndexes.Count == 0 && groups.Count > 0)
        {
            keepGroupIndexes.Add(groups.Count - 1);
        }

        var messagesToKeep = FlattenGroups(groups, keepGroupIndexes);
        var messagesToSummarize = FlattenGroups(groups, Enumerable.Range(0, groups.Count).Except(keepGroupIndexes));
        if (messagesToSummarize.Count == 0)
        {
            return null;
        }

        var isSplitTurn = anchorGroupIndex is not null &&
                          groups.Select((group, index) => (group, index))
                              .Any(pair => !keepGroupIndexes.Contains(pair.index) && pair.index > anchorGroupIndex.Value);

        return new LocalAgentCompactionPreparation(
            Trigger: trigger,
            MessagesToSummarize: messagesToSummarize,
            MessagesToKeep: messagesToKeep,
            AnchorContentId: anchorContentId,
            IsSplitTurn: isSplitTurn,
            TokensBefore: tokensBefore,
            PreviousSummary: previousSummary);
    }

    private static string? ExtractLeadingCheckpointSummary(
        IReadOnlyList<LocalAgentConversationMessage> conversation,
        out int startIndex)
    {
        if (conversation.Count > 0 &&
            LocalAgentCompactionCheckpoint.TryExtractSummary(conversation[0]) is { } summary)
        {
            startIndex = 1;
            return summary;
        }

        startIndex = 0;
        return null;
    }

    private static List<MessageGroup> BuildGroups(IReadOnlyList<LocalAgentConversationMessage> conversation)
    {
        var groups = new List<MessageGroup>();
        foreach (var message in conversation)
        {
            if (message.Role is LocalAgentConversationRole.Tool && groups.Count > 0)
            {
                groups[^1].Messages.Add(message);
                groups[^1].Tokens += LocalAgentTokenEstimator.EstimateMessage(message);
                continue;
            }

            groups.Add(new MessageGroup(message, LocalAgentTokenEstimator.EstimateMessage(message)));
        }

        return groups;
    }

    private static int? FindLatestUserGroupIndex(IReadOnlyList<MessageGroup> groups)
    {
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            if (groups[index].Messages.Any(static message => message.Role is LocalAgentConversationRole.User))
            {
                return index;
            }
        }

        return null;
    }

    private static long SumTokens(IReadOnlyList<MessageGroup> groups, IEnumerable<int> indexes)
        => indexes.Sum(index => groups[index].Tokens);

    private static IReadOnlyList<LocalAgentConversationMessage> FlattenGroups(
        IReadOnlyList<MessageGroup> groups,
        IEnumerable<int> indexes)
    {
        var orderedIndexes = indexes.OrderBy(static index => index);
        var messages = new List<LocalAgentConversationMessage>();
        foreach (var index in orderedIndexes)
        {
            messages.AddRange(groups[index].Messages);
        }

        return messages;
    }

    private sealed class MessageGroup
    {
        public MessageGroup(LocalAgentConversationMessage message, long tokens)
        {
            Messages = [message];
            Tokens = tokens;
        }

        public List<LocalAgentConversationMessage> Messages { get; }

        public long Tokens { get; set; }
    }
}
