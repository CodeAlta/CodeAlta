using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent.Runtime;

internal static class AgentUsageFactory
{
    public static AgentSessionUsage CreateOperationUsage(
        string? modelId,
        AgentModelInfo? modelInfo,
        long? inputTokens,
        long? outputTokens,
        long? totalTokens,
        long? cachedInputTokens,
        long? reasoningTokens,
        DateTimeOffset updatedAt)
    {
        var currentTokens = totalTokens ?? Sum(inputTokens, outputTokens);
        var budget = GetModelTokenBudget(modelInfo);
        var tokenLimit = budget.InputContextLimit;
        var window = currentTokens is not null || tokenLimit is not null
            ? new AgentWindowUsageSnapshot(
                CurrentTokens: currentTokens,
                TokenLimit: tokenLimit,
                MessageCount: null,
                Label: tokenLimit is not null ? "Active context window" : "Estimated active context",
                TotalContextEnvelope: budget.TotalContextEnvelope,
                MaxOutputTokens: budget.MaxOutputTokens)
            : null;

        return new AgentSessionUsage(
            Window: window,
            LastOperation: new AgentOperationUsageSnapshot(
                Model: string.IsNullOrWhiteSpace(modelId) ? modelInfo?.Id : modelId,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CachedInputTokens: cachedInputTokens,
                ReasoningTokens: reasoningTokens,
                Label: string.IsNullOrWhiteSpace(modelId)
                    ? null
                    : $"{modelId}: {inputTokens ?? 0}/{outputTokens ?? 0} tokens"),
            Scope: window is null ? AgentUsageScope.LastOperation : AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: updatedAt);
    }

    public static AgentSessionUsage? RecoverUsageFromHistory(IReadOnlyList<AgentEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        AgentSessionUsage? usage = null;
        foreach (var @event in history)
        {
            if (@event is AgentSessionUpdateEvent { Usage: { } updateUsage })
            {
                usage = MergeUsage(usage, updateUsage);
            }
        }

        return usage;
    }

    public static AgentSessionUsage? AttachMessageCount(AgentSessionUsage? usage, int? messageCount)
    {
        if (usage?.Window is null || messageCount is not >= 0)
        {
            return usage;
        }

        return usage with
        {
            Window = new AgentWindowUsageSnapshot(
                CurrentTokens: usage.Window.CurrentTokens,
                TokenLimit: usage.Window.TokenLimit,
                MessageCount: messageCount,
                Label: usage.Window.Label,
                TotalContextEnvelope: usage.Window.TotalContextEnvelope,
                MaxOutputTokens: usage.Window.MaxOutputTokens),
        };
    }

    public static AgentSessionUsage? AttachModelInfo(AgentSessionUsage? usage, AgentModelInfo? modelInfo)
    {
        if (usage is null)
        {
            return null;
        }

        var budget = GetModelTokenBudget(modelInfo);
        var tokenLimit = usage.Window?.TokenLimit ?? budget.InputContextLimit;
        if (tokenLimit is null)
        {
            return usage;
        }

        var currentTokens = usage.Window?.CurrentTokens;
        var label = usage.Window?.Label ?? "Context window limit";
        var window = new AgentWindowUsageSnapshot(
            CurrentTokens: currentTokens,
            TokenLimit: tokenLimit,
            MessageCount: usage.Window?.MessageCount,
            Label: label,
            TotalContextEnvelope: usage.Window?.TotalContextEnvelope ?? budget.TotalContextEnvelope,
            MaxOutputTokens: usage.Window?.MaxOutputTokens ?? budget.MaxOutputTokens);
        if (Equals(window, usage.Window))
        {
            return usage;
        }

        return usage with
        {
            Window = window,
            Scope = usage.Scope is AgentUsageScope.Unknown && currentTokens is not null
                ? AgentUsageScope.CurrentWindow
                : usage.Scope,
        };
    }

