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

    /// <summary>Returns whether the provider says that the efforts <paramref name="model"/> lists are all it has.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns>True only when the provider says so; false when it does not, or for an unknown model.</returns>
    public static bool IsListComplete(AgentModelInfo? model)
        => model?.Capabilities is { } capabilities && capabilities.TryGetValue(CompleteListKey, out var value)
           && value is true or JsonElement { ValueKind: JsonValueKind.True };
}
