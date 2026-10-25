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
