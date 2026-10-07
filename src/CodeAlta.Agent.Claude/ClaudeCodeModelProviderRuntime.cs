using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Model provider that runs CodeAlta sessions through the Claude Code CLI the user installed and signed in to.
/// </summary>
public sealed class ClaudeCodeModelProviderRuntime : IAgentModelProviderRuntime
{
    /// <summary>The provider type of the configuration (<c>type = "claude-code"</c>).</summary>
    public const string ProviderType = "claude-code";

    /// <summary>The protocol family recorded in the journals of the sessions of this provider.</summary>
    public const string ProtocolFamily = "claude-code";

    private readonly ClaudeCodeModelProviderRuntimeOptions _options;
    private readonly ClaudeCodeModelCatalog _modelCatalog;
    private readonly ClaudeCodeTurnExecutor _turnExecutor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The provider key of <paramref name="options"/> is empty.</exception>
    public ClaudeCodeModelProviderRuntime(ClaudeCodeModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);

        _options = options;
        Descriptor = CreateDescriptor(options);
        RuntimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = Descriptor.ProviderId.Value,
            DisplayName = Descriptor.DisplayName,
            TransportKind = AgentTransportKind.ClaudeCodeCli,
            IsDefault = options.IsDefault,
            Profile = new AgentProviderProfile
            {
                SupportsReasoningEffort = true,
                SupportsToolResultImages = true,
            },
            // The CLI keeps the context of a session and compacts it itself.
            Compaction = AgentCompactionSettings.Default with { Enabled = false },
        };
        _modelCatalog = new ClaudeCodeModelCatalog(options);
        _turnExecutor = new ClaudeCodeTurnExecutor(options);
    }

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _modelCatalog;

    /// <summary>
    /// Creates the descriptor of a provider with these options, without starting anything.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <returns>The provider descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static ModelProviderDescriptor CreateDescriptor(ClaudeCodeModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providerKey = options.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(options.DisplayName) ? providerKey : options.DisplayName.Trim();
        return new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, ProviderType)
        {
            IsDefault = options.IsDefault,
            IsEnabled = options.IsEnabled,
            DefaultModelId = string.IsNullOrWhiteSpace(options.SingleModelId) ? options.DefaultModelId : options.SingleModelId,
            SortModels = options.SortModels,
            DefaultReasoningEffort = options.DefaultReasoningEffort,
        };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks the CLI for its models and whether it is signed in. No model is called.
    /// </summary>
    /// <inheritdoc />
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Descriptor.IsEnabled)
        {
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Disabled };
        }

        ClaudeCodeInspection inspection;
        try
        {
            inspection = await _modelCatalog.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Failed,
                StatusMessage = ex.Message,
                // A missing executable is installed; one that does not run or answer is looked at in a terminal.
                ErrorCategory = ex is ClaudeCodeNotFoundException ? "claude-code-not-found" : "claude-code-unavailable",
            };
        }

        if (!inspection.IsSignedIn)
        {
            return new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Failed,
                Models = inspection.Models,
                StatusMessage = "Claude Code is not signed in. Run `claude` in a terminal and sign in with /login, then refresh the providers. CodeAlta does not handle the credentials of Claude Code.",
                ErrorCategory = "claude-code-signed-out",
            };
        }

        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Ready,
            Models = inspection.Models,
            SelectedModelId = Descriptor.DefaultModelId,
            SelectedReasoningEffort = Descriptor.DefaultReasoningEffort,
            StatusMessage = inspection.AccountSummary is { Length: > 0 } account
                ? $"Claude Code at {inspection.ExecutablePath} ({account})."
                : $"Claude Code at {inspection.ExecutablePath}.",
        };
    }

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _turnExecutor;

    /// <inheritdoc />
    public AgentRuntimeProviderRegistration CreateProviderRegistration()
        => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = _turnExecutor,
            ModelCatalog = _modelCatalog,
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _turnExecutor.DisposeAsync();
}
