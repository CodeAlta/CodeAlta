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
        string? anchorContentId = null,
        long? checkpointTokenEstimate = null,
        long? promptBudgetOverride = null,
        bool keepAnchorOnly = false)
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

        var groups = BuildGroups(effectiveConversation);
        var checkpointTokens = checkpointTokenEstimate
            ?? (previousSummary is null
                ? 64L
                : Math.Max(LocalAgentTokenEstimator.EstimateCheckpointTokens(previousSummary), 64L));
        var promptBudget = ResolveRetainedPromptBudget(tokensBefore.Tokens, budget.UsablePromptBudget, settings, promptBudgetOverride);
        var fixedTokenCost = LocalAgentTokenEstimator.EstimatePromptTokens(
            systemMessage,
            developerInstructions,
            [],
            usage: null).Tokens;
        var availableForRetained = Math.Max(promptBudget - fixedTokenCost - checkpointTokens, 0L);
        var anchorGroupIndex = settings.KeepLastUserMessage ? FindLatestUserGroupIndex(groups) : null;

        IReadOnlyList<int> turnPrefixIndexes = [];
        IReadOnlyList<int> suffixIndexes;
        bool isSplitTurn;
        int? oversizedAnchorGroupIndex = null;

        if (keepAnchorOnly)
        {
            (turnPrefixIndexes, suffixIndexes, isSplitTurn, oversizedAnchorGroupIndex) = BuildAnchorOnlyPlan(
                groups,
                anchorGroupIndex,
                availableForRetained,
                settings.AllowOversizedAnchorReduction);
        }
        else
        {
            (turnPrefixIndexes, suffixIndexes, isSplitTurn, oversizedAnchorGroupIndex) = BuildPlan(
                groups,
                anchorGroupIndex,
                settings.AllowSplitTurn,
                availableForRetained,
                settings.AllowOversizedAnchorReduction);
        }

        var retainedIndexes = turnPrefixIndexes.Concat(suffixIndexes).ToHashSet();
        if (oversizedAnchorGroupIndex is { } excludedAnchorIndex)
        {
            retainedIndexes.Add(excludedAnchorIndex);
        }

        var messagesToSummarize = FlattenGroups(
            groups,
            Enumerable.Range(0, groups.Count).Where(index => !retainedIndexes.Contains(index)));
        if (messagesToSummarize.Count == 0 && oversizedAnchorGroupIndex is null)
        {
            return null;
        }

        var oversizedAnchorMessage = oversizedAnchorGroupIndex is { } oversizedIndex
            ? groups[oversizedIndex].Messages.LastOrDefault(static message => message.Role is LocalAgentConversationRole.User)
            : null;

        return new LocalAgentCompactionPreparation(
            Trigger: trigger,
            MessagesToSummarize: messagesToSummarize,
            TurnPrefixMessages: FlattenGroups(groups, turnPrefixIndexes),
            MessagesToKeep: FlattenGroups(groups, suffixIndexes),
            AnchorContentId: anchorContentId,
            IsSplitTurn: isSplitTurn,
            TokensBefore: tokensBefore,
            PreviousSummary: previousSummary,
            OversizedAnchorMessage: oversizedAnchorMessage);
    }

    private static long ResolveRetainedPromptBudget(
        long tokensBefore,
        long? usablePromptBudget,
        LocalAgentCompactionSettings settings,
        long? promptBudgetOverride)
    {
        if (promptBudgetOverride is > 0)
        {
            return promptBudgetOverride.Value;
        }

        var resolvedPromptBudget = usablePromptBudget is > 0
            ? usablePromptBudget.Value
            : Math.Max(tokensBefore / 2, 1L);
        var preferredRetainedBudget = settings.RecentSuffixTargetTokens > 0
            ? settings.RecentSuffixTargetTokens
            : Math.Max((long)Math.Floor(resolvedPromptBudget * settings.TargetThreshold), 1L);
        return Math.Max(Math.Min(resolvedPromptBudget, preferredRetainedBudget), 1L);
    }

    private static (IReadOnlyList<int> TurnPrefixIndexes, IReadOnlyList<int> SuffixIndexes, bool IsSplitTurn, int? OversizedAnchorGroupIndex) BuildPlan(
        IReadOnlyList<MessageGroup> groups,
        int? anchorGroupIndex,
        bool allowSplitTurn,
        long availableForRetained,
        bool allowOversizedAnchorReduction)
    {
        if (groups.Count == 0)
        {
            return ([], [], false, null);
        }

        if (anchorGroupIndex is null)
        {
            return ([], BuildContiguousSuffix(groups, availableForRetained), false, null);
        }

        var anchoredSuffixIndexes = BuildSuffixFromStart(groups, anchorGroupIndex.Value);
        var anchoredSuffixTokens = SumTokens(groups, anchoredSuffixIndexes);
        if (anchoredSuffixTokens <= availableForRetained)
        {
            return ([], BuildContiguousSuffix(groups, availableForRetained, maximumStartIndex: anchorGroupIndex.Value), false, null);
        }

        if (!allowSplitTurn)
        {
            throw new InvalidOperationException("The latest user turn does not fit within the resolved prompt budget and split-turn compaction is disabled.");
        }

        var anchorTokens = groups[anchorGroupIndex.Value].Tokens;
        if (anchorTokens > availableForRetained)
        {
            if (allowOversizedAnchorReduction)
            {
                var reducedSuffixIndexes = BuildContiguousSuffix(groups, availableForRetained, minimumStartIndexExclusive: anchorGroupIndex.Value);
                return ([], reducedSuffixIndexes, true, anchorGroupIndex.Value);
            }

            throw new InvalidOperationException("The latest user message is too large to keep within the resolved prompt budget.");
        }

        var remainingBudget = availableForRetained - anchorTokens;
        var suffixIndexes = BuildContiguousSuffix(groups, remainingBudget, minimumStartIndexExclusive: anchorGroupIndex.Value);
        var isSplitTurn = suffixIndexes.Count > 0 || anchorGroupIndex.Value < groups.Count - 1;
        return ([anchorGroupIndex.Value], suffixIndexes, isSplitTurn, null);
    }

    private static (IReadOnlyList<int> TurnPrefixIndexes, IReadOnlyList<int> SuffixIndexes, bool IsSplitTurn, int? OversizedAnchorGroupIndex) BuildAnchorOnlyPlan(
        IReadOnlyList<MessageGroup> groups,
        int? anchorGroupIndex,
        long availableForRetained,
        bool allowOversizedAnchorReduction)
    {
        if (anchorGroupIndex is null)
        {
            return ([], [], false, null);
        }

        if (groups[anchorGroupIndex.Value].Tokens > availableForRetained)
        {
            if (allowOversizedAnchorReduction)
            {
                return ([], [], anchorGroupIndex.Value < groups.Count - 1, anchorGroupIndex.Value);
            }

            throw new InvalidOperationException("The latest user message is too large to keep within the resolved prompt budget.");
        }

        return ([anchorGroupIndex.Value], [], anchorGroupIndex.Value < groups.Count - 1, null);
    }

    private static IReadOnlyList<int> BuildContiguousSuffix(
        IReadOnlyList<MessageGroup> groups,
        long availableForRetained,
        int? maximumStartIndex = null,
        int? minimumStartIndexExclusive = null)
    {
        if (availableForRetained <= 0)
        {
            return [];
        }

        var startLimit = maximumStartIndex ?? groups.Count - 1;
        var minimumAllowedStart = (minimumStartIndexExclusive ?? -1) + 1;
        var keepTokens = 0L;
        var suffixIndexes = new Stack<int>();
        for (var index = groups.Count - 1; index >= minimumAllowedStart; index--)
        {
            var candidateTokens = keepTokens + groups[index].Tokens;
            if (candidateTokens > availableForRetained)
            {
                if (suffixIndexes.Count == 0 && index == groups.Count - 1 && index <= startLimit && groups[index].Tokens <= availableForRetained)
                {
                    suffixIndexes.Push(index);
                }

                break;
            }

            suffixIndexes.Push(index);
            keepTokens = candidateTokens;
            if (index == 0 || index - 1 < minimumAllowedStart)
            {
                break;
            }

            if (index - 1 < 0)
            {
                break;
            }
        }

        var suffix = suffixIndexes.ToList();
        if (suffix.Count == 0)
        {
            return [];
        }

        while (suffix.Count > 0 && suffix[0] > startLimit)
        {
            suffix.RemoveAt(0);
        }

        return suffix;
    }

    private static IReadOnlyList<int> BuildSuffixFromStart(IReadOnlyList<MessageGroup> groups, int startIndex)
        => Enumerable.Range(startIndex, groups.Count - startIndex).ToArray();

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
