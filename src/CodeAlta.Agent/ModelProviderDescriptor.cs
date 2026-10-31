namespace CodeAlta.Agent;

/// <summary>
/// Describes a configured model provider that can be surfaced in provider selectors.
/// </summary>
public sealed record ModelProviderDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ModelProviderDescriptor" /> class.
    /// </summary>
    /// <param name="providerId">The configured provider identifier.</param>
    /// <param name="displayName">The user-facing provider name. Whitespace falls back to <paramref name="providerId" />.</param>
    public ModelProviderDescriptor(ModelProviderId providerId, string? displayName)
        : this(providerId, displayName, providerId.Value)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelProviderDescriptor" /> class.
    /// </summary>
    /// <param name="providerId">The configured provider identifier.</param>
    /// <param name="displayName">The user-facing provider name. Whitespace falls back to <paramref name="providerId" />.</param>
    /// <param name="providerType">The provider adapter type, such as <c>openai-chat</c> or <c>anthropic</c>. Whitespace falls back to <paramref name="providerId" />.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerType" /> is empty after fallback.</exception>
    public ModelProviderDescriptor(ModelProviderId providerId, string? displayName, string? providerType)
    {
        ProviderId = providerId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? providerId.Value : displayName.Trim();
        ProviderType = string.IsNullOrWhiteSpace(providerType) ? providerId.Value : providerType.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderType);
    }

    /// <summary>
    /// Gets the configured provider identifier.
    /// </summary>
    public ModelProviderId ProviderId { get; init; }

    /// <summary>
    /// Gets the user-facing provider name.
    /// </summary>
    public string DisplayName { get; init; }

    /// <summary>
    /// Gets the provider adapter type.
    /// </summary>
    public string ProviderType { get; init; }

    /// <summary>
    /// Gets the optional provider endpoint base URI.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Gets a value indicating whether this provider is the default option within its source definition.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets a value indicating whether this provider definition is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets the configured default model identifier, when any.
    /// </summary>
    public string? DefaultModelId { get; init; }

    /// <summary>Gets the order the discovered models are listed in; the order of the provider by default.</summary>
    public ModelOrder ModelOrder { get; init; }

    /// <summary>
    /// Gets a value indicating whether discovered models are sorted for presentation; true sets <see cref="ModelOrder"/>
    /// to <see cref="Agent.ModelOrder.Name"/>.
    /// </summary>
    [Obsolete("Use ModelOrder.", error: false)]
    public bool SortModels
    {
        get => ModelOrder != ModelOrder.Provider;
        init => ModelOrder = value ? ModelOrder.Name : ModelOrder.Provider;
    }

    /// <summary>
    /// Gets the configured default reasoning effort, when any.
    /// </summary>
    public AgentReasoningEffort? DefaultReasoningEffort { get; init; }

    /// <summary>
    /// Gets the configured size of context of the sessions of the provider, in input tokens, when any: used by a model
    /// that has that size (<see cref="AgentModelInfo.ContextSizes"/>) in a session that chose none.
    /// </summary>
    public long? DefaultContextSize { get; init; }

    /// <summary>
    /// Gets the permission modes a session of this provider can be given, empty when the provider has none.
    /// </summary>
    public IReadOnlyList<string> PermissionModes { get; init; } = [];

    /// <summary>
    /// Gets the configured permission mode, when any: the mode of a session that is given none.
    /// </summary>
    public string? DefaultPermissionMode { get; init; }

    /// <summary>
    /// Gets whether a session of this provider can be followed and driven from elsewhere
    /// (<see cref="IAgentRemoteControlProvider"/>, Claude Code's Remote Control).
    /// </summary>
    public bool SupportsRemoteControl { get; init; }

    /// <summary>
    /// Gets whether a session of this provider can run in the cloud (Claude Code's cloud sessions): started there,
    /// sent messages there, and brought back to this computer.
    /// </summary>
    public bool SupportsCloudSessions { get; init; }

    /// <summary>
    /// Gets whether the provider keeps the conversation of a session where other programs continue it (the transcript of
    /// Claude Code, which VS Code and the CLI in a terminal write too): the turns made there are then added to the
    /// session. The provider of a session without it is never asked for them.
    /// </summary>
    public bool SharesConversations { get; init; }
}
