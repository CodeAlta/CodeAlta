namespace CodeAlta.Agent.Runtime.Compaction;

internal static class AgentTokenBudgetResolver
{
    /// <summary>
    /// The capability that gives the share of the input-context limit at which a session of the model compacts, at
    /// most: a model whose price rises past a size compacts before it (<see cref="ModelCatalog.AgentModelKnownLimit"/>).
    /// </summary>
    public const string CompactionRatioCapabilityKey = "compactionRatio";

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

        var capabilities = modelInfo?.Capabilities;
        var contextWindow = TryReadCapability(capabilities, ContextWindowCapabilityKeys);
        var inputTokenLimit = TryReadCapability(capabilities, InputTokenCapabilityKeys);
        var outputTokenLimit = TryReadCapability(capabilities, OutputTokenCapabilityKeys);
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

    /// <summary>
    /// The size of the active context at which a session compacts automatically: the ratio of the settings, or the
    /// smaller one of the model (<see cref="CompactionRatioCapabilityKey"/>), of the input-context limit.
    /// </summary>
    public static long ResolveCompactionThreshold(
        long inputContextLimit,
        AgentModelInfo? modelInfo,
        AgentCompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ratio = settings.Ratio;
        if (modelInfo?.Capabilities is { } capabilities &&
            capabilities.TryGetValue(CompactionRatioCapabilityKey, out var rawRatio) &&
            TryConvertToDouble(rawRatio, out var modelRatio) &&
            modelRatio is > 0 and <= 1 &&
            modelRatio < ratio)
        {
            ratio = modelRatio;
        }

        return Math.Max((long)Math.Floor(inputContextLimit * ratio), 1L);
    }

    // A capability read back from a cache can be another number type, or JSON.
    private static bool TryConvertToDouble(object? value, out double converted)
    {
        switch (value)
        {
            case double doubleValue:
                converted = doubleValue;
                return true;
            case float floatValue:
                converted = floatValue;
                return true;
            case decimal decimalValue:
                converted = (double)decimalValue;
                return true;
            case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element:
                return element.TryGetDouble(out converted);
            case string stringValue:
                return double.TryParse(stringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out converted);
            default:
                converted = default;
                return false;
        }
    }

    /// <summary>The input-context limit that capabilities of a model give, as <see cref="Resolve"/> reads it; null when they give none.</summary>
    internal static long? ResolveInputContextLimit(IReadOnlyDictionary<string, object?>? capabilities)
        => ResolveInputContextLimit(
            TryReadCapability(capabilities, ContextWindowCapabilityKeys),
            TryReadCapability(capabilities, InputTokenCapabilityKeys),
            TryReadCapability(capabilities, OutputTokenCapabilityKeys));

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

    internal static long? TryReadCapability(IReadOnlyDictionary<string, object?>? capabilities, IReadOnlyList<string> keys)
    {
        if (capabilities is not { Count: > 0 })
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

    internal static bool TryConvertToInt64(object? value, out long converted)
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
