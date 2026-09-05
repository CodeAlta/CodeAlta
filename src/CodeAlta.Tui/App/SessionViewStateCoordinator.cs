using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

internal sealed class SessionViewStateCoordinator
{
    private readonly SessionViewCatalog _sessionCatalog;
    private readonly Func<SessionViewStateSaveRequest, CancellationToken, Task<SessionViewStateSaveResult>> _save;
    private readonly object _persistenceGate = new();
    private Task _persistTail = Task.CompletedTask;
    private TextFileRevision? _acknowledgedRevision;
    private string? _pendingYaml;
    private long _submissionId;
    private bool _hasPendingChanges;
    private PersistenceResult? _lastResult;

    internal sealed record PersistenceResult(SessionViewStateSaveResult? Save, Exception? Error)
    {
        public bool IsAcknowledged => Save is { IsConflict: false };
    }

    public TextFileRevision? AcknowledgedRevision
    {
        get { lock (_persistenceGate) { return _acknowledgedRevision; } }
    }

    public string? PendingYaml
    {
        get { lock (_persistenceGate) { return _pendingYaml; } }
    }

    public bool HasPendingChanges
    {
        get { lock (_persistenceGate) { return _hasPendingChanges; } }
    }

    public PersistenceResult? LastResult
    {
        get { lock (_persistenceGate) { return _lastResult; } }
    }

    public SessionViewStateCoordinator(SessionViewCatalog sessionCatalog)
    {
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        _sessionCatalog = sessionCatalog;
        _save = sessionCatalog.SaveViewStateAsync;
    }

