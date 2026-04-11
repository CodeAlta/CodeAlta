namespace CodeAlta.Agent.LocalRuntime;

internal static class LocalAgentUsageFactory
{
    private static readonly string[] ContextWindowCapabilityKeys =
    [
        "contextWindow",
        "contextWindowTokens",
        "context_length",
        "contextLength",
        "inputTokenLimit",
        "maxInputTokens",
        "tokenLimit",
    ];

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
        var tokenLimit = GetContextWindowTokenLimit(modelInfo);
        var window = tokenLimit is not null
            ? new AgentWindowUsageSnapshot(
                CurrentTokens: null,
                TokenLimit: tokenLimit,
                MessageCount: null,
                Label: "Context window limit")
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
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.LocalProviderUsage,
            UpdatedAt: updatedAt);
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
                Label: usage.Window.Label),
        };
    }

    public static AgentSessionUsage? AttachModelInfo(AgentSessionUsage? usage, AgentModelInfo? modelInfo)
    {
        if (usage is null)
        {
            return null;
        }

        var tokenLimit = usage.Window?.TokenLimit ?? GetContextWindowTokenLimit(modelInfo);
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
            Label: label);
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

        var tokenLimit = usage?.Window?.TokenLimit ?? GetContextWindowTokenLimit(modelInfo);
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
            Label: resolvedLabel);
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
                ? AgentUsageSource.LocalProviderUsage
                : usage.Source,
            UpdatedAt = updatedAt,
        };
    }

    private static long? GetContextWindowTokenLimit(AgentModelInfo? modelInfo)
    {
        if (modelInfo?.Capabilities is not { Count: > 0 } capabilities)
        {
            return null;
        }

        foreach (var key in ContextWindowCapabilityKeys)
        {
            if (!capabilities.TryGetValue(key, out var rawValue))
            {
                continue;
            }

            if (TryConvertToInt64(rawValue, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static long? Sum(long? left, long? right)
        => left.HasValue || right.HasValue ? (left ?? 0) + (right ?? 0) : null;

    private static bool TryConvertToInt64(object? value, out long converted)
    {
        switch (value)
        {
            case byte byteValue:
                converted = byteValue;
                return true;
            case sbyte sbyteValue:
                converted = sbyteValue;
                return true;
            case short shortValue:
                converted = shortValue;
                return true;
            case ushort ushortValue:
                converted = ushortValue;
                return true;
            case int intValue:
                converted = intValue;
                return true;
            case uint uintValue:
                converted = (long)uintValue;
                return true;
            case long longValue:
                converted = longValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                converted = (long)ulongValue;
                return true;
            case float floatValue when floatValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)floatValue;
                return true;
            case double doubleValue when doubleValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)doubleValue;
                return true;
            case decimal decimalValue when decimalValue is >= long.MinValue and <= long.MaxValue:
                converted = (long)decimalValue;
                return true;
            case string stringValue when long.TryParse(stringValue, out var parsed):
                converted = parsed;
                return true;
            default:
                converted = default;
                return false;
        }
    }
}
