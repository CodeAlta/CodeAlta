using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class RuntimeEventPumpSourceTests
{
    [TestMethod]
    public void PumpCleanup_SourceWiring_UsesMandatoryCore()
    {
        var pump = ReadSource("CodeAlta.Tui/App/RuntimeEventPump.cs");
        RequireOnce(pump, "await DisposePumpAsync(");

        var adapter = Scope(pump, "    public async ValueTask DisposeAsync()", "    private async Task RunAsync(");
        Assert.AreEqual("""
                public async ValueTask DisposeAsync()
                {
                    var pumpTask = _pumpTask;
                    var pumpCts = _pumpCts;
                    await DisposePumpAsync(
                        pumpTask,
                        _disposeCts.Cancel,
                        () => pumpCts?.Cancel(),
                        () => pumpCts?.Dispose(),
                        _disposeCts.Dispose).ConfigureAwait(false);
                }
            """ + "\n\n", adapter);
        RequireOrdered(adapter,
            "var pumpTask = _pumpTask;",
            "var pumpCts = _pumpCts;",
            "await DisposePumpAsync(",
            "pumpTask,",
            "_disposeCts.Cancel,",
            "() => pumpCts?.Cancel(),",
            "() => pumpCts?.Dispose(),",
            "_disposeCts.Dispose).ConfigureAwait(false);");
        Reject(adapter,
            "async ()", "_pumpCts?.Cancel()", "_pumpCts?.Dispose()", "await _pumpTask",
            "new ", "try", "catch", "throw", "CancellationToken.None", ".Token",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            ".WaitAsync(", "GetAwaiter().GetResult()", ".Result", ".Wait(", " = null");

        RequireOrdered(pump,
            "    public async ValueTask DisposeAsync()",
            "    private async Task RunAsync(CancellationToken cancellationToken)",
            "    internal static Task DisposePumpAsync(");
        var runAndDocumentation = Scope(pump,
            "    private async Task RunAsync(CancellationToken cancellationToken)",
            "    internal static Task DisposePumpAsync(");
        var run = Scope(runAndDocumentation,
            "    private async Task RunAsync(CancellationToken cancellationToken)", "\n    }\n") + "\n    }";
        var documentation = runAndDocumentation[run.Length..];
        RequireOnce(documentation, "    /// <summary>");
        RequireOnce(documentation, "    /// <remarks>");
        RequireOnce(documentation, "    /// <exception cref=\"ArgumentNullException\">");
        RequireOnce(documentation, "    /// <exception cref=\"Exception\">");
        RequireOnce(documentation, "    /// <exception cref=\"OperationCanceledException\">");
        RequireOnce(documentation, "    /// <exception cref=\"AggregateException\">");
        foreach (var line in documentation.Split('\n'))
        {
            Assert.IsTrue(line.Length == 0 || line.StartsWith("    ///", StringComparison.Ordinal),
                "Only appended XML documentation may separate the original RunAsync and cleanup core.");
        }
    }

    [TestMethod]
    public void PumpCleanup_SourceCore_ContainsFailuresWithoutChangingJoinPolicy()
    {
        var pump = ReadSource("CodeAlta.Tui/App/RuntimeEventPump.cs");
        RequireOnce(pump, "    internal static Task DisposePumpAsync(");
        var core = Scope(pump, "    internal static Task DisposePumpAsync(", "\n    }\n");

        // Exact operation: the OCE catch belongs only to the original-task join. In particular,
        // a faulted task whose await throws OCE remains suppressed, without task/token classification.
        Assert.AreEqual("""
                internal static Task DisposePumpAsync(
                    Task? pumpTask,
                    Action cancelDisposal,
                    Action cancelPump,
                    Action disposePumpCancellation,
                    Action disposeDisposalCancellation)
                {
                    ArgumentNullException.ThrowIfNull(cancelDisposal);
                    ArgumentNullException.ThrowIfNull(cancelPump);
                    ArgumentNullException.ThrowIfNull(disposePumpCancellation);
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
                            cancelPump();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        if (pumpTask is not null)
                        {
                            try
                            {
                                await pumpTask.ConfigureAwait(false);
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
                            disposePumpCancellation();
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
            "ArgumentNullException.ThrowIfNull(cancelPump);",
            "ArgumentNullException.ThrowIfNull(disposePumpCancellation);",
            "ArgumentNullException.ThrowIfNull(disposeDisposalCancellation);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "cancelDisposal();",
            "cancelPump();",
            "if (pumpTask is not null)",
            "await pumpTask.ConfigureAwait(false);",
            "catch (OperationCanceledException)",
            "disposePumpCancellation();",
            "disposeDisposalCancellation();",
            "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        Reject(core,
            " when (", "IsCanceled", "IsFaulted", "IsCompleted", "IsCancellationRequested", "CancellationToken",
            "pumpTask.Exception", ".InnerException", ".Flatten(", ".GetBaseException(", "ReferenceEquals(", ".Distinct(",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            ".WaitAsync(", "GetAwaiter().GetResult()", ".Result", ".Wait(", "ConfigureAwait(true)",
            "Lazy<", "Interlocked.", "lock (", "Timer", "_pumpTask", "_pumpCts", "_disposeCts",
            "CodeAltaApp", "CodeAltaTaskMonitor", "CodeAltaCrashReporter", "StreamEventsAsync(", "QueueRuntimeEvent(");
        Assert.IsTrue(pump.EndsWith(core + "\n    }\n}\n", StringComparison.Ordinal),
            "Only the appended core and existing class close may follow its XML documentation.");
    }

    [TestMethod]
    public void PumpOwnership_SourceWiring_PreservesStartStreamAndFrontendBoundaries()
    {
        var pump = ReadSource("CodeAlta.Tui/App/RuntimeEventPump.cs");
        // This complete prefix also preserves the existing line-34 Task.Run allowance: no import,
        // field, constructor, latch or scheduling changes are part of disposal failure containment.
        Assert.AreEqual("""
            using CodeAlta.Orchestration.Runtime;

            namespace CodeAlta.Tui.App;

            internal sealed class RuntimeEventPump : IAsyncDisposable
            {
                private readonly SessionRuntimeService _runtimeService;
                private readonly ISessionRuntimeEventProjector _runtimeEventProjector;
                private readonly CancellationTokenSource _disposeCts = new();
                private CancellationTokenSource? _pumpCts;
                private Task? _pumpTask;

                public RuntimeEventPump(
                    SessionRuntimeService runtimeService,
                    ISessionRuntimeEventProjector runtimeEventProjector)
                {
                    ArgumentNullException.ThrowIfNull(runtimeService);
                    ArgumentNullException.ThrowIfNull(runtimeEventProjector);

                    _runtimeService = runtimeService;
                    _runtimeEventProjector = runtimeEventProjector;
                }

                public void Start(CancellationToken cancellationToken)
                {
                    if (_pumpTask is not null)
                    {
                        return;
                    }

                    _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
                    // Runtime event streaming is a background pump. Delivery back into the shell happens via
                    // the shell controller's explicit UI dispatch path.
                    _pumpTask = Task.Run(
                        () => RunAsync(_pumpCts.Token),
                        CancellationToken.None);
                    global::CodeAlta.Tui.CodeAltaTaskMonitor.Observe(_pumpTask, "Runtime event pump");
                }
            """ + "\n\n", Scope(pump, "using CodeAlta.Orchestration.Runtime;", "    public async ValueTask DisposeAsync()"));
        Assert.IsTrue(pump.StartsWith("using CodeAlta.Orchestration.Runtime;\n", StringComparison.Ordinal));
        RequireOnce(pump, "    public void Start(CancellationToken cancellationToken)");
        RequireOnce(pump, "    private async Task RunAsync(CancellationToken cancellationToken)");
        Assert.AreEqual("""
                private async Task RunAsync(CancellationToken cancellationToken)
                {
                    try
                    {
                        await foreach (var runtimeEvent in _runtimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
                        {
                            _runtimeEventProjector.QueueRuntimeEvent(runtimeEvent, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            """, Scope(pump, "    private async Task RunAsync(CancellationToken cancellationToken)", "\n    }\n") + "\n    }");

        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        RequireOnce(app, "    private readonly RuntimeEventPump _runtimeEventPump;");
        RequireOnce(app, "    private readonly TerminalLoopCoordinator _terminalLoopCoordinator;");
        RequireOnce(app, """
                    _shellController = composition.ShellController;
                    _runtimeEventPump = composition.RuntimeEventPump;
                    _terminalLoopCoordinator = composition.TerminalLoopCoordinator;
            """);
        RequireOnce(app, "public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        RequireOnce(app, "IAsyncDisposable? IShellFrontendHostLifecycle.OwnedServices => _ownedServices;");
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
        Assert.AreEqual("""
                public void PrepareForRun()
                {
                    StartupNavigatorSettingsApplier.Apply(_sessionStateCoordinator, UiLogger);
                    SetStatus(SR.T("Connecting providers..."), showSpinner: true);
                }
            """, Scope(app, "    public void PrepareForRun()", "\n    }\n") + "\n    }");
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
        Reject(app, "_runtimeEventPump.Start(", "_runtimeEventPump.DisposeAsync()", "RuntimeEventPump.DisposePumpAsync(");

        var composition = ReadSource("CodeAlta.Tui/App/CodeAltaFrontendComposition.cs");
        RequireOnce(composition, "public required RuntimeEventPump RuntimeEventPump { get; init; }");
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
                        ShellController = shellController,
                        RuntimeEventPump = runtimeEventPump,
                        TerminalLoopCoordinator = terminalLoopCoordinator,
            """);
        RequireOnce(composition, "new RuntimeEventPump(");
        Reject(composition, "runtimeEventPump.Start(", "runtimeEventPump.DisposeAsync(", "RuntimeEventPump.DisposePumpAsync(");

        var loop = ReadSource("CodeAlta.Tui/App/TerminalLoopCoordinator.cs");
        Assert.AreEqual("""
            using CodeAlta.Tui.Threading;
            using XenoAtom.Terminal.UI;

            namespace CodeAlta.Tui.App;

            internal sealed class TerminalLoopCoordinator
            {
                private readonly CodeAltaShellController _shellController;
                private readonly RuntimeEventPump _runtimeEventPump;
                private readonly IUiDispatcher _uiDispatcher;
                private readonly Action _applyPendingSidebarSelection;
                private bool _started;

                public TerminalLoopCoordinator(
                    CodeAltaShellController shellController,
                    RuntimeEventPump runtimeEventPump,
                    IUiDispatcher uiDispatcher,
                    Action applyPendingSidebarSelection)
                {
                    ArgumentNullException.ThrowIfNull(shellController);
                    ArgumentNullException.ThrowIfNull(runtimeEventPump);
                    ArgumentNullException.ThrowIfNull(uiDispatcher);
                    ArgumentNullException.ThrowIfNull(applyPendingSidebarSelection);

                    _shellController = shellController;
                    _runtimeEventPump = runtimeEventPump;
                    _uiDispatcher = uiDispatcher;
                    _applyPendingSidebarSelection = applyPendingSidebarSelection;
                }

                public bool HasStarted => _started;

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

                public TerminalLoopResult OnIteration(CancellationToken cancellationToken)
                {
                    Start(cancellationToken);
                    _applyPendingSidebarSelection();
                    return TerminalLoopResult.Continue;
                }
            }
            """ + "\n", loop);

        var controller = ReadSource("CodeAlta.Tui/App/CodeAltaShellController.cs");
        RequireOnce(controller, "internal sealed class CodeAltaShellController : ISessionRuntimeEventProjector, IAsyncDisposable");
        RequireOnce(controller, "private const int UiFrameRuntimeEventDrainBatchSize = 64;");
        RequireOnce(controller, "private readonly ConcurrentQueue<SessionRuntimeEvent> _pendingRuntimeEvents = new();");
        RequireOnce(controller, "private IUiDispatcher? _uiDispatcher;");
        RequireOnce(controller, "private int _runtimeEventDrainScheduled;");
        Assert.AreEqual("""
                public void AttachUiDispatcher(IUiDispatcher uiDispatcher)
                {
                    ArgumentNullException.ThrowIfNull(uiDispatcher);
                    _uiDispatcher = uiDispatcher;
                    if (!_pendingRuntimeEvents.IsEmpty)
                    {
                        ScheduleRuntimeEventDrain();
                    }
                }
            """, Scope(controller, "    public void AttachUiDispatcher(IUiDispatcher uiDispatcher)", "\n    }\n") + "\n    }");
        // Preserve the complete queue/frame-drain implementation, including merge and batching calls.
        Assert.AreEqual("""
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
            """ + "\n\n", Scope(controller,
                "    public void QueueRuntimeEvent(SessionRuntimeEvent runtimeEvent, CancellationToken cancellationToken)",
                "    public Task SelectGlobalScopeAsync(CancellationToken cancellationToken)"));
        Assert.AreEqual("""
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
            """, Scope(controller, "    private void ScheduleRuntimeEventDrain()", "\n    }\n") + "\n    }");

        var monitor = ReadSource("CodeAlta.Tui/CodeAltaTaskMonitor.cs");
        // This intentionally preserves the monitor's Flatten and fatal reporting. It is not the
        // cleanup core's direct-error policy, nor a join or process-survival guarantee.
        Assert.AreEqual("""
            namespace CodeAlta.Tui;

            internal static class CodeAltaTaskMonitor
            {
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
            }
            """ + "\n", monitor);
    }

    // Eight literal reads across six named checkout files. No runtime calls, discovery or setup.
    // Source assertions do not prove stream/UI drain, process survival, external cancellation
    // traversal completion or noncooperative termination. Pending pump joins avoid captured context;
    // completed awaits may stay inline. App's plain await retains its later frontend context.
    // Existing writerless assembly logging and source reads remain nonzero I/O, not qualification.
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
