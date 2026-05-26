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

internal sealed class CodeAltaShellController : IThreadRuntimeEventProjector, IAsyncDisposable
{
    private readonly ICodeAltaShell _shell;
    private readonly IKnownProjectImporter _knownProjectImporter;
    private readonly IProjectCatalogStore _projectCatalog;
    private readonly IRecoverableSessionSource _recoverableSessionSource;
    private readonly SessionLoadCoordinator _sessionLoadCoordinator;
    private readonly ISessionDeleter _sessionDeleter;
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
        IRecoverableSessionSource recoverableSessionSource,
        ISessionDeleter sessionDeleter,
        IReadOnlyList<ModelProviderDescriptor>? backendDescriptors = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(knownProjectImporter);
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(recoverableSessionSource);
        ArgumentNullException.ThrowIfNull(sessionDeleter);

        _shell = shell;
        _knownProjectImporter = knownProjectImporter;
        _projectCatalog = projectCatalog;
        _recoverableSessionSource = recoverableSessionSource;
        _sessionLoadCoordinator = new SessionLoadCoordinator(recoverableSessionSource, () => UiDispatcher, shell);
        _sessionDeleter = sessionDeleter;
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
        // Shell initialization intentionally runs off the UI flow. Any continuation that touches
        // shell/view state must marshal back through UiDispatcher.
        _initializationTask = Task.Run(
            () => RunInitializationAsync(_initializationCts.Token),
            CancellationToken.None);
        global::CodeAlta.CodeAltaTaskMonitor.Observe(_initializationTask, "Shell initialization");
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
            await _sessionLoadCoordinator.ApplyRecoverableSessionsProgressivelyAsync(projects, cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () =>
                    {
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
        return UiDispatcher.InvokeAsync(
            () =>
            {
                _shell.OpenThread(threadId);
                _shell.FocusPromptEditor();
            });
    }

    public Task<ProjectDescriptor> OpenFolderAsync(string folderPath, CancellationToken cancellationToken)
        => OpenFolderAsync(folderPath, includeHidden: false, cancellationToken);

    public async Task<ProjectDescriptor> OpenFolderAsync(string folderPath, bool includeHidden, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        await UiDispatcher.InvokeAsync(
                () => _shell.SetStatus($"Opening '{folderPath}'...", showSpinner: true))
            .ConfigureAwait(false);

        var project = await ResolveOpenProjectAsync(folderPath, includeHidden, cancellationToken).ConfigureAwait(false);

        await UiDispatcher.InvokeAsync(
                () =>
                {
                    _shell.UpsertProject(project);
                    _shell.SelectProjectScope(project.Id);
                    _shell.SetReadyStatusForCurrentSelection();
                    _shell.FocusPromptEditor();
                })
            .ConfigureAwait(false);

        return project;
    }

    public async Task<IReadOnlyList<SessionViewDescriptor>> LoadProjectThreadsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var threads = await CollectRecoverableSessionsAsync(
                _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
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

    public async Task<DeleteSessionResult> DeleteSessionAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var threads = await CollectRecoverableSessionsAsync(
                _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return await DeleteSessionAsync(threadId, threads, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteSessionResult> DeleteSessionAsync(
        string threadId,
        IReadOnlyList<SessionViewDescriptor> knownThreads,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(knownThreads);

        var thread = knownThreads.FirstOrDefault(candidate => string.Equals(candidate.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");
        return await DeleteSessionAsync(thread, knownThreads, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteSessionResult> DeleteSessionAsync(
        SessionViewDescriptor thread,
        IReadOnlyList<SessionViewDescriptor> knownThreads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(knownThreads);

        var threadsToDelete = CollectThreadSubtree(thread, knownThreads);
        var deletedByBackend = false;
        foreach (var candidate in threadsToDelete)
        {
            deletedByBackend |= await _sessionDeleter.DeleteSessionAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var deletedThreadIds = threadsToDelete.Select(static candidate => candidate.ThreadId).ToArray();
        return new DeleteSessionResult(
            deletedThreadIds,
            deletedByBackend);
    }

    public async Task<DeleteProjectResult> DeleteProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var project = await _projectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        var threads = await LoadProjectThreadsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await DeleteProjectAsync(project, threads, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteProjectResult> DeleteProjectAsync(
        ProjectDescriptor project,
        IReadOnlyList<SessionViewDescriptor> threads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(threads);

        foreach (var thread in threads)
        {
            await _sessionDeleter.DeleteSessionAsync(thread, cancellationToken).ConfigureAwait(false);
        }

        project.Archived = true;
        await _projectCatalog.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        var deletedThreadIds = threads.Select(static thread => thread.ThreadId).ToArray();
        return new DeleteProjectResult(project.Id, deletedThreadIds);
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

    private static IReadOnlyList<SessionViewDescriptor> CollectThreadSubtree(
        SessionViewDescriptor root,
        IReadOnlyList<SessionViewDescriptor> threads)
    {
        var byParentId = threads
            .Where(static thread => !string.IsNullOrWhiteSpace(thread.ParentThreadId))
            .GroupBy(static thread => thread.ParentThreadId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SessionViewDescriptor>();
        Collect(root);
        result.Reverse();
        return result;

        void Collect(SessionViewDescriptor thread)
        {
            if (!visited.Add(thread.ThreadId))
            {
                return;
            }

            result.Add(thread);
            if (!byParentId.TryGetValue(thread.ThreadId, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                Collect(child);
            }
        }
    }

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

    private async Task<ProjectDescriptor> ResolveOpenProjectAsync(
        string folderPathOrProjectReference,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPathOrProjectReference);

        if (OpenProjectRequestResolver.LooksLikePath(folderPathOrProjectReference))
        {
            var normalizedPath = OpenProjectRequestResolver.NormalizePath(folderPathOrProjectReference);
            if (!Directory.Exists(normalizedPath))
            {
                throw new InvalidOperationException($"The folder '{normalizedPath}' does not exist.");
            }

            return await _projectCatalog.UpsertFromPathAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        }

        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var candidateProjects = includeHidden
            ? projects
            : projects.Where(static project => !project.Archived).ToArray();
        var project = OpenProjectRequestResolver.ResolveProjectReference(candidateProjects, folderPathOrProjectReference);
        if (!project.Archived)
        {
            return project;
        }

        project.Archived = false;
        await _projectCatalog.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    private async Task RunInitializationAsync(CancellationToken cancellationToken)
    {
        var initializedForInteraction = false;
        try
        {
            // These startup calls are background I/O and must not assume UI-thread affinity.
            var startupProviderLoadTask = Task.Run(
                () => InitializeStartupTracksAsync(cancellationToken),
                CancellationToken.None);
            await MarkInitializedForInteractionAsync(cancellationToken).ConfigureAwait(false);
            initializedForInteraction = true;
            await startupProviderLoadTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposeCts.IsCancellationRequested)
            {
                if (!initializedForInteraction)
                {
                    await MarkInitializedForInteractionAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task InitializeStartupTracksAsync(CancellationToken cancellationToken)
    {
        var providerInitializationTask = InitializeStartupProvidersAsync(cancellationToken);
        var sessionLoadTask = LoadStartupSessionsAsync(cancellationToken);

        await Task.WhenAll(providerInitializationTask, sessionLoadTask).ConfigureAwait(false);
    }

    private async Task InitializeStartupProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _shell.InitializeChatBackendsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, "Failed to initialize model providers.");
        }
    }

    private async Task LoadStartupSessionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _sessionLoadCoordinator.ApplyRecoverableSessionsProgressivelyAsync(projects, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, "Failed to refresh startup session catalog state.");
        }
    }

    private Task MarkInitializedForInteractionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(
            () =>
            {
                _shell.PublishStartupCatalogProjectionReady();
                _shell.SetReadyStatusForCurrentSelection();
                _shell.SetInitialized(true);
                _shell.TrySchedulePendingStartupThreadRestore(CancellationToken.None);
            });
    }

    private static async Task<IReadOnlyList<SessionViewDescriptor>> CollectRecoverableSessionsAsync(
        IAsyncEnumerable<SessionViewDescriptor> sessions,
        CancellationToken cancellationToken)
    {
        var results = new List<SessionViewDescriptor>();
        await foreach (var session in sessions.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(session);
        }

        return results;
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
