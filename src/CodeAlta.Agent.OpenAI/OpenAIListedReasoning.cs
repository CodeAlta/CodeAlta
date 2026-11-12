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
    /// <summary>
    /// Reads the efforts of a model of the list. A <c>reasoning</c> of another shape says nothing: the efforts of the model
    /// then come from elsewhere (models.dev), as for a model the endpoint describes no further.
    /// </summary>
    /// <param name="listed">The entry of the model in the data of <c>/models</c>.</param>
    /// <param name="efforts">The efforts, from the lowest; empty for a model that takes none.</param>
    /// <param name="defaultEffort">
    /// The effort a turn that names none is sent; null for none, also when the endpoint says reasoning is off by default
    /// (<c>default_enabled: false</c>) or names a default the model does not list.
    /// </param>
    /// <returns>True when the endpoint gives the complete list of the efforts of the model.</returns>
    public static bool TryRead(JsonElement listed, out IReadOnlyList<AgentReasoningEffort> efforts, out AgentReasoningEffort? defaultEffort)
    {
        efforts = [];
        defaultEffort = null;
        if (listed.ValueKind != JsonValueKind.Object || !listed.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? defaultName;
        if (reasoning.TryGetProperty("supported_efforts", out var supported) && supported.ValueKind == JsonValueKind.Array)
        {
            efforts = ReadEfforts(supported);
            defaultName = reasoning.TryGetProperty("default_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False
                ? null
                : ReadString(reasoning, "default_effort");
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
            return true;
        }
        else
        {
            return false;
        }

        if (OpenAIProviderSdkFactory.ParseReasoningEffort(defaultName) is { } parsed && efforts.Contains(parsed))
        {
            defaultEffort = parsed;
        }

        return true;
    }

    private static AgentReasoningEffort[] ReadEfforts(JsonElement values)
        => values.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? OpenAIProviderSdkFactory.ParseReasoningEffort(value.GetString()) : null)
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
