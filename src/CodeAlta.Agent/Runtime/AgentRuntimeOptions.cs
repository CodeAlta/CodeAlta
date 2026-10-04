namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Options used to create the CodeAlta-owned agent session runtime.
/// </summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>
    /// Gets or initializes the agent runtime storage root path; session journals are stored under its
    /// <c>sessions</c> directory. Required: the runtime has no default location, so the host decides
    /// where sessions are written (normally the state root of its CodeAlta profile).
    /// </summary>
    public string? StateRootPath { get; init; }

    /// <summary>
    /// Gets or initializes the provider model cache used for optional model/usage enrichment.
    /// </summary>
    public IModelProviderInitializationService? ModelProviderInitializationService { get; init; }

    /// <summary>
    /// Gets or initializes the optional session projection cache used for fast local session listings.
    /// </summary>
    public IAgentSessionProjectionCache? SessionProjectionCache { get; init; }

    /// <summary>
    /// Gets or initializes the provider registrations available through this runtime.
    /// </summary>
    public required IReadOnlyList<AgentRuntimeProviderRegistration> Providers { get; init; }
}

/// <summary>
/// Associates a configured provider descriptor with its turn executor.
/// </summary>
public sealed class AgentRuntimeProviderRegistration
{
    /// <summary>
    /// Gets or initializes the configured provider descriptor.
    /// </summary>
    public required ModelProviderRuntimeDescriptor Provider { get; init; }

    /// <summary>
    /// Gets or initializes the turn executor used for sessions targeting the provider.
    /// </summary>
    public required IModelProviderTurnExecutor TurnExecutor { get; init; }

    /// <summary>
    /// Gets or initializes the model catalog for provider probing. Session execution does not require this service.
    /// </summary>
    public IModelProviderModelCatalog? ModelCatalog { get; init; }
}