    internal SessionViewStateCoordinator(SessionViewCatalog sessionCatalog,
        Func<SessionViewStateSaveRequest, CancellationToken, Task<SessionViewStateSaveResult>> save)
        : this(sessionCatalog)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = save;
    }

    public Task<SessionViewViewState> LoadViewStateAsync(CancellationToken cancellationToken)
        => _sessionCatalog.LoadViewStateAsync(cancellationToken);

    public async Task<NavigatorSettings> LoadNavigatorSettingsAsync(CancellationToken cancellationToken)
    {
        var viewState = await LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        return GetNavigatorSettingsSnapshot(viewState);
    }

    // Called on the UI owner: serialize before any await, never enumerate live collections in a worker.
    public Task<PersistenceResult> PersistViewStateAsync(SessionViewViewState viewState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        lock (_persistenceGate)
        {
            var submissionId = ++_submissionId;
            _hasPendingChanges = true;
            SessionViewStateSaveRequest request;
            try
            {
                request = _sessionCatalog.CreateViewStateSaveRequest(viewState, viewState.Revision);
            }
            catch (Exception ex)
            {
                LogFailure(ex, "Failed to snapshot session view state.");
                _lastResult = new PersistenceResult(null, ex);
                return Task.FromResult(_lastResult);
            }

            _acknowledgedRevision ??= request.ExpectedRevision;
            _pendingYaml = request.Yaml;
            var task = PersistSnapshotAsync(_persistTail, request, submissionId, cancellationToken);
            _persistTail = task;
            return task;
        }
    }

    private async Task<PersistenceResult> PersistSnapshotAsync(
        Task previous, SessionViewStateSaveRequest request, long submissionId, CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);
        try
        {
            lock (_persistenceGate)
            {
                request = request with { ExpectedRevision = _acknowledgedRevision ?? request.ExpectedRevision };
            }

            var saved = await _save(request, cancellationToken).ConfigureAwait(false);
            if (!saved.IsConflict)
            {
                lock (_persistenceGate)
                {
                    _acknowledgedRevision = saved.AcknowledgedRevision;
                    if (_submissionId == submissionId)
                    {
                        _pendingYaml = null;
                        _hasPendingChanges = false;
                    }
                }
            }
            else
            {
                CodeAlta.Tui.Views.CodeAltaApp.UiLogger.Warn("Session view state changed externally; pending state was not saved.");
            }

            return RememberResult(new PersistenceResult(saved, null), submissionId);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Failed to persist session view state.");
            return RememberResult(new PersistenceResult(null, ex), submissionId);
        }
    }

    private PersistenceResult RememberResult(PersistenceResult result, long submissionId)
    {
        lock (_persistenceGate)
        {
            if (_submissionId == submissionId)
            {
                _lastResult = result;
            }
        }

        return result;
    }

    public IReadOnlyList<SessionViewDescriptor> ApplySessionLocalState(
        IReadOnlyList<SessionViewDescriptor> sessions,
        SessionViewViewState viewState,
        bool readJournal = false)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(viewState);
        if (readJournal)
        {
            throw new InvalidOperationException("Use ApplySessionLocalStateAsync when journal state must be read.");
        }

        foreach (var session in sessions)
        {
            if (!viewState.SessionStates.TryGetValue(session.SessionId, out var localState))
            {
                continue;
            }

            ApplyLocalState(session, localState);
        }

        return sessions;
    }

    public async Task<IReadOnlyList<SessionViewDescriptor>> ApplySessionLocalStateAsync(
        IReadOnlyList<SessionViewDescriptor> sessions,
        SessionViewViewState viewState,
        bool readJournal = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(viewState);

        foreach (var session in sessions)
        {
            SessionViewLocalState? localState = null;
            if (readJournal)
            {
                localState = await _sessionCatalog.JournalStore
                    .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (localState is null && !viewState.SessionStates.TryGetValue(session.SessionId, out localState))
            {
                continue;
            }

            ApplyLocalState(session, localState);
        }

        return sessions;
    }

    public async Task PersistSessionLocalStateAsync(SessionViewViewState viewState, SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(session);

        var localState = CreateSessionLocalState(session);
        viewState.SessionStates[session.SessionId] = localState;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistSessionLocalStateSnapshotAsync(session, localState).ConfigureAwait(false);
    }

    public SessionViewLocalState RememberSessionLocalState(SessionViewViewState viewState, SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(session);

        var localState = CreateSessionLocalState(session);
        viewState.SessionStates[session.SessionId] = localState;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        return localState;
    }

    public async Task PersistSessionLocalStateSnapshotAsync(
        SessionViewDescriptor session,
        SessionViewLocalState localState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localState);

        try
        {
            // View-state persistence owns UI/session metadata only; keep runtime-owned prompt
            // queue/provenance fields from the latest journal snapshot intact.
            var journalState = await CreateJournalLocalStateSnapshotAsync(session, localState, cancellationToken).ConfigureAwait(false);
            await _sessionCatalog.JournalStore.AppendStateAsync(session, journalState, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to persist local state for session {session.SessionId}.");
        }
    }

    private static void LogFailure(Exception ex, string message)
    {
        CodeAlta.Tui.Views.CodeAltaApp.UiLogger.Error(ex, message);
    }

    public NavigatorSettings GetNavigatorSettingsSnapshot(SessionViewViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        return CloneNavigatorSettings(viewState.Navigator);
    }

    public static NavigatorSettings CloneNavigatorSettings(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new NavigatorSettings
        {
            SortMode = settings.SortMode,
            RecentSessionsPerProject = settings.RecentSessionsPerProject,
            ThemeSchemeName = settings.ThemeSchemeName,
            LanguageName = settings.LanguageName,
            AutoApprove = settings.AutoApprove,
        };
    }

    public Task<PersistenceResult> SaveNavigatorSettingsAsync(SessionViewViewState viewState, NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        viewState.Navigator = new NavigatorSettings
        {
            SortMode = settings.SortMode,
            RecentSessionsPerProject = settings.RecentSessionsPerProject,
            ThemeSchemeName = NormalizeThemeSchemeName(settings.ThemeSchemeName),
            LanguageName = NormalizeLanguageName(settings.LanguageName),
            AutoApprove = settings.AutoApprove,
        };
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        return PersistViewStateAsync(viewState);
    }

    private static string? NormalizeLanguageName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return null;
        }

        var trimmed = languageName.Trim();
        return string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static SessionViewLocalState CreateSessionLocalState(SessionViewDescriptor session)
        => new()
        {
            ProviderKey = session.ResolvedProviderKey,
            ModelId = session.ModelId,
            ReasoningEffort = session.ReasoningEffort,
            AgentPromptId = string.IsNullOrWhiteSpace(session.AgentPromptId) ? null : session.AgentPromptId.Trim(),
            Archived = session.Status == SessionViewStatus.Archived,
            MessageCount = session.MessageCount,
            ParentSessionId = session.ParentSessionId,
            CreatedBy = session.CreatedBy,
        };

    private async Task<SessionViewLocalState> CreateJournalLocalStateSnapshotAsync(
        SessionViewDescriptor session,
        SessionViewLocalState localState,
        CancellationToken cancellationToken)
    {
        var snapshot = CloneSessionLocalState(localState);
        try
        {
            var latestState = await _sessionCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (latestState is not null)
            {
                snapshot.PromptProvenance = ClonePromptProvenance(latestState.PromptProvenance);
                snapshot.QueuedPrompts = CloneQueuedPrompts(latestState.QueuedPrompts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return snapshot;
    }

    private static SessionViewLocalState CloneSessionLocalState(SessionViewLocalState localState)
        => new()
        {
            ProviderKey = localState.ProviderKey,
            ModelId = localState.ModelId,
            ReasoningEffort = localState.ReasoningEffort,
            AgentPromptId = localState.AgentPromptId,
            Archived = localState.Archived,
            MessageCount = localState.MessageCount,
            ParentSessionId = localState.ParentSessionId,
            CreatedBy = localState.CreatedBy,
            PromptProvenance = ClonePromptProvenance(localState.PromptProvenance),
            QueuedPrompts = CloneQueuedPrompts(localState.QueuedPrompts),
        };

    private static List<SessionViewPromptProvenance> ClonePromptProvenance(IEnumerable<SessionViewPromptProvenance>? promptProvenance)
        => promptProvenance?.Select(static provenance => new SessionViewPromptProvenance
        {
            PromptId = provenance.PromptId,
            Kind = provenance.Kind,
            RunId = provenance.RunId,
            Queued = provenance.Queued,
            PromptPreview = provenance.PromptPreview,
            SubmittedBy = provenance.SubmittedBy,
            CreatedAt = provenance.CreatedAt,
        }).ToList() ?? [];

    private static List<SessionViewQueuedPrompt> CloneQueuedPrompts(IEnumerable<SessionViewQueuedPrompt>? queuedPrompts)
        => queuedPrompts?.Select(static prompt => new SessionViewQueuedPrompt
        {
            QueueItemId = prompt.QueueItemId,
            Kind = prompt.Kind,
            Prompt = prompt.Prompt,
            PromptPreview = prompt.PromptPreview,
            State = prompt.State,
            RunId = prompt.RunId,
            SubmittedBy = prompt.SubmittedBy,
            CreatedAt = prompt.CreatedAt,
            DrainedAt = prompt.DrainedAt,
            LastError = prompt.LastError,
        }).ToList() ?? [];

    private static string? NormalizeThemeSchemeName(string? themeSchemeName)
        => string.IsNullOrWhiteSpace(themeSchemeName) ? null : themeSchemeName.Trim();

    private static void ApplyLocalState(SessionViewDescriptor session, SessionViewLocalState localState)
    {
        if (localState.Archived)
        {
            session.Status = SessionViewStatus.Archived;
        }

        if (!string.IsNullOrWhiteSpace(localState.ProviderKey))
        {
            var providerKey = localState.ProviderKey.Trim();
            session.ProviderKey = providerKey;
            session.ProviderId = providerKey;
        }

        if (!string.IsNullOrWhiteSpace(localState.ModelId))
        {
            session.ModelId = localState.ModelId;
        }

        if (localState.ReasoningEffort is { } reasoningEffort)
        {
            session.ReasoningEffort = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(localState.AgentPromptId))
        {
            session.AgentPromptId = localState.AgentPromptId.Trim();
        }

        if (localState.MessageCount is { } messageCount)
        {
            session.MessageCount = messageCount;
        }

        if (!string.IsNullOrWhiteSpace(localState.ParentSessionId))
        {
            session.ParentSessionId = localState.ParentSessionId;
        }

        if (localState.CreatedBy is not null)
        {
            session.CreatedBy = localState.CreatedBy;
        }
    }
}
