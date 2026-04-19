using CodeAlta.Agent.ModelCatalog;
using XenoAtom.Logging;

namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Shared <see cref="IAgentBackend"/> implementation for provider-backed local raw-API runtimes.
/// </summary>
public sealed class LocalAgentBackend : IAgentBackend
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
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        foreach (var provider in _options.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _store.UpsertProviderAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
        }
        _started = true;
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
        var sessionId = Guid.CreateVersion7().ToString();
        var summary = new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = BackendId,
            ProtocolFamily = registration.Provider.ProtocolFamily,
            ProviderKey = registration.Provider.ProviderKey,
            ModelId = options.Model,
            WorkingDirectory = options.WorkingDirectory,
            Title = null,
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
            options);
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
            (summary, state) = await RepairRecoveredUsageAsync(summary, state, provider, options, cancellationToken).ConfigureAwait(false);

            return new LocalAgentSession(
                BackendId,
                provider.Provider,
                OverrideSummary(summary, options),
                state,
                history,
                _store,
                provider.TurnExecutor,
                options);
        }

        throw new KeyNotFoundException($"The session '{sessionId}' was not found for backend '{BackendId.Value}'.");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        };
    }

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
        LocalAgentBackendProviderRegistration provider,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken)
    {
        var effectiveModelId = options.Model ??
                               summary.ModelId ??
                               state.Usage?.LastOperation?.Model ??
                               summary.Usage?.LastOperation?.Model;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            return (summary, state);
        }

        try
        {
            var models = await provider.TurnExecutor.ListModelsAsync(provider.Provider, cancellationToken).ConfigureAwait(false);
            var modelInfo = AgentModelIdentity.FindBestMatch(models, effectiveModelId);
            if (modelInfo is null)
            {
                return (summary, state);
            }

            var repairedSummaryUsage = LocalAgentUsageFactory.AttachModelInfo(summary.Usage, modelInfo);
            var repairedStateUsage = LocalAgentUsageFactory.AttachModelInfo(state.Usage, modelInfo);
            var summaryChanged = !Equals(repairedSummaryUsage, summary.Usage);
            var stateChanged = !Equals(repairedStateUsage, state.Usage);
            if (!summaryChanged && !stateChanged)
            {
                return (summary, state);
            }

            if (summaryChanged)
            {
                summary = summary with { Usage = repairedSummaryUsage };
                await _store.UpsertSessionAsync(summary, cancellationToken).ConfigureAwait(false);
            }

            if (stateChanged)
            {
                state = state with { Usage = repairedStateUsage };
                await _store.UpsertStateAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return (summary, state);
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
