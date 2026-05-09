using CodeAlta.Agent;

namespace CodeAlta.LiveTool;

/// <summary>
/// Represents a provider/model/reasoning tuple exposed by <c>alta</c> commands.
/// </summary>
public sealed record AltaModelSelection
{
    /// <summary>Gets the provider/backend key.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the model identifier, when selected.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the reasoning effort, when selected.</summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>Gets the compact model reference.</summary>
    public required string ModelRef { get; init; }
}

/// <summary>
/// User-supplied model selection inputs shared by model-sensitive commands.
/// </summary>
public sealed record AltaModelSelectionRequest
{
    /// <summary>Gets an explicit compact model ref.</summary>
    public string? ModelRef { get; init; }

    /// <summary>Gets a thread id whose model should be inherited.</summary>
    public string? SameModelAsThreadId { get; init; }

    /// <summary>Gets an explicit provider override.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets an explicit model override.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets an explicit reasoning override.</summary>
    public AgentReasoningEffort? ReasoningEffort { get; init; }
}

/// <summary>
/// Parses and formats compact model selection references.
/// </summary>
public static class AltaModelRef
{
    /// <summary>Formats a compact model reference.</summary>
    public static string Format(string providerKey, string? modelId, AgentReasoningEffort? reasoningEffort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var value = string.IsNullOrWhiteSpace(modelId) ? providerKey.Trim() : providerKey.Trim() + ":" + modelId.Trim();
        return reasoningEffort is null ? value : value + "@" + ToWireName(reasoningEffort.Value);
    }

    /// <summary>Attempts to parse a compact model reference.</summary>
    public static bool TryParse(string? modelRef, out AltaModelSelection selection, out string? error)
    {
        selection = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            error = "Model ref is required.";
            return false;
        }

        var trimmed = modelRef.Trim();
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            error = "Model ref must use '<provider-key>:<model-id>[@<reasoning>]'.";
            return false;
        }

        var providerKey = trimmed[..colon].Trim();
        var modelAndReasoning = trimmed[(colon + 1)..].Trim();
        string modelId;
        AgentReasoningEffort? reasoning = null;
        var at = modelAndReasoning.LastIndexOf('@');
        if (at >= 0)
        {
            modelId = modelAndReasoning[..at].Trim();
            var reasoningText = modelAndReasoning[(at + 1)..].Trim();
            if (!TryParseReasoning(reasoningText, out var parsedReasoning))
            {
                error = $"Reasoning effort '{reasoningText}' is not valid. Use minimal, low, medium, high, xhigh, or none.";
                return false;
            }

            reasoning = parsedReasoning;
        }
        else
        {
            modelId = modelAndReasoning;
        }

        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(modelId))
        {
            error = "Model ref must include a non-empty provider key and model id.";
            return false;
        }

        selection = new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = Format(providerKey, modelId, reasoning),
        };
        return true;
    }

    /// <summary>Attempts to parse a reasoning effort value.</summary>
    public static bool TryParseReasoning(string? value, out AgentReasoningEffort? reasoningEffort)
    {
        reasoningEffort = value?.Trim().ToLowerInvariant() switch
        {
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "none" => AgentReasoningEffort.None,
            _ => null,
        };
        return reasoningEffort is not null;
    }

    /// <summary>Converts a reasoning effort to its command-line wire name.</summary>
    public static string ToWireName(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.Minimal => "minimal",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            AgentReasoningEffort.None => "none",
            _ => effort.ToString().ToLowerInvariant(),
        };
}
