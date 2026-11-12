using System.Text.Json;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// The reasoning efforts an OpenAI-compatible endpoint gives a model in its list of <c>/models</c>, beyond the fields of
/// OpenAI, in the shapes endpoints use:
/// <list type="bullet">
/// <item>OpenRouter's, which other endpoints follow (TensorFold): <c>{"reasoning":{"supported_efforts":["low","high","max"],
/// "default_effort":"high","default_enabled":false}}</c>;</item>
/// <item>Kimi's: <c>{"reasoning":{"type":"only","effort":{"valid":["low","high","max"],"default":"high"}}}</c>, where a
/// <c>type</c> without <c>effort</c> is a model that thinks as it does and takes no effort.</item>
/// </list>
/// </summary>
internal static class OpenAIListedReasoning
{
    /// <summary>What an endpoint says of the reasoning of a model.</summary>
    /// <param name="Efforts">
    /// The efforts, from the lowest; empty for a model that takes none. <see cref="AgentReasoningEffort.None"/> is one of them
    /// when the endpoint lists <c>none</c>, or when the model reasons only when asked.
    /// </param>
    /// <param name="DefaultEffort">
    /// The effort a session starts with when its provider names none: <see cref="AgentReasoningEffort.None"/> for a model
    /// that reasons only when asked; null when the endpoint names no default, or one the model does not list.
    /// </param>
    /// <param name="OffByDefault">Whether the model reasons only when asked (OpenRouter's <c>default_enabled: false</c>).</param>
    /// <param name="NoneListed">Whether the endpoint lists <c>none</c>, which turns the reasoning of the model off.</param>
    public sealed record Listed(IReadOnlyList<AgentReasoningEffort> Efforts, AgentReasoningEffort? DefaultEffort, bool OffByDefault, bool NoneListed);

    /// <summary>
    /// Reads the efforts of a model of the list. A <c>reasoning</c> of another shape says nothing: the efforts of the model
    /// then come from elsewhere (models.dev), as for a model the endpoint describes no further.
    /// </summary>
    /// <param name="listed">The entry of the model in the data of <c>/models</c>.</param>
    /// <returns>What the endpoint says, a complete list of the efforts of the model; null when it says nothing.</returns>
    public static Listed? Read(JsonElement listed)
    {
        if (listed.ValueKind != JsonValueKind.Object || !listed.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        AgentReasoningEffort[] efforts;
        string? defaultName;
        var offByDefault = false;
        if (reasoning.TryGetProperty("supported_efforts", out var supported) && supported.ValueKind == JsonValueKind.Array)
        {
            efforts = ReadEfforts(supported);
            offByDefault = reasoning.TryGetProperty("default_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False;
            defaultName = ReadString(reasoning, "default_effort");
        }
        else if (reasoning.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object &&
                 effort.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.Array)
        {
            efforts = ReadEfforts(valid);
            defaultName = ReadString(effort, "default");
        }
        else if ((reasoning.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String) ||
                 (!reasoning.TryGetProperty("supported_efforts", out _) &&
                  (reasoning.TryGetProperty("mandatory", out _) || reasoning.TryGetProperty("default_enabled", out _))))
        {
            // Kimi's type alone, or OpenRouter's reasoning without supported_efforts: a model that does not expose
            // effort selection, and is sent no effort. A supported_efforts of null accepts any effort and says nothing.
            return new([], null, false, false);
        }
        else
        {
            return null;
        }

        var noneListed = efforts.Contains(AgentReasoningEffort.None);
        if (offByDefault)
        {
            // A model that reasons only when asked starts with None, which sends no effort unless the endpoint lists none.
            if (!noneListed) efforts = [AgentReasoningEffort.None, .. efforts];
            return new(efforts, AgentReasoningEffort.None, true, noneListed);
        }

        var defaultEffort = ParseEffort(defaultName) is { } parsed && efforts.Contains(parsed) ? parsed : (AgentReasoningEffort?)null;
        return new(efforts, defaultEffort, false, noneListed);
    }

    // An effort of the list: the levels of CodeAlta, and none.
    private static AgentReasoningEffort? ParseEffort(string? value)
        => string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? AgentReasoningEffort.None : OpenAIProviderSdkFactory.ParseReasoningEffort(value);

    private static AgentReasoningEffort[] ReadEfforts(JsonElement values)
        => values.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? ParseEffort(value.GetString()) : null)
            .OfType<AgentReasoningEffort>()
            .Distinct()
            .OrderBy(Rank)
            .ToArray();

    // From the lowest effort to the highest, whatever the values of the enumeration.
    private static int Rank(AgentReasoningEffort effort) => effort switch
    {
        AgentReasoningEffort.None => 0,
        AgentReasoningEffort.Minimal => 1,
        AgentReasoningEffort.Low => 2,
        AgentReasoningEffort.Medium => 3,
        AgentReasoningEffort.High => 4,
        AgentReasoningEffort.XHigh => 5,
        _ => 6,
    };

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
