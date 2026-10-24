namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentTokenBudgetResolver
{
    private static readonly string[] ContextWindowCapabilityKeys =
    [
        "contextWindow",
        "contextWindowTokens",
        "context_length",
        "contextLength",
        "tokenLimit",
    ];

    private static readonly string[] InputTokenCapabilityKeys =
    [
        "inputTokenLimit",
        "maxInputTokens",
    ];

    private static readonly string[] OutputTokenCapabilityKeys =
    [
        "outputTokenLimit",
        "maxOutputTokens",
        "maxTokens",
    ];

    public static AgentTokenBudget Resolve(
        AgentModelInfo? modelInfo,
        AgentCompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var contextWindow = TryReadCapability(modelInfo, ContextWindowCapabilityKeys);
        var inputTokenLimit = TryReadCapability(modelInfo, InputTokenCapabilityKeys);
        var outputTokenLimit = TryReadCapability(modelInfo, OutputTokenCapabilityKeys);
        var inputContextLimit = ResolveInputContextLimit(contextWindow, inputTokenLimit, outputTokenLimit);

        return new AgentTokenBudget(
            TotalContextEnvelope: contextWindow,
            InputContextLimit: inputContextLimit,
            MaxOutputTokens: outputTokenLimit);
    }

    /// <summary>
    /// Resolves the budget of a compaction: the one of the model, with an assumed input-context limit when the model
    /// reports none. An overflow assumes the input size of the last request the provider reported, a size the model
    /// accepted; anything else, or an overflow without such a size, assumes
    /// <see cref="AgentCompactionSettings.DefaultAssumedInputContextLimit"/>.
    /// </summary>
    public static AgentTokenBudget ResolveForCompaction(
        AgentModelInfo? modelInfo,
        AgentCompactionSettings settings,
        AgentCompactionTrigger trigger,
        long? lastReportedInputTokens)
    {
        var budget = Resolve(modelInfo, settings);
        if (budget.InputContextLimit is not null)
        {
            return budget;
        }

        var assumedInputContextLimit = trigger is AgentCompactionTrigger.Overflow && lastReportedInputTokens is > 0
            ? lastReportedInputTokens.Value
            : AgentCompactionSettings.DefaultAssumedInputContextLimit;
        return budget with { InputContextLimit = assumedInputContextLimit, IsInputContextLimitAssumed = true };
    }

    internal static long? ResolveInputContextLimit(long? contextWindow, long? inputTokenLimit, long? outputTokenLimit)
    {
        if (inputTokenLimit is > 0)
        {
            return inputTokenLimit.Value;
        }

        if (contextWindow is not > 0)
        {
            return null;
        }

        if (outputTokenLimit is > 0 && outputTokenLimit.Value < contextWindow.Value)
        {
            return Math.Max(contextWindow.Value - outputTokenLimit.Value, 1L);
        }

        return contextWindow.Value;
    }

    internal static long? TryReadCapability(AgentModelInfo? modelInfo, IReadOnlyList<string> keys)
    {
        if (modelInfo?.Capabilities is not { Count: > 0 } capabilities)
        {
            return null;
        }

        foreach (var key in keys)
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
