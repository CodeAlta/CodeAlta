using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent;

/// <summary>
/// Describes an available model in an agent provider.
/// </summary>
/// <param name="Id">The provider model identifier.</param>
/// <param name="DisplayName">Optional display name.</param>
/// <param name="Description">Optional model description.</param>
/// <param name="Provider">Optional provider identifier (e.g. "openai").</param>
/// <param name="DefaultReasoningEffort">Optional default reasoning effort for this model.</param>
/// <param name="SupportedReasoningEfforts">Optional set of reasoning effort values supported by this model.</param>
/// <param name="Capabilities">
/// Optional provider-defined capabilities metadata. The shape is provider-specific.
/// </param>
public sealed record AgentModelInfo(
    string Id,
    string? DisplayName = null,
    string? Description = null,
    string? Provider = null,
    AgentReasoningEffort? DefaultReasoningEffort = null,
    IReadOnlyList<AgentReasoningEffort>? SupportedReasoningEfforts = null,
    [property: JsonConverter(typeof(AgentObjectDictionaryJsonConverter))]
    IReadOnlyDictionary<string, object?>? Capabilities = null)
{
    /// <summary>
    /// Gets the choices of the model other than its reasoning effort, such as the size of its context or a fast mode,
    /// with the values they have now: set through <see cref="IModelProviderModelOptions"/>. Null or empty for a model
    /// that has none, or a provider that offers none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentModelOption>? Options { get; init; }

    /// <summary>
    /// Gets the sizes of context, in input tokens, a session can use the model with, from the smallest, as its provider
    /// gives them: the size of the model (<see cref="GetContextSize"/>) is one of them. Null for a model that has one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<long>? ContextSizes { get; init; }

    /// <summary>Gets the size of context of the model, in input tokens: the input it takes at most, null when it is not known.</summary>
    /// <returns>The size, or null.</returns>
    public long? GetContextSize() => AgentTokenBudgetResolver.Resolve(this, AgentCompactionSettings.Default).InputContextLimit;

    /// <summary>
    /// Gets the model as a session uses it with a size of context: one of <see cref="ContextSizes"/> becomes the input it
    /// takes at most, which its usage and its compaction follow. Another size, or null, leaves the model as it is.
    /// </summary>
    /// <param name="size">The size, in input tokens, or null.</param>
    /// <returns>The model with that size.</returns>
    public AgentModelInfo WithContextSize(long? size)
    {
        if (size is not { } tokens || ContextSizes?.Contains(tokens) != true || GetContextSize() == tokens)
        {
            return this;
        }

        var capabilities = Capabilities is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(Capabilities, StringComparer.Ordinal);
        capabilities["inputTokenLimit"] = tokens;
        capabilities["maxInputTokens"] = tokens;
        // The whole context of the model holds at least that input.
        if (AgentTokenBudgetResolver.Resolve(this, AgentCompactionSettings.Default).TotalContextEnvelope is not { } envelope || envelope < tokens)
        {
            capabilities["contextWindow"] = tokens;
        }

        return this with { Capabilities = capabilities };
    }
}
