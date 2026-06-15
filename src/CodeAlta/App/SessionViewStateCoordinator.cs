using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class SessionViewStateCoordinator
{
    private readonly SessionViewCatalog _sessionCatalog;
    private readonly SemaphoreSlim _persistViewStateGate = new(1, 1);

    public SessionViewStateCoordinator(SessionViewCatalog sessionCatalog)
    {
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        _sessionCatalog = sessionCatalog;
    }

    public Task<SessionViewViewState> LoadViewStateAsync(CancellationToken cancellationToken)
        => _sessionCatalog.LoadViewStateAsync(cancellationToken);

    public async Task<NavigatorSettings> LoadNavigatorSettingsAsync(CancellationToken cancellationToken)
    {
        var viewState = await LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        return GetNavigatorSettingsSnapshot(viewState);
    }

    public async Task PersistViewStateAsync(SessionViewViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        await _persistViewStateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _sessionCatalog.SaveViewStateAsync(viewState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CodeAlta.Views.CodeAltaApp.UiLogger.Error(ex, "Failed to persist session view state.");
        }
        finally
        {
            _persistViewStateGate.Release();
        }
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
            await _sessionCatalog.JournalStore.AppendStateAsync(session, localState, cancellationToken).ConfigureAwait(false);
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
        CodeAlta.Views.CodeAltaApp.UiLogger.Error(ex, message);
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

    public async Task SaveNavigatorSettingsAsync(SessionViewViewState viewState, NavigatorSettings settings)
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
        await PersistViewStateAsync(viewState).ConfigureAwait(false);
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
