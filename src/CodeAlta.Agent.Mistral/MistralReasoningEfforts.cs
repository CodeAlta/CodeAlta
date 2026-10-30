using System.Collections.Frozen;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// The reasoning efforts of the models of Mistral, which its model list does not give: it says only whether a model
/// reasons. Each model takes its own values and refuses the others (HTTP 400), on the chat API and on the Responses API
/// alike.
/// </summary>
internal static class MistralReasoningEfforts
{
    // What a model takes when neither the endpoint nor models.dev says better: most of the reasoning models of Mistral
    // (Small 4, Medium 3.5, Magistral) take none and high, and nothing else.
    public static readonly IReadOnlyList<AgentReasoningEffort> Default = [AgentReasoningEffort.None, AgentReasoningEffort.High];

    // The models that take other efforts, as both APIs answered (live probe of 2026-10-29). Large 4 and GLM 5.3 refuse
    // none and reason when the effort is left out; models.dev gives Large 4 none and high.
    private static readonly AgentReasoningEffort[] LowMediumHigh = [AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High];
    private static readonly AgentReasoningEffort[] LowHighMax = [AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.Max];
    private static readonly FrozenDictionary<string, AgentReasoningEffort[]> Known = new Dictionary<string, AgentReasoningEffort[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["mistral-large-4"] = LowMediumHigh,
        ["mistral-large-4-0"] = LowMediumHigh,
        ["zai-glm-5-3"] = LowHighMax,
        ["zai-glm-5-3-flash"] = LowHighMax,
        ["zai-glm-5"] = LowHighMax,
        ["zai-glm-latest"] = LowHighMax,
        ["glm-5-2"] =
        [
            AgentReasoningEffort.None, AgentReasoningEffort.Minimal, AgentReasoningEffort.Low, AgentReasoningEffort.Medium,
            AgentReasoningEffort.High, AgentReasoningEffort.XHigh, AgentReasoningEffort.Max,
        ],
        ["deepseek-v4-1-flash-1017"] =
        [
            AgentReasoningEffort.None, AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.XHigh,
            AgentReasoningEffort.Max,
        ],
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the efforts known for a model or one of its aliases; null when the model is not known.</summary>
    public static IReadOnlyList<AgentReasoningEffort>? Find(string modelId, IEnumerable<string>? aliases = null)
    {
        if (Known.TryGetValue(modelId, out var efforts))
        {
            return efforts;
        }

        foreach (var alias in aliases ?? [])
        {
            if (Known.TryGetValue(alias, out efforts))
            {
                return efforts;
            }
        }

        return null;
    }

    /// <summary>Returns the value of the <c>reasoning_effort</c> of Mistral for an effort.</summary>
    public static string ToValue(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.None => "none",
            AgentReasoningEffort.Minimal => "minimal",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            AgentReasoningEffort.Max => "max",
            _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort."),
        };

    /// <summary>
    /// Returns the effort to send for a turn, as Mistral names it: the effort asked when the model takes it, none
    /// included, so that a model that would reason by default does not. Null sends none, when the effort or the
    /// efforts of the model are not known.
    /// </summary>
    public static string? Resolve(AgentTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ReasoningEffort is { } effort &&
               request.ModelInfo?.SupportedReasoningEfforts is { } supported &&
               supported.Contains(effort)
            ? ToValue(effort)
            : null;
    }
}
