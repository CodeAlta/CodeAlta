using CodeAlta.Threading;
using System.Collections.Concurrent;
using System.Threading;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Views;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaShellController : IAsyncDisposable
{
    private readonly ICodeAltaShell _shell;
    private readonly IKnownProjectImporter _knownProjectImporter;
    private readonly IProjectCatalogStore _projectCatalog;
    private readonly IRecoverableThreadSource _recoverableThreadSource;
    private readonly IWorkThreadDeleter _threadDeleter;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentQueue<WorkThreadRuntimeEvent> _pendingRuntimeEvents = new();
    private IUiDispatcher? _uiDispatcher;
    private int _runtimeEventDrainScheduled;
    private CancellationTokenSource? _initializationCts;
    private Task? _initializationTask;

    public CodeAltaShellController(
        ICodeAltaShell shell,
        IKnownProjectImporter knownProjectImporter,
        IProjectCatalogStore projectCatalog,
        IRecoverableThreadSource recoverableThreadSource,
        IWorkThreadDeleter threadDeleter)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(recoverableThreadSource);
        ArgumentNullException.ThrowIfNull(threadDeleter);

        _shell = shell;
        _knownProjectImporter = knownProjectImporter;
        _projectCatalog = projectCatalog;
        _recoverableThreadSource = recoverableThreadSource;
        _threadDeleter = threadDeleter;
    }

    public void AttachUiDispatcher(IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        _uiDispatcher = uiDispatcher;
        if (!_pendingRuntimeEvents.IsEmpty)
        {
            ScheduleRuntimeEventDrain();
        }
    }

    public void StartInitialization(CancellationToken cancellationToken)
    {
        if (_initializationTask is not null)
        {
            return;
        }

        _initializationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        _initializationTask = Task.Run(
            () => RunInitializationAsync(_initializationCts.Token),
            CancellationToken.None);
    }

    public async Task ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus("Refreshing project and thread catalog...", showSpinner: true))
                .ConfigureAwait(false);

            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () =>
                    {
                        _shell.ApplyRecoveredCatalogState(projects, threads);
                        _shell.SetReadyStatusForCurrentSelection();
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus($"Failed to refresh catalog: {ex.Message}", tone: StatusTone.Error))
                .ConfigureAwait(false);
        }
    }

    public Task ApplyRuntimeEventAsync(WorkThreadRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.HandleRuntimeEvent(runtimeEvent));
    }

    public void QueueRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        _pendingRuntimeEvents.Enqueue(runtimeEvent);
        ScheduleRuntimeEventDrain();
    }

    public int DrainPendingRuntimeEvents(int maxEvents = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        var drainedEvents = 0;
        WorkThreadRuntimeEvent? pendingEvent = null;
        while (drainedEvents < maxEvents && _pendingRuntimeEvents.TryDequeue(out var runtimeEvent))
        {
            drainedEvents++;
            if (pendingEvent is null)
            {
                pendingEvent = runtimeEvent;
                continue;
            }

            if (TryMergeRuntimeEvents(pendingEvent, runtimeEvent, out var mergedEvent))
            {
                pendingEvent = mergedEvent;
                continue;
            }

            _shell.HandleRuntimeEvent(pendingEvent);
            pendingEvent = runtimeEvent;
        }

        if (pendingEvent is not null)
        {
            _shell.HandleRuntimeEvent(pendingEvent);
        }

        return drainedEvents;
    }

    public Task SelectGlobalScopeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(_shell.SelectGlobalScope);
    }

    public Task SelectProjectScopeAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.SelectProjectScope(projectId));
    }

    public Task OpenThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.OpenThread(threadId));
    }

    public async Task<IReadOnlyList<WorkThreadDescriptor>> LoadProjectThreadsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);
        return threads
            .Where(thread =>
                string.Equals(thread.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ThenBy(static thread => thread.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<ProjectDescriptor?> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _projectCatalog.GetByIdAsync(projectId, cancellationToken);
    }

    public async Task SaveProjectAsync(ProjectDescriptor project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        await _projectCatalog.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        await ReloadCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);
        var thread = threads.FirstOrDefault(candidate => string.Equals(candidate.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");

        var deletedByBackend = await _threadDeleter.DeleteThreadAsync(thread, cancellationToken).ConfigureAwait(false);
        await ReloadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return deletedByBackend;
    }

    public async Task<DeleteProjectResult> DeleteProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var project = await _projectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        var threads = await LoadProjectThreadsAsync(projectId, cancellationToken).ConfigureAwait(false);

        foreach (var thread in threads)
        {
            await _threadDeleter.DeleteThreadAsync(thread, cancellationToken).ConfigureAwait(false);
        }

        project.Archived = true;
        await _projectCatalog.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        await ReloadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return new DeleteProjectResult(project.Id, threads.Select(static thread => thread.ThreadId).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _initializationCts?.Cancel();

        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _initializationCts?.Dispose();
        _disposeCts.Dispose();
    }

    private IUiDispatcher UiDispatcher
        => _uiDispatcher ?? throw new InvalidOperationException("The UI dispatcher must be attached before shell operations begin.");

    private void ScheduleRuntimeEventDrain()
    {
        var uiDispatcher = _uiDispatcher;
        if (uiDispatcher is null || Interlocked.Exchange(ref _runtimeEventDrainScheduled, 1) != 0)
        {
            return;
        }

        UiDispatch.Post(uiDispatcher, DrainPendingRuntimeEventsOnUi);
    }

    private void DrainPendingRuntimeEventsOnUi()
    {
        _ = DrainPendingRuntimeEvents();
        Interlocked.Exchange(ref _runtimeEventDrainScheduled, 0);
        if (!_pendingRuntimeEvents.IsEmpty)
        {
            ScheduleRuntimeEventDrain();
        }
    }

    internal Task InitializeAsync(CancellationToken cancellationToken)
        => RunInitializationAsync(cancellationToken);

    private async Task RunInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _shell.InitializeChatBackendsAsync(cancellationToken).ConfigureAwait(false);
            await RefreshCatalogFromBackendsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposeCts.IsCancellationRequested)
            {
                await UiDispatcher.InvokeAsync(
                        () =>
                        {
                            _shell.RefreshCatalogAndThreadWorkspace();
                            _shell.SetReadyStatusForCurrentSelection();
                            _shell.SetInitialized(true);
                            _shell.TrySchedulePendingStartupThreadRestore(CancellationToken.None);
                        })
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RefreshCatalogFromBackendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var threads = await _recoverableThreadSource.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () => _shell.ApplyRecoveredCatalogState(projects, threads))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, "Failed to refresh backend startup state.");
            }
        }
    }

    internal static bool TryMergeRuntimeEvents(
        WorkThreadRuntimeEvent first,
        WorkThreadRuntimeEvent second,
        out WorkThreadRuntimeEvent? merged)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first is WorkThreadAgentEvent { Event: AgentContentDeltaEvent firstDelta } firstAgent &&
            second is WorkThreadAgentEvent { Event: AgentContentDeltaEvent secondDelta } secondAgent &&
            string.Equals(firstAgent.ThreadId, secondAgent.ThreadId, StringComparison.Ordinal) &&
            firstDelta.Kind == secondDelta.Kind &&
            string.Equals(firstDelta.ContentId, secondDelta.ContentId, StringComparison.Ordinal) &&
            string.Equals(firstDelta.ParentActivityId, secondDelta.ParentActivityId, StringComparison.Ordinal) &&
            string.Equals(firstDelta.SessionId, secondDelta.SessionId, StringComparison.Ordinal) &&
            firstDelta.BackendId == secondDelta.BackendId &&
            firstDelta.RunId == secondDelta.RunId)
        {
            merged = new WorkThreadAgentEvent(
                firstAgent.ThreadId,
                new AgentContentDeltaEvent(
                    firstDelta.BackendId,
                    firstDelta.SessionId,
                    secondDelta.Timestamp,
                    firstDelta.RunId,
                    firstDelta.Kind,
                    firstDelta.ContentId,
                    firstDelta.ParentActivityId,
                    string.Concat(firstDelta.Delta, secondDelta.Delta)));
            return true;
        }

        merged = null;
        return false;
    }
}
