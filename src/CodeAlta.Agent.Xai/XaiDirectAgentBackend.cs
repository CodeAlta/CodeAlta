using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Compaction;

namespace CodeAlta.Agent.Xai;

/// <summary>
/// Local-runtime backend for direct xAI (Grok) HTTP API access.
/// </summary>
public sealed class XaiDirectAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    /// <summary>
    /// The canonical provider type and protocol family for direct xAI access.
    /// </summary>
    public const string ProtocolFamily = "xai";

    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="XaiDirectAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    /// <exception cref="ArgumentException">Thrown when no provider is configured.</exception>
    public XaiDirectAgentBackend(XaiAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        foreach (var provider in options.Providers)
        {
            provider.StateRootPath ??= options.StateRootPath;
        }

        var backendId = options.BackendIdOverride ?? new AgentBackendId(options.Providers[0].ProviderKey.Trim());
        var displayName = string.IsNullOrWhiteSpace(options.DisplayNameOverride)
            ? "xAI Grok"
            : options.DisplayNameOverride.Trim();

        _inner = new LocalAgentBackend(
            backendId,
            displayName,
            new LocalAgentBackendOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = ProtocolFamily,
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            BackendId = backendId,
                            TransportKind = LocalAgentTransportKind.OpenAIResponses,
                            BaseUri = provider.BaseUri ?? XaiDefaults.DefaultApiBaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? CreateDefaultProfile(),
                            Compaction = provider.Compaction ?? LocalAgentCompactionSettings.Default,
                        },
                        TurnExecutor = new XaiDirectTurnExecutor(provider),
                    }),
                ],
            });
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => _inner.BackendId;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => _inner.ListModelsAsync(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
        => _inner.ListSessionsAsync(filter, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.DeleteSessionAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(options, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
        => _inner.ResumeSessionAsync(sessionId, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static LocalAgentProviderProfile CreateDefaultProfile()
        => new()
        {
            SupportsDeveloperRole = true,
            SupportsReasoningEffort = true,
            SupportsStore = false,
            StreamsUsage = true,
            MaxTokensFieldName = "max_output_tokens",
            ReasoningFieldNames = ["reasoning"],
        };
}
