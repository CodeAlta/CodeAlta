using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// CodeAlta-owned session runtime for provider-backed raw-API sessions.
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Runtime");
    private readonly AgentRuntimeOptions _options;
    private readonly object _storeLock = new();
    private readonly AgentRuntimePathLayout _layout;
    private readonly IReadOnlyDictionary<string, AgentRuntimeProviderRegistration> _providersByKey;
    private readonly Dictionary<string, IReadOnlyList<AgentModelInfo>> _modelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentSessionJournalFile _journalFile = new();
    private IAgentSessionJournalStore? _store;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRuntime"/> class.
    /// </summary>
    /// <param name="providerId">The provider identifier persisted in legacy backend-id fields.</param>
    /// <param name="displayName">The user-facing runtime name.</param>
    /// <param name="options">Runtime options.</param>
    public AgentRuntime(
        ModelProviderId providerId,
        string displayName,
        AgentRuntimeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers is not { Count: > 0 })
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        ProviderId = new ModelProviderId(providerId.Value);
        DisplayName = displayName.Trim();
        _options = options;
        var stateRootPath = string.IsNullOrWhiteSpace(options.StateRootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".alta")
            : options.StateRootPath;
        _layout = new AgentRuntimePathLayout(stateRootPath);
        _providersByKey = options.Providers.ToDictionary(
            static provider => provider.Provider.ProviderKey,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the provider identifier as stored in legacy persisted backend-id fields.
    /// </summary>
    public ModelProviderId ProviderId { get; }

    /// <summary>
    /// Gets the user-facing runtime name.
    /// </summary>
    public string DisplayName { get; }

    private IAgentSessionJournalStore Store
    {
        get
        {
            if (_store is not null)
            {
                return _store;
            }

            lock (_storeLock)
            {
                return _store ??= new FileSystemAgentSessionStore(
                    _layout,
                    _journalFile);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<AgentModelInfo>();
        foreach (var provider in _options.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInfo(
                $"Listing models runtimeProviderId={ProviderId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} protocol={provider.Provider.ProtocolFamily} baseUri={FormatUri(provider.Provider.BaseUri)}");

            IReadOnlyList<AgentModelInfo> models;
            try
            {
                var modelCatalog = ResolveModelCatalog(provider);
                if (modelCatalog is null)
                {
                    continue;
                }

                models = await modelCatalog.ListModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarn(
                    ex,
                    $"Failed to list models runtimeProviderId={ProviderId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} protocol={provider.Provider.ProtocolFamily} baseUri={FormatUri(provider.Provider.BaseUri)}");
                throw;
            }

            LogInfo(
                $"Listed models runtimeProviderId={ProviderId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} count={models.Count}");
            _modelCache[provider.Provider.ProviderKey] = models;
            results.AddRange(models);
        }

        var mergedModels = results
            .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LogInfo($"Provider model catalog ready runtimeProviderId={ProviderId.Value} providers={_options.Providers.Count} models={mergedModels.Length}");
        return mergedModels;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        return await Store.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var registration = ResolveProvider(options.ProviderKey);
        var now = DateTimeOffset.UtcNow;
        var sessionId = string.IsNullOrWhiteSpace(options.SessionId)
            ? Guid.CreateVersion7().ToString()
            : options.SessionId.Trim();
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ProviderId,
            ProtocolFamily = registration.Provider.ProtocolFamily,
            ProviderKey = registration.Provider.ProviderKey,
            ModelId = options.Model,
            WorkingDirectory = options.WorkingDirectory,
            Title = NormalizeOptionalText(options.Title),
            Summary = null,
            ParentSessionId = NormalizeOptionalText(options.ParentSessionId),
            CreatedBySessionId = NormalizeOptionalText(options.CreatedBySessionId ?? options.ParentSessionId),
            CreatedByRunId = options.CreatedByRunId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var state = new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = registration.Provider.ProtocolFamily,
            ProviderKey = registration.Provider.ProviderKey,
            UpdatedAt = now,
        };

        await Store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
        await Store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
        return new AgentSession(
            ProviderId,
            registration.Provider,
            summary,
            state,
            [],
            Store,
            registration.TurnExecutor,
            options,
            allowProviderContinuation: true,
            cachedModels: GetCachedModels(registration));
    }

    /// <inheritdoc />
    public async Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var summary = await Store.GetSessionSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            throw new KeyNotFoundException($"The session '{sessionId}' was not found for runtime '{ProviderId.Value}'.");
        }

        var provider = ResolveResumeProvider(options, summary);
        var state = await Store.GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? new AgentSessionState
            {
                SessionId = sessionId,
                ProtocolFamily = summary.ProtocolFamily,
                ProviderKey = summary.ProviderKey,
                UpdatedAt = summary.UpdatedAt,
            };
        var history = await Store.ReadEventsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!MatchesProvider(summary, provider.Provider) || !MatchesProvider(state, provider.Provider))
        {
            var now = DateTimeOffset.UtcNow;
            summary = TransferSummaryToProvider(summary, provider.Provider, options, now);
            state = TransferStateToProvider(state, provider.Provider, now);
            await Store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
            await Store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        (summary, state) = await RepairRecoveredUsageAsync(summary, state, history, provider, options, cancellationToken).ConfigureAwait(false);

        return new AgentSession(
            ProviderId,
            provider.Provider,
            OverrideSummary(summary, options),
            state,
            history,
            Store,
            provider.TurnExecutor,
            options,
            allowProviderContinuation: false,
            cachedModels: GetCachedModels(provider));

    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _options.Providers)
        {
            switch (registration.TurnExecutor)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private AgentRuntimeProviderRegistration ResolveProvider(string? providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            if (_providersByKey.TryGetValue(providerKey.Trim(), out var resolved))
            {
                return resolved;
            }

            throw new KeyNotFoundException($"The provider '{providerKey}' is not registered for provider runtime '{ProviderId.Value}'.");
        }

        var preferred = _options.Providers.FirstOrDefault(static provider => provider.Provider.IsDefault)
            ?? (_options.Providers.Count == 1 ? _options.Providers[0] : null);
        if (preferred is null)
        {
            throw new InvalidOperationException(
                $"Provider runtime '{ProviderId.Value}' requires an explicit provider key because no single default provider is configured.");
        }

        return preferred;
    }

    private AgentRuntimeProviderRegistration ResolveResumeProvider(
        AgentSessionResumeOptions options,
        AgentSessionSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(options.ProviderKey))
        {
            return ResolveProvider(options.ProviderKey);
        }

        if (!string.IsNullOrWhiteSpace(summary.ProviderKey) &&
            _providersByKey.TryGetValue(summary.ProviderKey, out var lastProvider))
        {
            return lastProvider;
        }

        return ResolveProvider(null);
    }

    private AgentSessionSummary TransferSummaryToProvider(
        AgentSessionSummary summary,
        ModelProviderRuntimeDescriptor provider,
        AgentSessionResumeOptions options,
        DateTimeOffset updatedAt)
        => summary with
        {
            ProviderId = ProviderId,
            ProtocolFamily = provider.ProtocolFamily,
            ProviderKey = provider.ProviderKey,
            ModelId = NormalizeOptionalText(options.Model),
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? summary.WorkingDirectory : options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(options.Title) ? summary.Title : options.Title.Trim(),
            UpdatedAt = updatedAt,
        };

    private static AgentSessionState TransferStateToProvider(
        AgentSessionState state,
        ModelProviderRuntimeDescriptor provider,
        DateTimeOffset updatedAt)
        => state with
        {
            ProtocolFamily = provider.ProtocolFamily,
            ProviderKey = provider.ProviderKey,
            ProviderSessionId = null,
            ProviderState = null,
            UpdatedAt = updatedAt,
        };

    private static AgentSessionSummary OverrideSummary(
        AgentSessionSummary summary,
        AgentSessionResumeOptions options)
    {
        return summary with
        {
            ModelId = string.IsNullOrWhiteSpace(options.Model) ? summary.ModelId : options.Model,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? summary.WorkingDirectory : options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(options.Title) ? summary.Title : options.Title.Trim(),
        };
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<(AgentSessionSummary Summary, AgentSessionState State)> RepairRecoveredUsageAsync(
        AgentSessionSummary summary,
        AgentSessionState state,
        IReadOnlyList<AgentEvent> history,
        AgentRuntimeProviderRegistration provider,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken)
    {
        var originalSummaryUsage = summary.Usage;
        var originalStateUsage = state.Usage;
        var recoveredUsage = AgentUsageFactory.RecoverUsageFromHistory(history);
        if (ShouldPreferRecoveredUsage(recoveredUsage, summary.Usage))
        {
            summary = summary with { Usage = recoveredUsage };
        }

        if (ShouldPreferRecoveredUsage(recoveredUsage, state.Usage))
        {
            state = state with { Usage = recoveredUsage };
        }

        var effectiveModelId = options.Model ??
                               summary.ModelId ??
                               state.Usage?.LastOperation?.Model ??
                               summary.Usage?.LastOperation?.Model;
        if (!string.IsNullOrWhiteSpace(effectiveModelId))
        {
            try
            {
                var models = GetCachedModels(provider);
                if (models.Count > 0)
                {
                    var modelInfo = AgentModelIdentity.FindBestMatch(models, effectiveModelId);
                    if (modelInfo is not null)
                    {
                        summary = summary with { Usage = AgentUsageFactory.AttachModelInfo(summary.Usage, modelInfo) };
                        state = state with { Usage = AgentUsageFactory.AttachModelInfo(state.Usage, modelInfo) };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        var summaryChanged = !Equals(summary.Usage, originalSummaryUsage);
        var stateChanged = !Equals(state.Usage, originalStateUsage);
        if (summaryChanged)
        {
            await Store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
        }

        if (stateChanged)
        {
            await Store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return (summary, state);
    }

    private static bool ShouldPreferRecoveredUsage(AgentSessionUsage? recovered, AgentSessionUsage? current)
    {
        if (recovered is null)
        {
            return false;
        }

        if (current is null)
        {
            return true;
        }

        if (recovered.UpdatedAt != default && current.UpdatedAt != default)
        {
            if (recovered.UpdatedAt > current.UpdatedAt)
            {
                return true;
            }

            if (recovered.UpdatedAt < current.UpdatedAt)
            {
                return false;
            }
        }

        return (current.Window is null && recovered.Window is not null) ||
               (current.LastOperation is null && recovered.LastOperation is not null) ||
               (current.RateLimits is null && recovered.RateLimits is not null) ||
               (current.CurrentTokens is null && recovered.CurrentTokens is not null) ||
               (current.TokenLimit is null && recovered.TokenLimit is not null);
    }

    private IReadOnlyList<AgentModelInfo> GetCachedModels(AgentRuntimeProviderRegistration provider)
    {
        var providerKey = provider.Provider.ProviderKey;
        var state = _options.ModelProviderInitializationService?.CurrentStates.FirstOrDefault(
            state => string.Equals(state.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase));
        if (state?.Models is { Count: > 0 } models)
        {
            _modelCache[providerKey] = models;
            return models;
        }

        return _modelCache.TryGetValue(providerKey, out var cached) ? cached : [];
    }

    private static IModelProviderModelCatalog? ResolveModelCatalog(AgentRuntimeProviderRegistration provider)
        => provider.ModelCatalog ?? provider.TurnExecutor as IModelProviderModelCatalog;

    private static bool MatchesProvider(AgentSessionSummary summary, ModelProviderRuntimeDescriptor provider)
        => string.Equals(summary.ProtocolFamily, provider.ProtocolFamily, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(summary.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesProvider(AgentSessionState state, ModelProviderRuntimeDescriptor provider)
        => string.Equals(state.ProtocolFamily, provider.ProtocolFamily, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(state.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase);

    private static string FormatUri(Uri? uri)
        => uri?.ToString() ?? "<default>";

    private static void LogInfo(string message)
    {
        Logger.Info(message);
    }

    private static void LogWarn(Exception exception, string message)
    {
        Logger.Warn(exception, message);
    }
}
