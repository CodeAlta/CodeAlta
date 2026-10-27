using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Chooses the <c>text.verbosity</c> of a Responses request: the provider's setting, else the model's own default.
/// </summary>
internal static class OpenAITextVerbosity
{
    // The default verbosity Codex gives the models of its catalog (codex-rs/models-manager/models.json), and of the
    // models CodeAlta lists for the ChatGPT subscription. Each of them takes low, medium and high.
    private static readonly FrozenDictionary<string, string> KnownModelDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6.1-sol"] = "low",
        ["gpt-6-astra"] = "low",
        ["gpt-6-sol"] = "low",
        ["gpt-6-luna"] = "low",
        ["gpt-5.6-sol"] = "low",
        ["gpt-5.6-terra"] = "low",
        ["gpt-5.6-luna"] = "low",
        ["gpt-5.5"] = "low",
        ["gpt-5.4"] = "low",
        ["gpt-5.4-mini"] = "medium",
        ["gpt-5.3-codex"] = "low",
        ["gpt-5.2"] = "low",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Models from before GPT-5 take no verbosity but medium: a request with another value fails.
    private static readonly string[] ModelPrefixesWithoutVerbosity = ["gpt-4", "gpt-3.5", "chatgpt-4o", "o1", "o3", "o4"];

    /// <summary>
    /// Gets the verbosity of a Codex subscription request: the provider's setting, else the model's default; null
    /// when the model takes none, or when neither is known (the endpoint then uses its own default).
    /// </summary>
    public static string? ForCodexSubscription(string? configured, string? modelId, AgentModelInfo? model)
    {
        if (GetSupport(model, modelId) == false)
        {
            return null;
        }

        return Normalize(configured) ?? GetModelDefault(model, modelId);
    }

    /// <summary>
    /// Gets the verbosity of a request to an OpenAI Responses endpoint. The provider's setting goes to every model
    /// that is not known to reject it; without a setting, the model's own default goes only to the OpenAI API, for the
    /// models whose default is known: another endpoint that speaks the protocol may not take the field.
    /// </summary>
    public static string? ForResponses(string? configured, string? modelId, AgentModelInfo? model, bool isOfficialEndpoint)
    {
        var support = GetSupport(model, modelId);
        if (support == false)
        {
            return null;
        }

        if (Normalize(configured) is { } explicitVerbosity)
        {
            return explicitVerbosity;
        }

        return isOfficialEndpoint && support == true ? GetModelDefault(model, modelId) : null;
    }

    private static bool? GetSupport(AgentModelInfo? model, string? modelId)
    {
        if ((GetBooleanCapability(model, "supportVerbosity") ?? GetBooleanCapability(model, "supportsTextVerbosity")) is { } capability)
        {
            return capability;
        }

        if (FindKnownDefault(modelId ?? model?.Id) is not null)
        {
            return true;
        }

        var id = NormalizeModelId(modelId ?? model?.Id);
        if (id is not null)
        {
            foreach (var prefix in ModelPrefixesWithoutVerbosity)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return null;
    }

    private static string? GetModelDefault(AgentModelInfo? model, string? modelId)
        => Normalize(GetStringCapability(model, "defaultTextVerbosity")) ?? FindKnownDefault(modelId ?? model?.Id);

    private static string? FindKnownDefault(string? modelId)
        => NormalizeModelId(modelId) is { } id && KnownModelDefaults.TryGetValue(id, out var verbosity) ? verbosity : null;

    // A dated snapshot, such as gpt-5.5-2026-04-23, has the default of its model.
    private static string? NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var id = modelId.Trim();
        const int SnapshotSuffixLength = 11; // "-yyyy-MM-dd"
        if (id.Length > SnapshotSuffixLength &&
            id[^SnapshotSuffixLength] == '-' &&
            DateOnly.TryParseExact(id.AsSpan(id.Length - SnapshotSuffixLength + 1), "yyyy-MM-dd", out _))
        {
            id = id[..^SnapshotSuffixLength];
        }

        return id;
    }

    private static string? Normalize(string? verbosity)
        => verbosity?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null,
        };

    private static bool? GetBooleanCapability(AgentModelInfo? model, string key)
    {
        if (model?.Capabilities is null || !model.Capabilities.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
    }

    private static string? GetStringCapability(AgentModelInfo? model, string key)
    {
        if (model?.Capabilities is null || !model.Capabilities.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
    }
}
