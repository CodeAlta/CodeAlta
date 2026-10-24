namespace CodeAlta.Agent;

/// <summary>
/// Describes model-provider readiness.
/// </summary>
public enum ModelProviderAvailability
{
    /// <summary>
    /// The provider state has not been loaded yet.
    /// </summary>
    Unknown,

    /// <summary>
    /// The provider is being probed or initialized.
    /// </summary>
    Probing,

    /// <summary>
    /// The provider is ready for use.
    /// </summary>
    Ready,

    /// <summary>
    /// The provider is configured but disabled.
    /// </summary>
    Disabled,

    /// <summary>
    /// The provider configuration or current host does not support this provider.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The provider failed to initialize or probe.
    /// </summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of a model provider's current state.
/// </summary>
public sealed record ModelProviderStateSnapshot
{
    /// <summary>
    /// Gets the provider descriptor.
    /// </summary>
    public required ModelProviderDescriptor Descriptor { get; init; }

    /// <summary>
    /// Gets the provider identifier.
    /// </summary>
    public ModelProviderId ProviderId => Descriptor.ProviderId;

    /// <summary>
    /// Gets the current provider availability.
    /// </summary>
    public ModelProviderAvailability Availability { get; init; } = ModelProviderAvailability.Unknown;

    /// <summary>
    /// Gets the user-facing status message.
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Gets the model catalog returned by the provider probe.
    /// </summary>
    public IReadOnlyList<AgentModelInfo> Models { get; init; } = [];

    /// <summary>
    /// Gets the selected or suggested model identifier.
    /// </summary>
    public string? SelectedModelId { get; init; }

    /// <summary>
    /// Gets the selected or suggested reasoning effort.
    /// </summary>
    public AgentReasoningEffort? SelectedReasoningEffort { get; init; }

    /// <summary>
    /// Gets an optional provider-specific error category suitable for diagnostics.
    /// </summary>
    public string? ErrorCategory { get; init; }

    /// <summary>
    /// Gets what the provider can and cannot do, as its last probe said (<see cref="ModelProviderProbeResult.Capabilities"/>).
    /// </summary>
    public IReadOnlyList<ModelProviderCapability> Capabilities { get; init; } = [];

    /// <summary>
    /// Gets the time when this state was observed.
    /// </summary>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of probing a model provider runtime.
/// </summary>
public sealed record ModelProviderProbeResult
{
    /// <summary>
    /// Gets the provider identifier.
    /// </summary>
    public required ModelProviderId ProviderId { get; init; }

    /// <summary>
    /// Gets the probed availability.
    /// </summary>
    public ModelProviderAvailability Availability { get; init; } = ModelProviderAvailability.Ready;

    /// <summary>
    /// Gets the models discovered during probing.
    /// </summary>
    public IReadOnlyList<AgentModelInfo> Models { get; init; } = [];

    /// <summary>
    /// Gets the selected or suggested model identifier.
    /// </summary>
    public string? SelectedModelId { get; init; }

    /// <summary>
    /// Gets the selected or suggested reasoning effort.
    /// </summary>
    public AgentReasoningEffort? SelectedReasoningEffort { get; init; }

    /// <summary>
    /// Gets an optional user-facing status or error message.
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Gets an optional provider-specific error category suitable for diagnostics.
    /// </summary>
    public string? ErrorCategory { get; init; }

    /// <summary>
    /// Gets what the provider can and cannot do that a user of CodeAlta would otherwise find out by trying, when it
    /// depends on what runs behind it (the agent of an ACP provider); empty when there is nothing to say.
    /// </summary>
    public IReadOnlyList<ModelProviderCapability> Capabilities { get; init; } = [];
}

/// <summary>
/// One thing a provider can or cannot do, shown with it.
/// </summary>
/// <param name="Id">A stable identifier, such as <c>codealta-tools</c>.</param>
/// <param name="Label">The short text shown, such as "CodeAlta tools".</param>
/// <param name="Supported">Whether the provider can do it.</param>
/// <param name="Detail">A sentence that says more, shown on demand; <see langword="null" /> for none.</param>
public sealed record ModelProviderCapability(string Id, string Label, bool Supported, string? Detail = null);
