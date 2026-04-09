namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal static class LocalAgentTokenBudgetResolver
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

    public static LocalAgentTokenBudget Resolve(
        AgentModelInfo? modelInfo,
        LocalAgentCompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var contextWindow = TryReadCapability(modelInfo, ContextWindowCapabilityKeys);
        var inputTokenLimit = TryReadCapability(modelInfo, InputTokenCapabilityKeys);
        var outputTokenLimit = TryReadCapability(modelInfo, OutputTokenCapabilityKeys);
        long? usablePromptBudget = null;

        if (contextWindow is not null)
        {
            usablePromptBudget = contextWindow.Value - settings.ReservedOutputTokens - settings.ReservedOverheadTokens;
        }

        if (inputTokenLimit is not null)
        {
            usablePromptBudget = usablePromptBudget is null
                ? inputTokenLimit
                : Math.Min(usablePromptBudget.Value, inputTokenLimit.Value);
        }

        return new LocalAgentTokenBudget(
            ContextWindow: contextWindow,
            InputTokenLimit: inputTokenLimit,
            OutputTokenLimit: outputTokenLimit,
            UsablePromptBudget: usablePromptBudget,
            ReservedOutputTokens: settings.ReservedOutputTokens,
            ReservedOverheadTokens: settings.ReservedOverheadTokens);
    }

    private static long? TryReadCapability(AgentModelInfo? modelInfo, IReadOnlyList<string> keys)
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
                converted = uintValue;
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
