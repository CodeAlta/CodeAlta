using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaShellControllerInitializationSourceTests
{
    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void ControllerCleanup_SourceWiring_UsesMandatoryCore()
    {
        var controller = ReadSource("CodeAlta.Tui/App/CodeAltaShellController.cs");
        RequireOnce(controller, "await DisposeInitializationAsync(");
        var adapter = Scope(controller, "    public async ValueTask DisposeAsync()", "    private IUiDispatcher UiDispatcher");
        Assert.AreEqual("""
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
            """ + "\n\n", adapter);
        RequireOrdered(adapter,
            "var initializationTask = _initializationTask;",
            "var initializationCts = _initializationCts;",
            "await DisposeInitializationAsync(",
            "initializationTask,",
            "_disposeCts.Cancel,",
            "() => initializationCts?.Cancel(),",
            "() => initializationCts?.Dispose(),",
            "_disposeCts.Dispose)",
            ".ConfigureAwait(false);");
        Reject(adapter,
            "async ()", "_initializationCts?.Cancel()", "_initializationCts?.Dispose()", "await _initializationTask",
            "= new ", "try", "catch", "throw", "CancellationToken.None", ".Token",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            ".WaitAsync(", "GetAwaiter().GetResult()", ".Result", ".Wait(", " = null");

        RequireOrdered(controller,
            "    public async ValueTask DisposeAsync()",
            "    private IUiDispatcher UiDispatcher",
            "    internal static bool TryMergeRuntimeEvents(",
            "    internal static Task DisposeInitializationAsync(");
        var mergeAndDocumentation = Scope(controller,
            "    internal static bool TryMergeRuntimeEvents(",
            "    internal static Task DisposeInitializationAsync(");
        var merge = Scope(mergeAndDocumentation, "    internal static bool TryMergeRuntimeEvents(", "\n    }\n") + "\n    }";
        var documentation = mergeAndDocumentation[merge.Length..];
        RequireOnce(documentation, "    /// <summary>");
        RequireOnce(documentation, "    /// <remarks>");
        RequireOnce(documentation, "    /// <exception cref=\"ArgumentNullException\">");
        RequireOnce(documentation, "    /// <exception cref=\"Exception\">");
        RequireOnce(documentation, "    /// <exception cref=\"OperationCanceledException\">");
        RequireOnce(documentation, "    /// <exception cref=\"AggregateException\">");
        foreach (var line in documentation.Split('\n'))
        {
            Assert.IsTrue(line.Length == 0 || line.StartsWith("    ///", StringComparison.Ordinal),
                "Only appended XML documentation may separate the complete original merge method and cleanup core.");
        }
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void ControllerCleanup_SourceCore_ContainsFailuresWithoutChangingJoinPolicy()
    {
        var controller = ReadSource("CodeAlta.Tui/App/CodeAltaShellController.cs");
        RequireOnce(controller, "    internal static Task DisposeInitializationAsync(");
        var core = Scope(controller, "    internal static Task DisposeInitializationAsync(", "\n    }\n");
        Assert.AreEqual("""
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
            """, core + "\n    }");
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(cancelDisposal);",
            "ArgumentNullException.ThrowIfNull(cancelInitialization);",
            "ArgumentNullException.ThrowIfNull(disposeInitializationCancellation);",
            "ArgumentNullException.ThrowIfNull(disposeDisposalCancellation);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "cancelDisposal();",
            "cancelInitialization();",
            "if (initializationTask is not null)",
            "await initializationTask.ConfigureAwait(false);",
            "catch (OperationCanceledException)",
            "disposeInitializationCancellation();",
            "disposeDisposalCancellation();",
            "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        // The unconditional OCE catch is the old disposal-join policy, not the startup token filter.
        // Callback/release OCE and aggregates containing OCE remain direct failures; no classification.
        Reject(core,
            " when (", "IsCanceled", "IsFaulted", "IsCompleted", "IsCancellationRequested", "CancellationToken",
            "initializationTask.Exception", ".InnerException", ".Flatten(", ".GetBaseException(", "ReferenceEquals(", ".Distinct(",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            ".WaitAsync(", "GetAwaiter().GetResult()", ".Result", ".Wait(", "ConfigureAwait(true)",
            "Lazy<", "Interlocked.", "lock (", "Timer", "_initializationTask", "_initializationCts", "_disposeCts",
            "RuntimeEventPump", "CodeAltaApp", "CodeAltaTaskMonitor", "CodeAltaCrashReporter", "RunInitializationAsync(");
        Assert.IsTrue(controller.EndsWith(core + "\n    }\n}\n", StringComparison.Ordinal),
            "Only the appended core and existing class close may follow its XML documentation.");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void ControllerInitialization_Source_PreservesCompleteOriginalOutsideDisposal()
    {
        var controller = ReadSource("CodeAlta.Tui/App/CodeAltaShellController.cs");
        // Preserve the complete prefix, including constructor-time ownership and Start admission.
        Assert.IsTrue(controller.StartsWith("using CodeAlta.Tui.Threading;", StringComparison.Ordinal));
        Assert.IsTrue(controller.EndsWith("\n}\n", StringComparison.Ordinal));
        Assert.AreEqual("""
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
            """ + "\n\n", Scope(controller, "using CodeAlta.Tui.Threading;", "    public async ValueTask DisposeAsync()"));

        // Everything after disposal through the complete original final method is immutable too.
        // In particular, the local startup task can be left unjoined by the interaction-ready await.
        var suffix = Scope(controller, "    private IUiDispatcher UiDispatcher", "    internal static bool TryMergeRuntimeEvents(")
            + Scope(controller, "    internal static bool TryMergeRuntimeEvents(", "\n    }\n") + "\n    }";
        Assert.AreEqual("""
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
            """, suffix);
        // Both literal architecture allowances must survive the nineteen-line adapter replacement.
        var lines = controller.Split('\n');
        Assert.AreEqual("_initializationTask = Task.Run(", lines[72].Trim());
        Assert.AreEqual("var startupProviderLoadTask = Task.Run(", lines[447].Trim());
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void ControllerInitialization_Source_PreservesFrontendAndIndependentWorkBoundaries()
    {
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var bridge = ReadSource("CodeAlta.Tui/App/CodeAltaShellBridge.cs");
        var composition = ReadSource("CodeAlta.Tui/App/CodeAltaFrontendComposition.cs");
        var loop = ReadSource("CodeAlta.Tui/App/TerminalLoopCoordinator.cs");
        var dispatcher = ReadSource("CodeAlta.Tui/Threading/IUiDispatcher.cs");
        var terminalDispatcher = ReadSource("CodeAlta.Tui/Threading/TerminalUiDispatcher.cs");
        var sessionLoad = ReadSource("CodeAlta.Tui/App/SessionLoadCoordinator.cs");
        var providerCoordinator = ReadSource("CodeAlta.Tui/App/ModelProviderInitializationCoordinator.cs");
        var sessionState = ReadSource("CodeAlta.Tui/App/ShellSessionStateCoordinator.cs");
        var history = ReadSource("CodeAlta.Tui/App/SessionHistoryCoordinator.cs");
        var providerService = ReadSource("CodeAlta.Agent/ModelProviderInitializationService.cs");
        var monitor = ReadSource("CodeAlta.Tui/CodeAltaTaskMonitor.cs");

        // This is the actual stage-six controller route; the accepted Shell core keeps plain awaits.
        Assert.AreEqual("""
                async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()
                {
                    await ShellFrontendHost.DisposeFrontendResourcesAsync(
                        _projectionCoordinator.Dispose, _reminderUiCoordinator.Dispose,
                        () => _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false),
                        _fileEditorWorkspaceCoordinator.DisposeAsync,
                        _runtimeEventPump.DisposeAsync,
                        _shellController.DisposeAsync,
                        _promptDraftUiCoordinator.DisposeAsync);
                }
            """, Scope(app, "    async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()", "\n    }\n") + "\n    }");
        RequireOnce(app, """
                public async ValueTask DisposeAsync()
                    => await _frontendHost.DisposeAsync();
            """);
        Assert.AreEqual("""
                public TerminalLoopResult Tick(CancellationToken cancellationToken)
                {
                    _shellController.DrainPendingRuntimeEventsForUiFrame();
                    if (_disableTerminalLoopCallback)
                    {
                        DrainDeferredUiActions();
                        return TerminalLoopResult.Continue;
                    }

                    _shellAnimationRuntime.Advance();
                    var now = DateTimeOffset.UtcNow;
                    _workspaceCoordinator.RefreshRunningStatusElapsed(now);
                    _sidebarCoordinator.RefreshRecency(now, VerifyBindableAccess);
                    _initialCatalogStateCoordinator.EnsureStarted(cancellationToken);
                    if (!TryResolveInitialCatalogState(cancellationToken))
                    {
                        DrainDeferredUiActions();
                        return TerminalLoopResult.Continue;
                    }

                    var result = _terminalLoopCoordinator.OnIteration(cancellationToken);
                    DrainDeferredUiActions();
                    return result;
                }
            """, Scope(app, "    public TerminalLoopResult Tick(CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        RequireOnce(app, """
                internal Task InitializeModelProvidersAsync(CancellationToken cancellationToken)
                    => _modelProviderInitializationCoordinator.InitializeAsync(cancellationToken);
            """);
        RequireOnce(app, """
                internal void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
                    => _sessionStateCoordinator.TrySchedulePendingStartupSessionRestore(cancellationToken);
            """);
        RequireOnce(app, """
                internal Task EnsureSessionHistoryLoadedAsync(SessionView session, CancellationToken cancellationToken = default)
                    => _sessionHistoryCoordinator.EnsureLoadedAsync(session, cancellationToken);
            """);
        RequireOnce(app, "    internal void DispatchToUi(Action action) { ArgumentNullException.ThrowIfNull(action); var dispatcher = GetUiDispatcher(); UiDispatch.Post(dispatcher, action, allowInline: ShouldRunInlineOnCurrentSession(dispatcher.CheckAccess(), _terminalLoopCoordinator.HasStarted)); }");
        RequireOnce(bridge, """
                public Task InitializeModelProvidersAsync(CancellationToken cancellationToken)
                    => _app.InitializeModelProvidersAsync(cancellationToken);
            """);
        RequireOnce(bridge, """
                public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
                    => _app.TrySchedulePendingStartupSessionRestore(cancellationToken);
            """);
        RequireOnce(bridge, """
                public void PublishStartupCatalogProjectionReady()
                    => _app.PublishStartupCatalogProjectionReady();
            """);
        RequireOnce(bridge, """
                public void SetInitialized(bool isInitialized)
                    => _app.SetShellInitialized(isInitialized);
            """);
        RequireOnce(composition, """
                    var shellController = new CodeAltaShellController(
                        shell,
                        knownProjectImporter,
                        new ProjectCatalogStore(projectCatalog),
                        new RecoverableSessionSource(runtimeService),
                        new SessionDeleter(runtimeService),
                        providerDescriptors);
                    var runtimeEventPump = new RuntimeEventPump(runtimeService, shellController);
                    var terminalLoopCoordinator = new TerminalLoopCoordinator(
                        shellController,
                        runtimeEventPump,
                        uiDispatcher,
                        frontend.ApplyPendingSidebarSelection);
            """);
        RequireOnce(composition, """
                    var modelProviderInitializationCoordinator = new ModelProviderInitializationCoordinator(
                        modelProviderInitializationService,
                        providerDescriptors,
                        modelProviderStates,
                        frontend.DispatchToUi,
                        frontendEvents,
                        frontend.SetProviderSessionLoadStatus);
            """);
        RequireOnce(composition, "new SessionHistoryLoaderService(frontend.EnsureSessionHistoryLoadedAsync)");
        RequireOnce(composition, "ShellController = shellController,");
        Assert.AreEqual("""
                public void Start(CancellationToken cancellationToken)
                {
                    if (_started)
                    {
                        return;
                    }

                    _started = true;
                    _shellController.AttachUiDispatcher(_uiDispatcher);
                    _shellController.StartInitialization(cancellationToken);
                    _runtimeEventPump.Start(cancellationToken);
                }
            """, Scope(loop, "    public void Start(CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        RequireOnce(loop, """
                public TerminalLoopResult OnIteration(CancellationToken cancellationToken)
                {
                    Start(cancellationToken);
                    _applyPendingSidebarSelection();
                    return TerminalLoopResult.Continue;
                }
            """);

        // Canceling these wrappers does not remove or join their underlying queued actions.
        Assert.AreEqual("""
                Task InvokeAsync(Action action, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(action);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Task.FromCanceled(cancellationToken);
                    }

                    var task = InvokeAsync(action);
                    return cancellationToken.CanBeCanceled
                        ? task.WaitAsync(cancellationToken)
                        : task;
                }
            """, Scope(dispatcher, "    Task InvokeAsync(Action action, CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(action);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Task.FromCanceled<T>(cancellationToken);
                    }

                    var task = InvokeAsync(action);
                    return cancellationToken.CanBeCanceled
                        ? task.WaitAsync(cancellationToken)
                        : task;
                }
            """, Scope(dispatcher, "    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        RequireOnce(terminalDispatcher, """
                public void Post(Action action)
                {
                    ArgumentNullException.ThrowIfNull(action);
                    _dispatcher.Post(action);
                }

                public Task InvokeAsync(Action action)
                {
                    ArgumentNullException.ThrowIfNull(action);
                    return _dispatcher.InvokeAsync(action);
                }

                public Task<T> InvokeAsync<T>(Func<T> action)
                {
                    ArgumentNullException.ThrowIfNull(action);
                    return _dispatcher.InvokeAsync(action);
                }
            """);

        // Progressive enumeration/snapshot dispatch is awaited; scheduled restoration is not.
        Assert.AreEqual("""
                public async Task ApplyRecoverableSessionsProgressivelyAsync(
                    IReadOnlyList<ProjectDescriptor> projects,
                    CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(projects);

                    var recoveredSessions = new Dictionary<string, SessionViewDescriptor>(StringComparer.OrdinalIgnoreCase);
                    var appliedAny = false;
                    var pendingSinceLastApply = 0;
                    await foreach (var session in _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken))
                    {
                        appliedAny = true;
                        recoveredSessions[session.SessionId] = session;
                        pendingSinceLastApply++;
                        if (recoveredSessions.Count <= ProgressiveSnapshotThreshold || pendingSinceLastApply >= CoalescedSnapshotBatchSize)
                        {
                            pendingSinceLastApply = 0;
                            await ApplySnapshotAsync(projects, recoveredSessions, pruneMissingSessions: false, cancellationToken);
                        }
                    }

                    if (!appliedAny)
                    {
                        await _getUiDispatcher().InvokeAsync(
                            () =>
                            {
                                _shell.ApplyRecoveredCatalogState(projects, []);
                                _shell.TrySchedulePendingStartupSessionRestore(CancellationToken.None);
                            },
                            cancellationToken);
                        return;
                    }

                    await ApplySnapshotAsync(projects, recoveredSessions, pruneMissingSessions: true, cancellationToken);
                    if (await _recoverableSessionSource.ReconcileRecoverableSessionsAsync(cancellationToken))
                    {
                        recoveredSessions.Clear();
                        await foreach (var session in _recoverableSessionSource.ListRecoverableSessionsAsync(cancellationToken))
                        {
                            recoveredSessions[session.SessionId] = session;
                        }

                        await ApplySnapshotAsync(projects, recoveredSessions, pruneMissingSessions: true, cancellationToken);
                    }
                }

                private Task ApplySnapshotAsync(
                    IReadOnlyList<ProjectDescriptor> projects,
                    Dictionary<string, SessionViewDescriptor> recoveredSessions,
                    bool pruneMissingSessions,
                    CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sessions = recoveredSessions.Values
                        .OrderByDescending(static item => item.LastActiveAt)
                        .ToArray();

                    return _getUiDispatcher().InvokeAsync(
                        () =>
                        {
                            _shell.ApplyRecoveredCatalogState(projects, sessions, pruneMissingSessions);
                            _shell.TrySchedulePendingStartupSessionRestore(CancellationToken.None);
                        },
                        cancellationToken);
                }
            """, Scope(sessionLoad, "    public async Task ApplyRecoverableSessionsProgressivelyAsync(", "    private Task ApplySnapshotAsync(")
                + Scope(sessionLoad, "    private Task ApplySnapshotAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
                {
                    if (string.IsNullOrWhiteSpace(PendingStartupSessionRestoreId))
                    {
                        return;
                    }

                    var session = FindSession(PendingStartupSessionRestoreId);
                    if (session is null || !_modelProviderReadiness.IsModelProviderReady(session))
                    {
                        return;
                    }

                    var sessionId = PendingStartupSessionRestoreId;
                    PendingStartupSessionRestoreId = null;
                    _ = RestoreStartupSessionHistoryAsync(sessionId, cancellationToken);
                }
            """, Scope(sessionState, "    public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                public async Task RestoreStartupSessionHistoryAsync(string? sessionId, CancellationToken cancellationToken)
                {
                    var session = FindSession(sessionId);
                    if (session is null)
                    {
                        return;
                    }

                    await _historyLoader.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
                }
            """, Scope(sessionState, "    public async Task RestoreStartupSessionHistoryAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                public async Task EnsureLoadedAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(session);

                    if (!_canLoadHistory(session))
                    {
                        return;
                    }

                    var tab = _ensureSessionTab(session);
                    var loadTask = GetOrStartLoadTask(tab, session, cancellationToken);
                    await loadTask;
                }
            """, Scope(history, "    public async Task EnsureLoadedAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private Task GetOrStartLoadTask(
                    OpenSessionState tab,
                    SessionViewDescriptor session,
                    CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(tab);
                    ArgumentNullException.ThrowIfNull(session);

                    if (tab.HistoryLoaded)
                    {
                        return Task.CompletedTask;
                    }

                    if (tab.HistoryLoadTask is { } existingTask)
                    {
                        return existingTask.WaitAsync(cancellationToken);
                    }

                    var loadTask = Task.Run(() => LoadCoreAsync(session, tab, cancellationToken));
                    tab.HistoryLoadTask = loadTask;
                    return loadTask.WaitAsync(cancellationToken);
                }
            """, Scope(history, "    private Task GetOrStartLoadTask(", "\n    }\n") + "\n    }");

        // The normal first reader stop is repeated in finally. A Cancel failure in the FINAL stop
        // can skip its reader join; a first-stop failure alone does not prove that the reader is unjoined.
        // This is a separate hypothetical exception channel, not a discovered throwing registration.
        Assert.AreEqual("""
                public async Task InitializeAsync(CancellationToken cancellationToken)
                {
                    var progress = new ProviderInitializationProgress(_providerDescriptors);
                    var descriptorsByProviderId = _providerDescriptors.ToDictionary(
                        static descriptor => ModelProviderId.NormalizeValue(descriptor.ProviderId.Value),
                        StringComparer.OrdinalIgnoreCase);
                    var completedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ReportProviderInitializationProgress(progress.Snapshot(null));
                    using var stateReaderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var stateReaderTask = ApplyProviderStateChangesAsync(
                        descriptorsByProviderId,
                        completedProviderIds,
                        progress,
                        stateReaderCts.Token);
                    try
                    {
                        await _providerInitializationService.InitializeAllAsync(cancellationToken).ConfigureAwait(false);

                        await StopProviderStateReaderAsync(stateReaderCts, stateReaderTask).ConfigureAwait(false);

                        foreach (var providerState in _providerInitializationService.CurrentStates)
                        {
                            await ApplyProviderStateChangeAsync(
                                    providerState,
                                    descriptorsByProviderId,
                                    completedProviderIds,
                                    progress,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        await StopProviderStateReaderAsync(stateReaderCts, stateReaderTask).ConfigureAwait(false);

                        ReportProviderInitializationProgress(null);
                    }
                }
            """, Scope(providerCoordinator, "    public async Task InitializeAsync(CancellationToken cancellationToken)", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private async Task ApplyProviderStateChangesAsync(
                    IReadOnlyDictionary<string, ModelProviderDescriptor> descriptorsByProviderId,
                    HashSet<string> completedProviderIds,
                    ProviderInitializationProgress progress,
                    CancellationToken cancellationToken)
                {
                    await foreach (var change in _providerInitializationService.StreamStateChangesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await ApplyProviderStateChangeAsync(
                                change.State,
                                descriptorsByProviderId,
                                completedProviderIds,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                private static async Task StopProviderStateReaderAsync(CancellationTokenSource stateReaderCts, Task stateReaderTask)
                {
                    stateReaderCts.Cancel();
                    try
                    {
                        await stateReaderTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stateReaderCts.IsCancellationRequested)
                    {
                    }
                }
            """, Scope(providerCoordinator, "    private async Task ApplyProviderStateChangesAsync(", "    private static async Task StopProviderStateReaderAsync(")
                + Scope(providerCoordinator, "    private static async Task StopProviderStateReaderAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private async Task ApplyProviderStateChangeAsync(
                    ModelProviderStateSnapshot providerState,
                    IReadOnlyDictionary<string, ModelProviderDescriptor> descriptorsByProviderId,
                    HashSet<string> completedProviderIds,
                    ProviderInitializationProgress progress,
                    CancellationToken cancellationToken)
                {
                    var key = ModelProviderId.NormalizeValue(providerState.ProviderId.Value);
                    if (!descriptorsByProviderId.TryGetValue(key, out var descriptor))
                    {
                        return;
                    }

                    var isTerminal = IsTerminalAvailability(providerState.Availability);
                    lock (completedProviderIds)
                    {
                        if (!isTerminal && completedProviderIds.Contains(key))
                        {
                            return;
                        }
                    }

                    var state = await EnsureProviderStateAsync(providerState.ProviderId, descriptor.DisplayName, cancellationToken).ConfigureAwait(false);
                    lock (completedProviderIds)
                    {
                        if (!isTerminal && completedProviderIds.Contains(key))
                        {
                            return;
                        }
                    }

                    _dispatchToUi(
                        () =>
                        {
                            ApplyProviderState(providerState, state);
                            LogInfo(
                                $"Model provider state updated provider={providerState.ProviderId.Value} displayName={state.DisplayName} availability={state.Availability} models={state.Models.Count} status={state.StatusMessage}");
                            PublishProviderStateChanged(providerState.ProviderId);
                        });

                    if (isTerminal)
                    {
                        lock (completedProviderIds)
                        {
                            if (!completedProviderIds.Add(key))
                            {
                                return;
                            }
                        }

                        ReportProviderInitializationProgress(progress.Snapshot(descriptor));
                    }
                }
            """, Scope(providerCoordinator, "    private async Task ApplyProviderStateChangeAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private async Task<ModelProviderState> EnsureProviderStateAsync(
                    ModelProviderId providerId,
                    string? displayName,
                    CancellationToken cancellationToken)
                {
                    if (_modelProviderStates.TryGetValue(providerId.Value, out var state))
                    {
                        return state;
                    }

                    var completion = new TaskCompletionSource<ModelProviderState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _dispatchToUi(
                        () =>
                        {
                            try
                            {
                                if (!_modelProviderStates.TryGetValue(providerId.Value, out var state))
                                {
                                    state = new ModelProviderState(
                                        providerId,
                                        string.IsNullOrWhiteSpace(displayName) ? providerId.Value : displayName.Trim());
                                    _modelProviderStates[providerId.Value] = state;
                                }

                                completion.SetResult(state);
                            }
                            catch (Exception ex)
                            {
                                completion.SetException(ex);
                            }
                        });

                    return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            """, Scope(providerCoordinator, "    private async Task<ModelProviderState> EnsureProviderStateAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private void ReportProviderInitializationProgress(ProviderInitializationProgressSnapshot? progress)
                {
                    if (_setProviderInitializationStatus is null)
                    {
                        return;
                    }

                    var status = progress is null
                        ? null
                        : FormatProviderInitializationStatus(
                            progress.CompletedProviderCount,
                            progress.TotalProviderCount,
                            progress.InitializingProviderDisplayNames);
                    var version = Interlocked.Increment(ref _providerInitializationStatusVersion);
                    _dispatchToUi(
                        () =>
                        {
                            if (version == Volatile.Read(ref _providerInitializationStatusVersion))
                            {
                                _setProviderInitializationStatus(status);
                            }
                        });
                }
            """, Scope(providerCoordinator, "    private void ReportProviderInitializationProgress(", "\n    }\n") + "\n    }");

        // Caller cancellation wraps independently retained refresh work, whose probes use a timeout
        // token rather than the controller token. Joining wrapper tasks is not provider termination.
        RequireOnce(providerService, "private readonly Dictionary<string, Task> _refreshTasks = new(StringComparer.OrdinalIgnoreCase);");
        Assert.AreEqual("""
                public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
                {
                    var tasks = _registry.ListProviders(includeDisabled: true)
                        .Select(descriptor => StartRefreshAsync(descriptor, cancellationToken))
                        .ToArray();

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            """, Scope(providerService, "    public async Task InitializeAllAsync(", "\n    }\n") + "\n    }");
        Assert.AreEqual("""
                private Task StartRefreshAsync(ModelProviderDescriptor descriptor, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(descriptor);
                    cancellationToken.ThrowIfCancellationRequested();

                    var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
                    Task refreshTask;
                    lock (_gate)
                    {
                        if (_refreshTasks.TryGetValue(key, out refreshTask!))
                        {
                            return refreshTask.WaitAsync(cancellationToken);
                        }

                        refreshTask = RefreshProviderCoreAsync(descriptor, key);
                        _refreshTasks[key] = refreshTask;
                    }

                    return refreshTask.WaitAsync(cancellationToken);
                }
            """, Scope(providerService, "    private Task StartRefreshAsync(", "\n    }\n") + "\n    }");
        var refresh = Scope(providerService, "    private async Task RefreshProviderCoreAsync(ModelProviderDescriptor descriptor, string key)", "\n    }\n");
        RequireOnce(refresh, """
                        using var timeout = new CancellationTokenSource(_options.DefaultProbeTimeout);
                        var runtime = await _registry.GetOrCreateRuntimeAsync(descriptor.ProviderId, timeout.Token).ConfigureAwait(false);
                        await runtime.StartAsync(timeout.Token).ConfigureAwait(false);
                        var probe = await runtime.ProbeAsync(timeout.Token).ConfigureAwait(false);
            """);
        RequireOnce(refresh, """
                    catch (OperationCanceledException)
                    {
                        PublishFailureState(descriptor, ModelProviderAvailability.Failed, $"Provider probe timed out after {_options.DefaultProbeTimeout}.", "Timeout");
                    }
                    catch (Exception ex)
                    {
                        var (availability, message, category) = ClassifyFailure(ex);
                        PublishFailureState(descriptor, availability, message, category);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _refreshTasks.Remove(key);
                        }
                    }
            """);
        Reject(refresh, "cancellationToken", "CreateLinkedTokenSource");

        // Preserve the monitor's own Flatten/fatal-reporting policy as data, not a seam invocation.
        // Its observation continuation is not an extra join, and fatal reporting may end the process.
        Assert.AreEqual("""
                public static void Observe(Task task, string source)
                {
                    ArgumentNullException.ThrowIfNull(task);
                    ArgumentException.ThrowIfNullOrWhiteSpace(source);

                    if (task.IsCompleted)
                    {
                        ReportIfFaulted(task, source);
                        return;
                    }

                    _ = task.ContinueWith(
                        static (completedTask, state) => ReportIfFaulted(completedTask, (string)state!),
                        source,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                private static void ReportIfFaulted(Task task, string source)
                {
                    if (!task.IsFaulted || task.Exception is null)
                    {
                        return;
                    }

                    CodeAltaCrashReporter.ReportFatalTaskException(source, task.Exception.Flatten());
                }
            """, Scope(monitor, "    public static void Observe(Task task, string source)", "    private static void ReportIfFaulted(")
                + Scope(monitor, "    private static void ReportIfFaulted(", "\n    }\n") + "\n    }");
    }

    // Quoted production (including record construction, logging and monitoring) is source DATA only.
    // The retained top-level join is not descendant completion, queued-action/frame draining or a
    // join of independent/external cancellation callers. No throwing registration or ordinary CTS
    // release failure is established here, nor an updater-style token-read race in serialized use.
    // The None-token fallback can stay pending or replace errors; existing logging/swallowing and
    // cache-lock policies determine what actually escapes. Only those escaped errors reach disposal.
    // Own cancellation may traverse linked registrations; external callers retain their own errors.
    // No new repeated/concurrent disposal policy or noncooperative termination guarantee is implied.
    // Waiting after cancellation errors can delay releases, drafts and later owners indefinitely.
    // A pending internal join avoids capture; completed awaits need not switch threads. App/Shell's
    // plain-await frontend traversal retains later draft context. Default fatal monitoring is unchanged;
    // no process survival, metadata/editor/reminder lifetime or hidden-acquisition qualification follows.
    // Named reads and existing writerless assembly logging are nonzero I/O, not isolation.
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(SourceRoot(), relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));

    private static string Scope(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing source anchor: {startAnchor}");
        var end = source.IndexOf(endAnchor, start + startAnchor.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"Missing following source anchor: {endAnchor}");
        return source[start..end];
    }

    private static void RequireOnce(string source, string expected)
    {
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, $"Missing expected wiring: {expected}");
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), $"Duplicate wiring: {expected}");
    }

    private static void RequireOrdered(string source, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            RequireOnce(source, item);
            var current = source.IndexOf(item, StringComparison.Ordinal);
            Assert.IsTrue(current > previous, $"Out-of-order wiring: {item}");
            previous = current;
        }
    }

    private static void Reject(string source, params string[] forbidden)
    {
        foreach (var item in forbidden)
        {
            Assert.IsFalse(source.Contains(item, StringComparison.Ordinal), $"Unexpected wiring: {item}");
        }
    }
}