    public static AgentSessionUsage? AttachWindowEstimate(
        AgentSessionUsage? usage,
        AgentModelInfo? modelInfo,
        long? currentTokens,
        int? messageCount,
        DateTimeOffset updatedAt,
        string? label = null)
    {
        if (messageCount is not >= 0 && currentTokens is null)
        {
            return usage;
        }

        var budget = GetModelTokenBudget(modelInfo);
        var tokenLimit = usage?.Window?.TokenLimit ?? budget.InputContextLimit;
        if (currentTokens is null && tokenLimit is null)
        {
            return usage;
        }

        var resolvedLabel = string.IsNullOrWhiteSpace(label)
            ? "Estimated active context"
            : label;
        var window = new AgentWindowUsageSnapshot(
            CurrentTokens: currentTokens,
            TokenLimit: tokenLimit,
            MessageCount: messageCount,
            Label: resolvedLabel,
            TotalContextEnvelope: usage?.Window?.TotalContextEnvelope ?? budget.TotalContextEnvelope,
            MaxOutputTokens: usage?.Window?.MaxOutputTokens ?? budget.MaxOutputTokens);
        if (usage is not null &&
            Equals(window, usage.Window) &&
            usage.Scope == AgentUsageScope.CurrentWindow)
        {
            return usage;
        }

        return (usage ?? new AgentSessionUsage()) with
        {
            Window = window,
            Scope = AgentUsageScope.CurrentWindow,
            Source = usage?.Source is AgentUsageSource.Unknown or null
                ? AgentUsageSource.ProviderUsage
                : usage.Source,
            UpdatedAt = updatedAt,
        };
    }

    private static AgentTokenBudget GetModelTokenBudget(AgentModelInfo? modelInfo)
    {
        return AgentTokenBudgetResolver.Resolve(modelInfo, AgentCompactionSettings.Default);
    }

    private static long? Sum(long? left, long? right)
        => left.HasValue || right.HasValue ? (left ?? 0) + (right ?? 0) : null;

    private static AgentSessionUsage MergeUsage(AgentSessionUsage? current, AgentSessionUsage incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        return new AgentSessionUsage(
            Window: MergeWindowUsage(current.Window, incoming.Window),
            LastOperation: MergeOperationUsage(current.LastOperation, incoming.LastOperation),
            RateLimits: incoming.RateLimits ?? current.RateLimits,
            Scope: incoming.Scope,
            Source: incoming.Source,
            UpdatedAt: incoming.UpdatedAt,
            Details: incoming.Details ?? current.Details);
    }

    private static AgentWindowUsageSnapshot? MergeWindowUsage(AgentWindowUsageSnapshot? current, AgentWindowUsageSnapshot? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            CurrentTokens = incoming.CurrentTokens ?? current.CurrentTokens,
            TokenLimit = incoming.TokenLimit ?? current.TokenLimit,
            MessageCount = incoming.MessageCount ?? current.MessageCount,
            Label = incoming.Label ?? current.Label,
            TotalContextEnvelope = incoming.TotalContextEnvelope ?? current.TotalContextEnvelope,
            MaxOutputTokens = incoming.MaxOutputTokens ?? current.MaxOutputTokens,
        };
    }

    private static AgentOperationUsageSnapshot? MergeOperationUsage(AgentOperationUsageSnapshot? current, AgentOperationUsageSnapshot? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return current with
        {
            Model = incoming.Model ?? current.Model,
            InputTokens = incoming.InputTokens ?? current.InputTokens,
            OutputTokens = incoming.OutputTokens ?? current.OutputTokens,
            CacheReadTokens = incoming.CacheReadTokens ?? current.CacheReadTokens,
            CacheWriteTokens = incoming.CacheWriteTokens ?? current.CacheWriteTokens,
            CachedInputTokens = incoming.CachedInputTokens ?? current.CachedInputTokens,
            ReasoningTokens = incoming.ReasoningTokens ?? current.ReasoningTokens,
            Cost = incoming.Cost ?? current.Cost,
            DurationMs = incoming.DurationMs ?? current.DurationMs,
            Initiator = incoming.Initiator ?? current.Initiator,
            ParentToolCallId = incoming.ParentToolCallId ?? current.ParentToolCallId,
            ReasoningEffort = incoming.ReasoningEffort ?? current.ReasoningEffort,
            Label = incoming.Label ?? current.Label,
        };
    }

}
