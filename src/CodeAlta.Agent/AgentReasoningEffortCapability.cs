using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>
/// Says whether the reasoning efforts a model lists are all it has. A provider sets it when it knows its list is
/// complete and sends no effort it does not list: a model that lists none then runs without one, whatever a session asked.
/// </summary>
public static class AgentReasoningEffortCapability
{
    /// <summary>The key of <see cref="AgentModelInfo.Capabilities"/> that holds <c>true</c> when the list is complete.</summary>
    public const string CompleteListKey = "reasoningEffortsComplete";

    /// <summary>
    /// The key of <see cref="AgentModelInfo.Capabilities"/> that names the level the highest effort of a session,
    /// <see cref="AgentReasoningEffort.XHigh"/>, is sent as: <c>xhigh</c>, or <c>max</c> for a model that has no
    /// <c>xhigh</c>. Set from the catalog of models.dev for a model whose provider lists no effort.
    /// </summary>
    public const string HighestEffortKey = "highestReasoningEffort";

    /// <summary>
    /// The key of <see cref="AgentModelInfo.Capabilities"/> that holds <c>true</c> for a model whose endpoint says it reasons
    /// only when asked (OpenRouter's <c>default_enabled: false</c>): a session that names no effort starts with
    /// <see cref="AgentReasoningEffort.None"/>.
    /// </summary>
    public const string OffByDefaultKey = "reasoningOffByDefault";

    /// <summary>
    /// The key of <see cref="AgentModelInfo.Capabilities"/> that holds <c>true</c> for a model whose endpoint lists the
    /// effort <c>none</c>: <see cref="AgentReasoningEffort.None"/> is then sent as <c>none</c>, which turns its reasoning off,
    /// instead of no effort at all.
    /// </summary>
    public const string NoneListedKey = "reasoningNoneListed";

    /// <summary>Returns whether the endpoint of <paramref name="model"/> says it reasons only when asked.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns>True only when the endpoint says so.</returns>
    public static bool IsOffByDefault(AgentModelInfo? model) => IsTrue(model, OffByDefaultKey);

    /// <summary>Returns whether the endpoint of <paramref name="model"/> lists the effort <c>none</c>.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns>True only when the endpoint lists it.</returns>
    public static bool IsNoneListed(AgentModelInfo? model) => IsTrue(model, NoneListedKey);

    private static bool IsTrue(AgentModelInfo? model, string key)
        => model?.Capabilities is { } capabilities && capabilities.TryGetValue(key, out var value)
           && value is true or JsonElement { ValueKind: JsonValueKind.True };

    /// <summary>Returns whether the provider says that the efforts <paramref name="model"/> lists are all it has.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns>True only when the provider says so; false when it does not, or for an unknown model.</returns>
    public static bool IsListComplete(AgentModelInfo? model)
        => model?.Capabilities is { } capabilities && capabilities.TryGetValue(CompleteListKey, out var value)
           && value is true or JsonElement { ValueKind: JsonValueKind.True };

    /// <summary>Returns the level the highest effort of <paramref name="model"/> is sent as, when it is known.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns><c>xhigh</c> or <c>max</c>; null when the model does not say.</returns>
    public static string? GetHighestEffort(AgentModelInfo? model)
    {
        if (model?.Capabilities is not { } capabilities || !capabilities.TryGetValue(HighestEffortKey, out var value))
        {
            return null;
        }

        // A capability read back from a saved list may be a JSON string.
        var level = value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return level is "xhigh" or "max" ? level : null;
    }
}
