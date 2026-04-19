namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Options used to create a shared local-runtime backend.
/// </summary>
public sealed class LocalAgentBackendOptions
{
    /// <summary>
    /// Gets or initializes the local runtime storage root path.
    /// Defaults to <c>~/.alta</c>, with session journals stored under <c>~/.alta/sessions</c>.
    /// </summary>
    public string? StateRootPath { get; init; }

    /// <summary>
    /// Gets or initializes the provider registrations available through this backend.
    /// </summary>
    public required IReadOnlyList<LocalAgentBackendProviderRegistration> Providers { get; init; }
}

/// <summary>
/// Associates a configured provider descriptor with its turn executor.
/// </summary>
public sealed class LocalAgentBackendProviderRegistration
{
    /// <summary>
    /// Gets or initializes the configured provider descriptor.
    /// </summary>
    public required LocalAgentProviderDescriptor Provider { get; init; }

    /// <summary>
    /// Gets or initializes the turn executor used for sessions targeting the provider.
    /// </summary>
    public required ILocalAgentTurnExecutor TurnExecutor { get; init; }
}
