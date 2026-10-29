using System.Text.Json.Serialization;

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
    /// <summary>Gets verified session-scoped context choices. Null means the adapter supports Default only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentModelContextSizes? ContextSizes { get; init; }

    // A session-local copy only; never advertised or persisted as a capability.
    [JsonIgnore]
    internal long? EffectiveContextWindowTokens { get; init; }

    /// <summary>
    /// Gets the choices of the model other than its reasoning effort, such as the size of its context or a fast mode,
    /// with the values they have now: set through <see cref="IModelProviderModelOptions"/>. Null or empty for a model
    /// that has none, or a provider that offers none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentModelOption>? Options { get; init; }
}
