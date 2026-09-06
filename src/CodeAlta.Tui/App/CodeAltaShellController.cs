using CodeAlta.Tui.Threading;
using System.Collections.Concurrent;
using System.Threading;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Views;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

internal sealed class CodeAltaShellController : ISessionRuntimeEventProjector, IAsyncDisposable
{
    private const int UiFrameRuntimeEventDrainBatchSize = 64;

    private readonly ICodeAltaShell _shell;
    private readonly IKnownProjectImporter _knownProjectImporter;
    private readonly IProjectCatalogStore _projectCatalog;
    private readonly IRecoverableSessionSource _recoverableSessionSource;
    private readonly SessionLoadCoordinator _sessionLoadCoordinator;
    private readonly ISessionDeleter _sessionDeleter;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentQueue<SessionRuntimeEvent> _pendingRuntimeEvents = new();
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
        IReadOnlyList<ModelProviderDescriptor>? providerDescriptors = null)
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
        global::CodeAlta.Tui.CodeAltaTaskMonitor.Observe(_initializationTask, "Shell initialization");
    }

    public async Task ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus(SR.T("Refreshing project and session catalog..."), showSpinner: true),
                    cancellationToken)
                .ConfigureAwait(false);

            await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _sessionLoadCoordinator.ApplyRecoverableSessionsProgressivelyAsync(projects, cancellationToken).ConfigureAwait(false);

            await UiDispatcher.InvokeAsync(
                    () =>
                    {
                        _shell.SetReadyStatusForCurrentSelection();
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await UiDispatcher.InvokeAsync(
                    () => _shell.SetStatus(SR.T("Failed to refresh catalog: {0}", ex.Message), tone: StatusTone.Error))
                .ConfigureAwait(false);
        }
    }

    public Task ApplyRuntimeEventAsync(SessionRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.HandleRuntimeEvent(runtimeEvent), cancellationToken);
    }

    public void QueueRuntimeEvent(SessionRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        _pendingRuntimeEvents.Enqueue(runtimeEvent);
        ScheduleRuntimeEventDrain();
    }

    public int DrainPendingRuntimeEventsForUiFrame()
        => DrainPendingRuntimeEventsForUiFrame(UiFrameRuntimeEventDrainBatchSize);

    public int DrainPendingRuntimeEventsForUiFrame(int maxEvents)
    {
        var drainedEvents = DrainPendingRuntimeEvents(maxEvents);
        Interlocked.Exchange(ref _runtimeEventDrainScheduled, 0);
        if (!_pendingRuntimeEvents.IsEmpty)
        {
            ScheduleRuntimeEventDrain();
        }

        return drainedEvents;
    }

    public int DrainPendingRuntimeEvents(int maxEvents = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        var drainedEvents = 0;
        SessionRuntimeEvent? pendingEvent = null;
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
        return UiDispatcher.InvokeAsync(_shell.SelectGlobalScope, cancellationToken);
    }

    public Task SelectProjectScopeAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(() => _shell.SelectProjectScope(projectId), cancellationToken);
    }

    public Task OpenSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return UiDispatcher.InvokeAsync(
            () =>
            {
                _shell.OpenSession(sessionId);
                _shell.FocusPromptEditor();
            },
            cancellationToken);
    }

    public Task<ProjectDescriptor> OpenFolderAsync(string folderPath, CancellationToken cancellationToken)
        => OpenFolderAsync(folderPath, includeHidden: false, cancellationToken);

    public async Task<ProjectDescriptor> OpenFolderAsync(string folderPath, bool includeHidden, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        await UiDispatcher.InvokeAsync(
                () => _shell.SetStatus(SR.T("Opening '{0}'...", folderPath), showSpinner: true),
                cancellationToken)
            .ConfigureAwait(false);

        var project = await ResolveOpenProjectAsync(folderPath, includeHidden, cancellationToken).ConfigureAwait(false);

        await UiDispatcher.InvokeAsync(
                () =>
                {
                    _shell.UpsertProject(project);
                    _shell.SelectProjectScope(project.Id);
                    _shell.SetReadyStatusForCurrentSelection();
                    _shell.FocusPromptEditor();
                },
                cancellationToken)
            .ConfigureAwait(false);

        return project;
    }

    public async Task<IReadOnlyList<SessionViewDescriptor>> LoadProjectSessionsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var sessions = await CollectRecoverableSessionsAsync(
                _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return sessions
            .Where(session =>
                string.Equals(session.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static session => session.LastActiveAt)
            .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
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

    public async Task<DeleteSessionResult> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessions = await CollectRecoverableSessionsAsync(
                _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return await DeleteSessionAsync(sessionId, sessions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteSessionResult> DeleteSessionAsync(
        string sessionId,
        IReadOnlyList<SessionViewDescriptor> knownSessions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(knownSessions);

        var session = knownSessions.FirstOrDefault(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        return await DeleteSessionAsync(session, knownSessions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteSessionResult> DeleteSessionAsync(
        SessionViewDescriptor session,
        IReadOnlyList<SessionViewDescriptor> knownSessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(knownSessions);

        var sessionsToDelete = CollectSessionSubtree(session, knownSessions);
        var deletedFromSessionStore = false;
        foreach (var candidate in sessionsToDelete)
        {
            deletedFromSessionStore |= await _sessionDeleter.DeleteSessionAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var deletedSessionIds = sessionsToDelete.Select(static candidate => candidate.SessionId).ToArray();
        return new DeleteSessionResult(
            deletedSessionIds,
            deletedFromSessionStore);
    }

    public async Task<DeleteProjectResult> DeleteProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var project = await _projectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{projectId}' was not found.");
        var sessions = await LoadProjectSessionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return await DeleteProjectAsync(project, sessions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteProjectResult> DeleteProjectAsync(
        ProjectDescriptor project,
        IReadOnlyList<SessionViewDescriptor> sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sessions);

        foreach (var session in sessions)
        {
            await _sessionDeleter.DeleteSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }

        await _projectCatalog.DeleteAsync(project, cancellationToken).ConfigureAwait(false);
        var deletedSessionIds = sessions.Select(static session => session.SessionId).ToArray();
        return new DeleteProjectResult(project.Id, deletedSessionIds);
    }

    public async ValueTask DisposeAsync()
    {
        var initializationTask = _initializationTask;
        var initializationCts = _initializationCts;

        // Contain errors from our cancellation calls before joining the retained task.
        // That task can finish without joining all independently started work.
        // Cancelable dispatcher waits can finish before queued actions execute.
        // Provider refresh and startup-history work remain outside this join.
        // Release after the retained task is terminal, not after complete shutdown.
        // Preserve serialized Start/first disposal without a new concurrency policy.
        await DisposeInitializationAsync(
            initializationTask,
            _disposeCts.Cancel,
            () => initializationCts?.Cancel(),
            () => initializationCts?.Dispose(),
            _disposeCts.Dispose)
            .ConfigureAwait(false);
    }

    private IUiDispatcher UiDispatcher
        => _uiDispatcher ?? throw new InvalidOperationException("The UI dispatcher must be attached before shell operations begin.");

    private static IReadOnlyList<SessionViewDescriptor> CollectSessionSubtree(
        SessionViewDescriptor root,
        IReadOnlyList<SessionViewDescriptor> sessions)
    {
        var byParentId = sessions
            .Where(static session => !string.IsNullOrWhiteSpace(session.ParentSessionId))
            .GroupBy(static session => session.ParentSessionId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SessionViewDescriptor>();
        Collect(root);
        result.Reverse();
        return result;

        void Collect(SessionViewDescriptor session)
        {
            if (!visited.Add(session.SessionId))
            {
                return;
            }

            result.Add(session);
            if (!byParentId.TryGetValue(session.SessionId, out var children))
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

        // Runtime events are drained by CodeAltaApp.Tick, after terminal input has been handled for
        // the frame. The posted no-op only wakes the terminal loop; draining directly from the posted
        // action would run before input and can make pointer clicks feel dropped during busy sessions.
        uiDispatcher.Post(static () => { });
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
            // These startup calls are background I/O and must not assume UI-session affinity.
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
            await _shell.InitializeModelProvidersAsync(cancellationToken).ConfigureAwait(false);
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
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _sessionLoadCoordinator.ApplyRecoverableSessionsProgressivelyAsync(projects, cancellationToken).ConfigureAwait(false);
            if (await _knownProjectImporter.ImportAsync(cancellationToken).ConfigureAwait(false))
            {
                var refreshedProjects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
                await _sessionLoadCoordinator.ApplyRecoverableSessionsProgressivelyAsync(refreshedProjects, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AgentSessionCacheLockedException)
        {
            throw;
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
                _shell.TrySchedulePendingStartupSessionRestore(CancellationToken.None);
            },
            cancellationToken);
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
        SessionRuntimeEvent first,
        SessionRuntimeEvent second,
        out SessionRuntimeEvent? merged)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first is SessionAgentEvent { Event: AgentContentDeltaEvent firstDelta } firstAgent &&
            second is SessionAgentEvent { Event: AgentContentDeltaEvent secondDelta } secondAgent &&
            string.Equals(firstAgent.SessionId, secondAgent.SessionId, StringComparison.Ordinal) &&
            firstDelta.Kind == secondDelta.Kind &&
            string.Equals(firstDelta.ContentId, secondDelta.ContentId, StringComparison.Ordinal) &&
            string.Equals(firstDelta.ParentActivityId, secondDelta.ParentActivityId, StringComparison.Ordinal) &&
            string.Equals(firstDelta.SessionId, secondDelta.SessionId, StringComparison.Ordinal) &&
            firstDelta.ProviderId == secondDelta.ProviderId &&
            firstDelta.RunId == secondDelta.RunId)
        {
            merged = new SessionAgentEvent(
                firstAgent.SessionId,
                new AgentContentDeltaEvent(
                    firstDelta.ProviderId,
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

    /// <summary>
    /// Attempts initialization cancellation, the retained original-task join and source release in order.
    /// </summary>
    /// <remarks>
    /// Validate all four mandatory callbacks synchronously, then start the local core inline.
    /// The caller snapshots its retained task and linked source before callbacks and retains its fields.
    /// Support never-started initialization and serialized Start/first disposal without changing admission,
    /// scheduling, initialization tracks, token policies, fallback or monitoring. Independently attempt
    /// own cancellation, linked cancellation, the actual original join, linked release and own release.
    /// Release follows the retained task's terminal completion even after cancellation errors, not complete
    /// startup shutdown. Only the join suppresses OCE unconditionally, including faulted OCE without
    /// task/token/request classification. Callback/release OCE remains a failure; aggregates stay intact.
    /// Rethrow a lone retained failure through EDI and multiple direct references in execution order,
    /// without flattening or deduplication. A pending join avoids captured frontend context; completed
    /// awaits may stay inline, with no promised thread switch. App's plain await retains later draft context.
    /// No cache, retry, timeout, replacement task or repeated/concurrent disposal guarantee is added.
    /// This joins only the retained initializer: interaction dispatch failure/cancellation can skip its
    /// local startup-track join, and direct InitializeAsync calls are unretained. The None-token fallback
    /// can hang or replace errors. Existing provider/session wrappers swallow/log errors except cache-lock;
    /// their actual escaping-error policy, including fallible logging/finally work, remains unchanged.
    /// Provider refresh has an independent probe-timeout token and cancelable caller waits; state posts
    /// and dispatcher waits do not join queued action execution. A final provider reader stop can fail
    /// in its preceding Cancel before joining; first-stop failure alone does not prove this because finally
    /// retries the stop. Restoration/history work remains separately or discardedly retained.
    /// These exception channels do not demonstrate an application-defined throwing registration, ordinary
    /// CTS release failure or updater-style released-source token-read race under serialized Start/disposal.
    /// External Cancel callers own their errors/traversals; cancellation request/completion does not join
    /// independent callers. Queue/frame/no-op wakes are unchanged: this join does not drain actions/events
    /// or stop providers, publishers, runtime or plugins. Default monitoring can FailFast; inert tests
    /// characterize cleanup only if reached, not process survival. Waiting despite cancel errors can prevent
    /// releases, drafts and later owners indefinitely instead of escaping earlier. Hidden acquisitions and
    /// publication, earlier noncompletion, Program/admission, reader/provider/history/editor/drafts/reminder/
    /// metadata and M2-M7 remain open, not qualified by this bounded disposal extraction.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory callback is null.</exception>
    /// <exception cref="Exception">A lone retained original failure is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">A cancellation/release callback supplies the sole retained OCE.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in order, or an original aggregate is retained.</exception>
    internal static Task DisposeInitializationAsync(
        Task? initializationTask,
        Action cancelDisposal,
        Action cancelInitialization,
        Action disposeInitializationCancellation,
        Action disposeDisposalCancellation)
    {
        ArgumentNullException.ThrowIfNull(cancelDisposal);
        ArgumentNullException.ThrowIfNull(cancelInitialization);
        ArgumentNullException.ThrowIfNull(disposeInitializationCancellation);
        ArgumentNullException.ThrowIfNull(disposeDisposalCancellation);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                cancelDisposal();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                cancelInitialization();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (initializationTask is not null)
            {
                try
                {
                    await initializationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            try
            {
                disposeInitializationCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                disposeDisposalCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }
}
