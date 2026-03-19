using CodeAlta.Agent;
using XenoAtom.Terminal.UI;

internal sealed partial class CodeAltaApp
{
    private SessionUsagePresenter EnsureSessionUsagePresenter()
    {
        _sessionUsagePresenter ??= new SessionUsagePresenter(
            GetSelectedSessionUsage,
            GetUsageSelectionContext,
            markdown => (_threadPaneLayout?.App)?.Terminal.Clipboard.TrySetText(markdown),
            build => CreateComputedVisual(build));
        return _sessionUsagePresenter;
    }

    private AgentSessionUsage? GetSelectedSessionUsage()
    {
        var selectedThread = GetSelectedThread();
        return selectedThread is null
            ? null
            : EnsureThreadTab(selectedThread).Usage;
    }

    private (string BackendName, string? ModelName) GetUsageSelectionContext()
    {
        var selectedThread = GetSelectedThread();
        if (selectedThread is not null)
        {
            var tab = EnsureThreadTab(selectedThread);
            var backendState = _chatBackendStates[tab.BackendId.Value];
            return (backendState.DisplayName, tab.ModelId ?? backendState.SelectedModelId);
        }

        var backendId = GetPreferredBackendId();
        var draftBackendState = _chatBackendStates[backendId.Value];
        return (draftBackendState.DisplayName, draftBackendState.SelectedModelId);
    }

    internal static AgentSessionUsage MergeSessionUsage(AgentSessionUsage? current, AgentSessionUsage incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (current is null)
        {
            return incoming;
        }

        return new AgentSessionUsage(
            Window: MergeWindowUsage(current.Window, incoming.Window),
            LastOperation: MergeOperationUsage(current.LastOperation, incoming.LastOperation),
            RateLimits: MergeRateLimitSummary(current.RateLimits, incoming.RateLimits),
            Scope: incoming.Scope,
            Source: incoming.Source,
            UpdatedAt: incoming.UpdatedAt,
            Details: MergeSessionUsageDetails(current.Details, incoming.Details));
    }

    internal static string BuildSessionUsageIndicatorMarkup(AgentSessionUsage? usage)
        => SessionUsageFormatter.BuildIndicatorMarkup(usage);

    internal static string FormatSessionUsageSummary(AgentSessionUsage usage)
        => SessionUsageFormatter.FormatSummary(usage);

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

    private static AgentRateLimitSummary? MergeRateLimitSummary(AgentRateLimitSummary? current, AgentRateLimitSummary? incoming)
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
            Name = incoming.Name ?? current.Name,
            PlanType = incoming.PlanType ?? current.PlanType,
            Primary = MergeRateLimitWindow(current.Primary, incoming.Primary),
            Secondary = MergeRateLimitWindow(current.Secondary, incoming.Secondary),
            Label = incoming.Label ?? current.Label,
        };
    }

    private static AgentRateLimitWindow? MergeRateLimitWindow(AgentRateLimitWindow? current, AgentRateLimitWindow? incoming)
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
            UsedPercent = incoming.UsedPercent ?? current.UsedPercent,
            ResetsAt = incoming.ResetsAt ?? current.ResetsAt,
            WindowDurationMinutes = incoming.WindowDurationMinutes ?? current.WindowDurationMinutes,
        };
    }

    private static AgentSessionUsageDetails? MergeSessionUsageDetails(AgentSessionUsageDetails? current, AgentSessionUsageDetails? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        if (current is null)
        {
            return incoming;
        }

        return (current, incoming) switch
        {
            (CodexSessionUsageDetails currentCodex, CodexSessionUsageDetails incomingCodex) => currentCodex with
            {
                LastTurnUsage = incomingCodex.LastTurnUsage ?? currentCodex.LastTurnUsage,
                TotalUsage = incomingCodex.TotalUsage ?? currentCodex.TotalUsage,
                ModelContextWindow = incomingCodex.ModelContextWindow ?? currentCodex.ModelContextWindow,
                RateLimits = incomingCodex.RateLimits ?? currentCodex.RateLimits,
            },
            (CopilotSessionUsageDetails currentCopilot, CopilotSessionUsageDetails incomingCopilot) => currentCopilot with
            {
                LastAssistantUsage = incomingCopilot.LastAssistantUsage ?? currentCopilot.LastAssistantUsage,
                LastCompaction = incomingCopilot.LastCompaction ?? currentCopilot.LastCompaction,
                QuotaSnapshots = incomingCopilot.QuotaSnapshots ?? currentCopilot.QuotaSnapshots,
            },
            _ => incoming
        };
    }

    internal static string BuildSessionUsageMarkdown(AgentSessionUsage? usage, string backendName, string? modelName)
        => SessionUsageFormatter.BuildMarkdown(usage, backendName, modelName);

    internal static string? FormatOperationPopupText(AgentOperationUsageSnapshot usage)
        => SessionUsageFormatter.FormatOperationPopupText(usage);
}
