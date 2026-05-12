namespace CodeAlta.Agent;

/// <summary>
/// Describes backend registration metadata used by orchestration before a backend instance is created.
/// </summary>
public sealed class AgentBackendRegistrationOptions
{
    /// <summary>
    /// Gets the default backend registration options.
    /// </summary>
    public static AgentBackendRegistrationOptions Default { get; } = new();

    /// <summary>
    /// Gets backend registration options for backends that use the shared CodeAlta session metadata store.
    /// </summary>
    public static AgentBackendRegistrationOptions SharedSessionMetadataStore { get; } = new()
    {
        UsesSharedSessionMetadataStore = true,
    };

    /// <summary>
    /// Gets or initializes a value indicating whether this backend stores session metadata in the shared CodeAlta session store.
    /// </summary>
    public bool UsesSharedSessionMetadataStore { get; init; }
}
