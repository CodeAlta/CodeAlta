namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// Describes request-level customizations that apply when a model id matches a configured model request override.
/// </summary>
public sealed class AgentModelRequestOverride
{
    /// <summary>
    /// Gets or sets static HTTP headers added for matching model requests.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets provider/default headers to remove before this model's headers are applied.
    /// Required authentication headers cannot be removed.
    /// </summary>
    public IReadOnlyList<string>? RemoveHeaders { get; set; }

    /// <summary>
    /// Gets or sets OpenAI-compatible request-body fields added for matching model requests.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ExtraBody { get; set; }

    /// <summary>
    /// Gets or sets provider/default OpenAI-compatible request-body fields to remove before this model's fields are applied.
    /// </summary>
    public IReadOnlyList<string>? RemoveExtraBody { get; set; }
}
