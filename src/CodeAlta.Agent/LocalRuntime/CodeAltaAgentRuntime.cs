using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// CodeAlta-owned session runtime for provider-backed raw-API sessions.
/// </summary>
public sealed class CodeAltaAgentRuntime : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.LocalRuntime");
    private readonly CodeAltaAgentRuntimeOptions _options;
    private readonly object _storeLock = new();
    private readonly LocalAgentRuntimePathLayout _layout;
    private readonly IReadOnlyDictionary<string, CodeAltaAgentRuntimeProviderRegistration> _providersByKey;
    private readonly Dictionary<string, IReadOnlyList<AgentModelInfo>> _modelCache = new(StringComparer.OrdinalIgnoreCase);
    private LocalAgentSessionJournalFile? _journalFile;
    private ILocalAgentSessionStore? _store;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaAgentRuntime"/> class.
    /// </summary>
    /// <param name="backendId">The transitional provider/backend identifier used by existing provider wrappers.</param>
    /// <param name="displayName">The user-facing runtime name.</param>
    /// <param name="options">Runtime options.</param>
    public CodeAltaAgentRuntime(
        AgentBackendId backendId,
        string displayName,
        CodeAltaAgentRuntimeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers is not { Count: > 0 })
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        BackendId = backendId;
        DisplayName = displayName.Trim();
        _options = options;
        var stateRootPath = string.IsNullOrWhiteSpace(options.StateRootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".alta")
            : options.StateRootPath;
        _layout = new LocalAgentRuntimePathLayout(stateRootPath);
        _journalFile = options.SessionJournalFile;
        _providersByKey = options.Providers.ToDictionary(
            static provider => provider.Provider.ProviderKey,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AgentBackendId BackendId { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    private ILocalAgentSessionStore Store
    {
        get
        {
            if (_store is not null)
            {
                return _store;
            }

            lock (_storeLock)
            {
                return _store ??= new FileSystemLocalAgentSessionStore(
                    _layout,
                    _journalFile ??= new LocalAgentSessionJournalFile());
            }
        }
    }

    internal void UseSessionJournalFile(LocalAgentSessionJournalFile journalFile)
    {
        ArgumentNullException.ThrowIfNull(journalFile);

        lock (_storeLock)
        {
            if (_store is null)
            {
                _journalFile = journalFile;
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
                $"Listing models backend={BackendId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} protocol={provider.Provider.ProtocolFamily} baseUri={FormatUri(provider.Provider.BaseUri)}");

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
                    $"Failed to list models backend={BackendId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} protocol={provider.Provider.ProtocolFamily} baseUri={FormatUri(provider.Provider.BaseUri)}");
                throw;
            }

            LogInfo(
                $"Listed models backend={BackendId.Value} provider={provider.Provider.ProviderKey} displayName={provider.Provider.DisplayName} count={models.Count}");
            _modelCache[provider.Provider.ProviderKey] = models;
            results.AddRange(models);
        }

        var mergedModels = results
            .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LogInfo($"Backend model catalog ready backend={BackendId.Value} providers={_options.Providers.Count} models={mergedModels.Length}");
        return mergedModels;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var summary in Store.ListSessionSummariesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesFilter(summary, filter))
            {
                continue;
            }

            var state = await Store.GetStateAsync(summary.SessionId, cancellationToken).ConfigureAwait(false);
            _providersByKey.TryGetValue(summary.ProviderKey, out var provider);
            yield return ToMetadata(summary, state, provider?.Provider);
        }
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
        var sessionId = string.IsNullOrWhiteSpace(options.ThreadId)
            ? Guid.CreateVersion7().ToString()
            : options.ThreadId.Trim();
        var summary = new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = BackendId,
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
        var state = new LocalAgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = registration.Provider.ProtocolFamily,
            ProviderKey = registration.Provider.ProviderKey,
            UpdatedAt = now,
        };

        await Store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
        await Store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
        return new LocalAgentSession(
            BackendId,
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
            throw new KeyNotFoundException($"The session '{sessionId}' was not found for runtime '{BackendId.Value}'.");
        }

        var provider = ResolveResumeProvider(options, summary);
        var state = await Store.GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? new LocalAgentSessionState
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

        return new LocalAgentSession(
            BackendId,
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

    private CodeAltaAgentRuntimeProviderRegistration ResolveProvider(string? providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            if (_providersByKey.TryGetValue(providerKey.Trim(), out var resolved))
            {
                return resolved;
            }

            throw new KeyNotFoundException($"The provider '{providerKey}' is not registered for backend '{BackendId.Value}'.");
        }

        var preferred = _options.Providers.FirstOrDefault(static provider => provider.Provider.IsDefault)
            ?? (_options.Providers.Count == 1 ? _options.Providers[0] : null);
        if (preferred is null)
        {
            throw new InvalidOperationException(
                $"Backend '{BackendId.Value}' requires an explicit provider key because no single default provider is configured.");
        }

        return preferred;
    }

    private CodeAltaAgentRuntimeProviderRegistration ResolveResumeProvider(
        AgentSessionResumeOptions options,
        LocalAgentSessionSummary summary)
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

    private LocalAgentSessionSummary TransferSummaryToProvider(
        LocalAgentSessionSummary summary,
        ModelProviderRuntimeDescriptor provider,
        AgentSessionResumeOptions options,
        DateTimeOffset updatedAt)
        => summary with
        {
            BackendId = BackendId,
            ProtocolFamily = provider.ProtocolFamily,
            ProviderKey = provider.ProviderKey,
            ModelId = NormalizeOptionalText(options.Model),
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? summary.WorkingDirectory : options.WorkingDirectory,
            Title = string.IsNullOrWhiteSpace(options.Title) ? summary.Title : options.Title.Trim(),
            UpdatedAt = updatedAt,
        };

    private static LocalAgentSessionState TransferStateToProvider(
        LocalAgentSessionState state,
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

    private static LocalAgentSessionSummary OverrideSummary(
        LocalAgentSessionSummary summary,
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

    private static bool MatchesFilter(LocalAgentSessionSummary summary, AgentSessionListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(filter.Cwd) &&
            !string.Equals(summary.WorkingDirectory, filter.Cwd, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<(LocalAgentSessionSummary Summary, LocalAgentSessionState State)> RepairRecoveredUsageAsync(
        LocalAgentSessionSummary summary,
        LocalAgentSessionState state,
        IReadOnlyList<AgentEvent> history,
        CodeAltaAgentRuntimeProviderRegistration provider,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken)
    {
        var originalSummaryUsage = summary.Usage;
        var originalStateUsage = state.Usage;
        var recoveredUsage = LocalAgentUsageFactory.RecoverUsageFromHistory(history);
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
                        summary = summary with { Usage = LocalAgentUsageFactory.AttachModelInfo(summary.Usage, modelInfo) };
                        state = state with { Usage = LocalAgentUsageFactory.AttachModelInfo(state.Usage, modelInfo) };
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

    private IReadOnlyList<AgentModelInfo> GetCachedModels(CodeAltaAgentRuntimeProviderRegistration provider)
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

    private static IModelProviderModelCatalog? ResolveModelCatalog(CodeAltaAgentRuntimeProviderRegistration provider)
        => provider.ModelCatalog ?? provider.TurnExecutor as IModelProviderModelCatalog;

    private static bool MatchesProvider(LocalAgentSessionSummary summary, ModelProviderRuntimeDescriptor provider)
        => string.Equals(summary.ProtocolFamily, provider.ProtocolFamily, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(summary.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesProvider(LocalAgentSessionState state, ModelProviderRuntimeDescriptor provider)
        => string.Equals(state.ProtocolFamily, provider.ProtocolFamily, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(state.ProviderKey, provider.ProviderKey, StringComparison.OrdinalIgnoreCase);

    private static AgentSessionMetadata ToMetadata(
        LocalAgentSessionSummary summary,
        LocalAgentSessionState? state,
        ModelProviderRuntimeDescriptor? provider)
    {
        return new AgentSessionMetadata(
            summary.SessionId,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Summary,
            summary.WorkingDirectory is null ? null : new AgentSessionContext(summary.WorkingDirectory),
            summary.WorkingDirectory,
            new RawApiSessionMetadataDetails(
                provider?.DisplayName,
                provider?.BaseUri?.ToString(),
                state?.ProviderSessionId,
                summary.Title),
            summary.ProtocolFamily,
            summary.ProviderKey,
            summary.ModelId,
            summary.ParentSessionId,
            summary.CreatedBySessionId,
            summary.CreatedByRunId);
    }

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
