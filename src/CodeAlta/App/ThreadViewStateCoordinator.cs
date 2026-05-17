using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ThreadViewStateCoordinator
{
    private readonly WorkThreadCatalog _threadCatalog;

    public ThreadViewStateCoordinator(WorkThreadCatalog threadCatalog)
    {
        ArgumentNullException.ThrowIfNull(threadCatalog);
        _threadCatalog = threadCatalog;
    }

    public Task<WorkThreadViewState> LoadViewStateAsync(CancellationToken cancellationToken)
        => _threadCatalog.LoadViewStateAsync(cancellationToken);

    public async Task PersistViewStateAsync(WorkThreadViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        try
        {
            await _threadCatalog.SaveViewStateAsync(viewState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAlta.Views.CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAlta.Views.CodeAltaApp.UiLogger.Error(ex, "Failed to persist thread view state.");
            }
        }
    }

    public IReadOnlyList<WorkThreadDescriptor> ApplyThreadLocalState(
        IReadOnlyList<WorkThreadDescriptor> threads,
        WorkThreadViewState viewState,
        bool readJournal = false)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(viewState);
        if (readJournal)
        {
            throw new InvalidOperationException("Use ApplyThreadLocalStateAsync when journal state must be read.");
        }

        foreach (var thread in threads)
        {
            if (!viewState.ThreadStates.TryGetValue(thread.ThreadId, out var localState))
            {
                continue;
            }

            ApplyLocalState(thread, localState);
        }

        return threads;
    }

    public async Task<IReadOnlyList<WorkThreadDescriptor>> ApplyThreadLocalStateAsync(
        IReadOnlyList<WorkThreadDescriptor> threads,
        WorkThreadViewState viewState,
        bool readJournal = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(viewState);

        foreach (var thread in threads)
        {
            WorkThreadLocalState? localState = null;
            if (readJournal)
            {
                localState = await _threadCatalog.JournalStore
                    .ReadLatestStateAsync(thread.ThreadId, thread.CreatedAt, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (localState is null && !viewState.ThreadStates.TryGetValue(thread.ThreadId, out localState))
            {
                continue;
            }

            ApplyLocalState(thread, localState);
        }

        return threads;
    }

    public async Task PersistThreadLocalStateAsync(WorkThreadViewState viewState, WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(thread);

        var localState = CreateThreadLocalState(thread);
        viewState.ThreadStates[thread.ThreadId] = localState;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistThreadLocalStateSnapshotAsync(thread, localState).ConfigureAwait(false);
    }

    public WorkThreadLocalState RememberThreadLocalState(WorkThreadViewState viewState, WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(thread);

        var localState = CreateThreadLocalState(thread);
        viewState.ThreadStates[thread.ThreadId] = localState;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        return localState;
    }

    public async Task PersistThreadLocalStateSnapshotAsync(
        WorkThreadDescriptor thread,
        WorkThreadLocalState localState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(localState);

        try
        {
            await _threadCatalog.JournalStore.AppendStateAsync(thread, localState, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to persist local state for thread {thread.ThreadId}.");
        }
    }

    private static void LogFailure(Exception ex, string message)
    {
        if (LogManager.IsInitialized && CodeAlta.Views.CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
        {
            CodeAlta.Views.CodeAltaApp.UiLogger.Error(ex, message);
        }
    }

    public NavigatorSettings GetNavigatorSettingsSnapshot(WorkThreadViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        return new NavigatorSettings
        {
            SortMode = viewState.Navigator.SortMode,
            RecentThreadsPerProject = viewState.Navigator.RecentThreadsPerProject,
            ThemeSchemeName = viewState.Navigator.ThemeSchemeName,
        };
    }

    public async Task SaveNavigatorSettingsAsync(WorkThreadViewState viewState, NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        viewState.Navigator = new NavigatorSettings
        {
            SortMode = settings.SortMode,
            RecentThreadsPerProject = settings.RecentThreadsPerProject,
            ThemeSchemeName = NormalizeThemeSchemeName(settings.ThemeSchemeName),
        };
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync(viewState).ConfigureAwait(false);
    }

    private static WorkThreadLocalState CreateThreadLocalState(WorkThreadDescriptor thread)
        => new()
        {
            ProviderKey = thread.ResolvedProviderKey,
            ModelId = thread.ModelId,
            ReasoningEffort = thread.ReasoningEffort,
            Archived = thread.Status == WorkThreadStatus.Archived,
            MessageCount = thread.MessageCount,
            ParentThreadId = thread.ParentThreadId,
            CreatedBy = thread.CreatedBy,
        };

    private static string? NormalizeThemeSchemeName(string? themeSchemeName)
        => string.IsNullOrWhiteSpace(themeSchemeName) ? null : themeSchemeName.Trim();

    private static void ApplyLocalState(WorkThreadDescriptor thread, WorkThreadLocalState localState)
    {
        if (localState.Archived)
        {
            thread.Status = WorkThreadStatus.Archived;
        }

        if (!string.IsNullOrWhiteSpace(localState.ProviderKey))
        {
            var providerKey = localState.ProviderKey.Trim();
            thread.ProviderKey = providerKey;
            thread.BackendId = providerKey;
        }

        if (!string.IsNullOrWhiteSpace(localState.ModelId))
        {
            thread.ModelId = localState.ModelId;
        }

        if (localState.ReasoningEffort is { } reasoningEffort)
        {
            thread.ReasoningEffort = reasoningEffort;
        }

        if (localState.MessageCount is { } messageCount)
        {
            thread.MessageCount = messageCount;
        }

        if (!string.IsNullOrWhiteSpace(localState.ParentThreadId))
        {
            thread.ParentThreadId = localState.ParentThreadId;
        }

        if (localState.CreatedBy is not null)
        {
            thread.CreatedBy = localState.CreatedBy;
        }
    }
}
