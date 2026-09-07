using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Agent.OpenAI.Codex;

internal readonly record struct CodexSubscriptionModelCapabilities(
    bool SupportsReasoningSummaries,
    bool SupportsVerbosity,
    bool SupportsImageDetailOriginal,
    bool UseResponsesLite,
    bool SupportsPriorityServiceTier)
{
    public static CodexSubscriptionModelCapabilities FromModel(AgentModelInfo? model)
        => new(
            GetBoolean(model, "supportsReasoningSummaries", fallback: true),
            GetBoolean(model, "supportVerbosity", fallback: true),
            GetBoolean(model, "supportsImageDetailOriginal", fallback: true),
            GetBoolean(model, "useResponsesLite", fallback: false),
            SupportsPriorityTier(model));

    private static bool SupportsPriorityTier(AgentModelInfo? model)
    {
        if (model?.Capabilities is null || !model.Capabilities.TryGetValue("serviceTiers", out var value))
        {
            return false;
        }

        return value switch
        {
            IEnumerable<string> tiers => tiers.Contains("priority", StringComparer.Ordinal),
            // Agent capability JSON round-trips materialize arrays as object[].
            IEnumerable<object?> tiers => tiers.OfType<string>().Contains("priority", StringComparer.Ordinal),
            JsonElement { ValueKind: JsonValueKind.Array } tiers => tiers.EnumerateArray().Any(
                static tier => tier.ValueKind is JsonValueKind.String && tier.GetString() == "priority"),
            _ => false,
        };
    }

    private static bool GetBoolean(AgentModelInfo? model, string key, bool fallback)
    {
        if (model?.Capabilities is null || !model.Capabilities.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => fallback,
        };
    }
}
