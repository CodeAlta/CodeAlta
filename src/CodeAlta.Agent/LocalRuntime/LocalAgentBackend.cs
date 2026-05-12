using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Shared <see cref="IAgentBackend"/> implementation for provider-backed local raw-API runtimes.
/// </summary>
public sealed class LocalAgentBackend : IAgentBackend, IAgentSharedSessionMetadataBackend
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.LocalRuntime");
    private readonly LocalAgentBackendOptions _options;
    private readonly ILocalAgentSessionStore _store;
    private readonly IReadOnlyDictionary<string, LocalAgentBackendProviderRegistration> _providersByKey;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalAgentBackend"/> class.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="displayName">The user-facing backend name.</param>
    /// <param name="options">Backend options.</param>
    public LocalAgentBackend(
        AgentBackendId backendId,
        string displayName,
        LocalAgentBackendOptions options)
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
        _store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(stateRootPath));
        _providersByKey = options.Providers.ToDictionary(
            static provider => provider.Provider.ProviderKey,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AgentBackendId BackendId { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

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
                models = await provider.TurnExecutor.ListModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<AgentSessionMetadata>();
        foreach (var provider in _options.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summaries = await _store.ListSessionsAsync(
                    provider.Provider.ProtocolFamily,
                    provider.Provider.ProviderKey,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var summary in summaries)
            {
                if (!MatchesFilter(summary, filter))
                {
                    continue;
                }

                var state = await _store.GetStateAsync(
                        summary.ProtocolFamily,
                        summary.ProviderKey,
                        summary.SessionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                results.Add(ToMetadata(summary, state, provider.Provider));
            }
        }

        return results
            .OrderByDescending(static session => session.UpdatedAt)
            .ThenByDescending(static session => session.CreatedAt)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        foreach (var provider in _options.Providers)
        {
            if (await _store.DeleteSessionAsync(
                    provider.Provider.ProtocolFamily,
                    provider.Provider.ProviderKey,
                    sessionId,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
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

        await _store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
        await _store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
        return new LocalAgentSession(
            BackendId,
            registration.Provider,
            summary,
            state,
            [],
            _store,
            registration.TurnExecutor,
            options,
            allowProviderContinuation: true);
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

        foreach (var provider in _options.Providers)
        {
            var summary = await _store.GetSessionAsync(
                    provider.Provider.ProtocolFamily,
                    provider.Provider.ProviderKey,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (summary is null)
            {
                continue;
            }

            var state = await _store.GetStateAsync(
                    provider.Provider.ProtocolFamily,
                    provider.Provider.ProviderKey,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new LocalAgentSessionState
                {
                    SessionId = sessionId,
                    ProtocolFamily = summary.ProtocolFamily,
                    ProviderKey = summary.ProviderKey,
                    UpdatedAt = summary.UpdatedAt,
                };
            var history = await _store.ReadEventsAsync(
                    provider.Provider.ProtocolFamily,
                    provider.Provider.ProviderKey,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            (summary, state) = await RepairRecoveredUsageAsync(summary, state, history, provider, options, cancellationToken).ConfigureAwait(false);

            return new LocalAgentSession(
                BackendId,
                provider.Provider,
                OverrideSummary(summary, options),
                state,
                history,
                _store,
                provider.TurnExecutor,
                options,
                allowProviderContinuation: false);
        }

        throw new KeyNotFoundException($"The session '{sessionId}' was not found for backend '{BackendId.Value}'.");
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

    private LocalAgentBackendProviderRegistration ResolveProvider(string? providerKey)
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
        LocalAgentBackendProviderRegistration provider,
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
                var models = await provider.TurnExecutor.ListModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
                var modelInfo = AgentModelIdentity.FindBestMatch(models, effectiveModelId);
                if (modelInfo is not null)
                {
                    summary = summary with { Usage = LocalAgentUsageFactory.AttachModelInfo(summary.Usage, modelInfo) };
                    state = state with { Usage = LocalAgentUsageFactory.AttachModelInfo(state.Usage, modelInfo) };
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
            await _store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
        }

        if (stateChanged)
        {
            await _store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
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

    private static AgentSessionMetadata ToMetadata(
        LocalAgentSessionSummary summary,
        LocalAgentSessionState? state,
        LocalAgentProviderDescriptor provider)
    {
        return new AgentSessionMetadata(
            summary.SessionId,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Summary,
            summary.WorkingDirectory is null ? null : new AgentSessionContext(summary.WorkingDirectory),
            summary.WorkingDirectory,
            new RawApiSessionMetadataDetails(
                provider.DisplayName,
                provider.BaseUri?.ToString(),
                state?.ProviderSessionId,
                summary.Title),
            summary.ProtocolFamily,
            summary.ProviderKey,
            summary.ModelId);
    }

    private static string FormatUri(Uri? uri)
        => uri?.ToString() ?? "<default>";

    private static void LogInfo(string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Info))
        {
            Logger.Info(message);
        }
    }

    private static void LogWarn(Exception exception, string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Warn))
        {
            Logger.Warn(exception, message);
        }
    }
}
