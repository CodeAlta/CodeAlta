using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Screenshot;
using XenoAtom.Terminal.UI.Graphics;

namespace CodeAlta.Tui.Views;

internal sealed class DeferredCodeAltaApp : IAsyncDisposable
{
    private readonly Padder _rootHost;
    private readonly Padder _sidebarHost;
    private readonly Padder _workspaceHost;
    private readonly Padder _commandBarHost;
    private readonly ToastHost _toastHost;
    private readonly TerminalImageGraphicsPresenter _graphicsPresenter;
    private readonly CodeAltaUpdateService _updateService = new();
    private readonly PluginRuntimeManager? _prestartedPluginRuntime;
    private readonly Lazy<Task> _disposeTask;
    private CancellationTokenSource? _startupCancellation;
    private CancellationToken _startupToken;
    private Task<CodeAltaOwnedServices>? _ownedServicesTask;
    private CodeAltaApp? _app;
    private ConfigRecoveryDialog? _configRecoveryDialog;
    private ConfigRecoveryService? _configRecovery;
    private Exception? _startupFailure;
    private bool _configRecoveryChecked;
    private bool _exitRequested;
    private bool _openProvidersAfterStartup;
    private bool _updateToastShown;
    private bool _runStarted;
    private bool _stopRequested;

    public DeferredCodeAltaApp(PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        _prestartedPluginRuntime = prestartedPluginRuntime;
        var sixelOptions = new TerminalSixelEncoderOptions();
        _graphicsPresenter = new TerminalImageGraphicsPresenter(new TerminalImageGraphicsPresenterOptions
        {
            SixelOptions = sixelOptions,
        });

        // Build a synchronous placeholder shell first so Program can enter Terminal.RunAsync
        // without awaiting startup work before the UI claims the main session.
        _sidebarHost = CreateStretchHost(BuildMessage(SR.T("Loading sidebar...")));
        _workspaceHost = CreateStretchHost(BuildWorkspacePlaceholder(SR.T("Starting CodeAlta...")));
        _commandBarHost = CreateStretchHost(new Placeholder { IsVisible = false });
        _toastHost = new ToastHost(
            new CodeAltaShellView(
                _sidebarHost,
                _workspaceHost,
                _commandBarHost,
                CodeAltaGlobalCommandConfigurator.Configure).Root);
        _rootHost = CreateStretchHost(_toastHost);
        _rootHost.RegisterClipboardScreenshotCommand();
        _disposeTask = CreateDeferredDisposal(
            stopStartup: () => _stopRequested = true,
            disposeCore: DisposeCoreAsync);
        _updateService.Start();
    }

    public CodeAltaUpdateCheckSnapshot UpdateCheckSnapshot => _updateService.Snapshot;

    public ValueTask<TerminalInstance> RunAsync(CancellationToken cancellationToken)
    {
        _startupCancellation = BeginDeferredRun(
            ref _runStarted, _stopRequested, cancellationToken);
        _startupToken = _startupCancellation.Token;

        return Terminal.RunAsync(
            _rootHost,
            _ => OnIteration(cancellationToken),
            new TerminalRunOptions
            {
                GraphicsPresenter = _graphicsPresenter,
                UpdateWaitDuration = TimeSpan.FromMilliseconds(1),
            },
            cancellationToken);
    }

    public ValueTask DisposeAsync() => new(_disposeTask.Value);

    private Task DisposeCoreAsync()
    {
        // The lazy initializer has stopped admission. Capture before cancellation callbacks run.
        var app = _app;
        var startupTask = _ownedServicesTask;
        var reportedStartupFailure = _startupFailure;
        var startupToken = _startupToken;
        var startupCancellation = _startupCancellation;

        return DisposeDeferredStartupAsync(
            app,
            startupTask,
            reportedStartupFailure,
            startupToken,
            cancelStartup: () => startupCancellation?.Cancel(),
            disposeUpdate: _updateService.DisposeAsync,
            disposePresenter: _graphicsPresenter.Dispose,
            disposeStartupCancellation: () => startupCancellation?.Dispose(),
            beginOwnedShutdown: () =>
            {
                // A constructed app is proof that this exact startup original returned its services.
                // Signal their controls before ShellFrontendHost enters its unchanged plugin barrier.
                if (app is not null) startupTask!.GetAwaiter().GetResult().BeginShutdownControls();
            },
            quiescePlugins: () => app is null ? Task.CompletedTask
                : startupTask!.GetAwaiter().GetResult().PluginRuntime.QuiesceAgentEventsAsync());
    }

    private TerminalLoopResult OnIteration(CancellationToken cancellationToken)
    {
        if (_stopRequested || _exitRequested)
        {
            return TerminalLoopResult.Stop;
        }

        if (_app is not null)
        {
            SyncUpdateNotifications();
            return _app.Tick(cancellationToken);
        }

        if (!_configRecoveryChecked && !EnsureConfigCanLoadBeforeStartup())
        {
            return TerminalLoopResult.Continue;
        }

        if (_configRecoveryDialog is not null)
        {
            return TerminalLoopResult.Continue;
        }

        if (_startupFailure is not null)
        {
            return TerminalLoopResult.Continue;
        }

        if (!TryStartDeferredServices(
                _stopRequested,
                ref _ownedServicesTask,
                _startupToken,
                token => CodeAltaOwnedServices.CreateAsync(
                    token, _prestartedPluginRuntime)))
        {
            return TerminalLoopResult.Stop;
        }

        // A successful admission retains the mandatory factory's non-null task.
        var startupTask = _ownedServicesTask!;
        if (!startupTask.IsCompleted)
        {
            // Keep async service startup behind the terminal loop so the real app is attached
            // only after the UI is already running on the main session.
            return TerminalLoopResult.Continue;
        }

        if (_stopRequested)
        {
            return TerminalLoopResult.Stop;
        }

        try
        {
            var ownedServices = startupTask.GetAwaiter().GetResult();
            _app = CodeAltaApp.Create(ownedServices, _updateService);
            _app.PrepareForRun();
            _toastHost.Content = _app.GetRoot();
            if (_openProvidersAfterStartup)
            {
                _openProvidersAfterStartup = false;
                _ = _app.OpenModelProvidersAsync();
            }
        }
        catch (OperationCanceledException ex) when (
            IsExpectedDeferredStartupCancellation(
                startupTask, ex, _startupToken,
                _startupToken.IsCancellationRequested))
        {
            return TerminalLoopResult.Continue;
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _sidebarHost.Content = BuildMessage(SR.T("Startup failed."));
            _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("CodeAlta startup failed: {0}", ex.Message));
            _commandBarHost.Content = new Placeholder { IsVisible = false };
            return TerminalLoopResult.Continue;
        }

        SyncUpdateNotifications();
        return _app.Tick(cancellationToken);
    }

    private void SyncUpdateNotifications()
    {
        _updateService.SynchronizeUiState();
        var snapshot = _updateService.Snapshot;
        if (_updateToastShown || !snapshot.HasNewerVersion)
        {
            return;
        }

        _updateToastShown = true;
        ToastService.Show(() => new Toast
        {
            Title = SR.T("Update available"),
            Content = CodeAltaUpdateVisualFactory.CreateToastContent(snapshot, CopyUpdateCommand),
            Severity = ToastSeverity.Info,
            Duration = TimeSpan.FromSeconds(10),
            ShowCloseButton = true,
        });
    }

    private void CopyUpdateCommand(string command)
        => _rootHost.App?.Terminal.Clipboard.TrySetText(command);

    private bool EnsureConfigCanLoadBeforeStartup()
    {
        _configRecoveryChecked = true;
        if (_rootHost.App is not { } app)
        {
            _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("CodeAlta config needs repair. Waiting for the terminal UI..."));
            _configRecoveryChecked = false;
            return false;
        }

        _configRecovery ??= new ConfigRecoveryService(GetGlobalRoot(), new TextFileCodec());
        _configRecoveryDialog = PrepareConfigRecovery(
            _configRecovery,
            saveAndContinue: () =>
            {
                if (_stopRequested || _ownedServicesTask is not null)
                {
                    return;
                }

                _configRecoveryDialog = null;
                _startupFailure = null;
                _openProvidersAfterStartup = _configRecovery.CreatedDefault;
            },
            exit: () => _exitRequested = true);
        _openProvidersAfterStartup = _configRecovery.CreatedDefault;
        if (_configRecoveryDialog is null) return true;
        _sidebarHost.Content = BuildMessage(SR.T("Config recovery"));
        _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("Repair ~/.alta/config.toml to continue startup."));
        _commandBarHost.Content = new Placeholder { IsVisible = false };
        _configRecoveryDialog.Show(app);
        return false;
    }

    // Storage-only preflight seam: tests exercise the actual route without constructing the
    // deferred application (whose existing constructor starts update checking).
    internal static ConfigRecoveryDialog? PrepareConfigRecovery(ConfigRecoveryService recovery, Action saveAndContinue, Action exit)
    {
        recovery.Reload();
        return recovery.IsReady ? null : new ConfigRecoveryDialog(recovery, saveAndContinue, exit);
    }

    /// <summary>
    /// Admits one terminal run and creates its independently owned, linked startup cancellation source.
    /// </summary>
    /// <remarks>
    /// Run/iteration and disposal are serialized by the caller. Latch before source creation so a
    /// failed admission cannot be retried. The caller stores the returned source before terminal entry.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Disposal has stopped admission.</exception>
    /// <exception cref="InvalidOperationException">A run has already been admitted.</exception>
    internal static CancellationTokenSource BeginDeferredRun(
        ref bool runStarted,
        bool stopRequested,
        CancellationToken terminalToken)
    {
        if (stopRequested)
        {
            throw new ObjectDisposedException(nameof(DeferredCodeAltaApp));
        }

        if (runStarted)
        {
            throw new InvalidOperationException("The deferred application can only run once.");
        }

        runStarted = true;
        return CancellationTokenSource.CreateLinkedTokenSource(terminalToken);
    }

    /// <summary>
    /// Retains the original startup task once, unless disposal has stopped admission.
    /// </summary>
    /// <remarks>
    /// The mandatory factory must return a non-null task. No fallback, task replacement or retry is
    /// provided. This is serialized startup admission, not concurrent iteration/disposal support.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The startup factory is null.</exception>
    internal static bool TryStartDeferredServices<TServices>(
        bool stopRequested,
        ref Task<TServices>? startupTask,
        CancellationToken startupToken,
        Func<CancellationToken, Task<TServices>> startServices)
        where TServices : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(startServices);

        if (stopRequested)
        {
            return false;
        }

        startupTask ??= startServices(startupToken);
        return true;
    }

    /// <summary>
    /// Creates the single cached Deferred disposal operation, stopping admission before its core starts.
    /// </summary>
    /// <remarks>
    /// Validate both callbacks before invocation. The stop callback must be a nonthrowing assignment.
    /// Default execution-and-publication shares pending and terminal outcomes without retries. First
    /// access belongs to the frontend cleanup context, after the terminal run completes; it starts
    /// inline. Same-owner recursive disposal is unsupported and may throw or self-deadlock.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory callback is null.</exception>
    internal static Lazy<Task> CreateDeferredDisposal(
        Action stopStartup,
        Func<Task> disposeCore)
    {
        ArgumentNullException.ThrowIfNull(stopStartup);
        ArgumentNullException.ThrowIfNull(disposeCore);

        return new Lazy<Task>(async () =>
        {
            stopStartup();
            await disposeCore();
        });
    }

    /// <summary>
    /// Requests startup cancellation and joins/disposes the exclusive returned owner before final cleanup.
    /// </summary>
    /// <remarks>
    /// Capture arguments after stopping admission and before cancellation callbacks. An existing app
    /// exclusively owns its services; otherwise join the original startup task to actual completion
    /// and dispose its exact returned resource. An app cannot coexist with pending/failed startup in
    /// the supported caller flow. Every later stage is attempted after faults or cancellation.
    /// Only a join failure identical to the live-recorded startup failure or precisely expected
    /// startup cancellation is omitted. This is not confirmation that live presentation succeeded.
    /// Plain awaits preserve frontend cleanup context. No timeout or child-termination guarantee is
    /// provided; callbacks and composed startup may depend on work outside this bounded traversal.
    /// Update cleanup is awaited at its existing stage, joining its check before later cleanup.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    /// <exception cref="Exception">One retained failure is rethrown through EDI without replacement.</exception>
    /// <exception cref="OperationCanceledException">The sole retained failure is cancellation.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in execution order, without flattening.</exception>
    internal static Task DisposeDeferredStartupAsync<TServices>(
        IAsyncDisposable? app,
        Task<TServices>? startupTask,
        Exception? reportedStartupFailure,
        CancellationToken startupToken,
        Action cancelStartup,
        Func<ValueTask> disposeUpdate,
        Action disposePresenter,
        Action disposeStartupCancellation)
        where TServices : class, IAsyncDisposable
        => DisposeDeferredStartupAsync(app, startupTask, reportedStartupFailure, startupToken, cancelStartup,
            disposeUpdate, disposePresenter, disposeStartupCancellation, static () => { });

    internal static Task DisposeDeferredStartupAsync<TServices>(
        IAsyncDisposable? app,
        Task<TServices>? startupTask,
        Exception? reportedStartupFailure,
        CancellationToken startupToken,
        Action cancelStartup,
        Func<ValueTask> disposeUpdate,
        Action disposePresenter,
        Action disposeStartupCancellation,
        Action beginOwnedShutdown)
        where TServices : class, IAsyncDisposable
        => DisposeDeferredStartupAsync(app, startupTask, reportedStartupFailure, startupToken, cancelStartup,
            disposeUpdate, disposePresenter, disposeStartupCancellation, beginOwnedShutdown, static () => Task.CompletedTask);

    internal static Task DisposeDeferredStartupAsync<TServices>(
        IAsyncDisposable? app,
        Task<TServices>? startupTask,
        Exception? reportedStartupFailure,
        CancellationToken startupToken,
        Action cancelStartup,
        Func<ValueTask> disposeUpdate,
        Action disposePresenter,
        Action disposeStartupCancellation,
        Action beginOwnedShutdown,
        Func<Task> quiescePlugins)
        where TServices : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(cancelStartup);
        ArgumentNullException.ThrowIfNull(disposeUpdate);
        ArgumentNullException.ThrowIfNull(disposePresenter);
        ArgumentNullException.ThrowIfNull(disposeStartupCancellation);
        ArgumentNullException.ThrowIfNull(beginOwnedShutdown);
        ArgumentNullException.ThrowIfNull(quiescePlugins);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            Task? appDisposalOriginal = null;
            Task? servicesDisposalOriginal = null;
            Task? pluginDrainOriginal = null;
            var originalFailures = new List<DeferredOriginalFailure>();
            DeferredOriginalFailure CaptureOriginalFailure(Task? original, Exception awaitedFailure)
            {
                var evidence = new DeferredOriginalFailure(original, awaitedFailure, original?.Exception);
                originalFailures.Add(evidence);
                return evidence;
            }
            void ThrowIfRetained()
            {
                if (failures is not null && failures.Any(Program.StartupOwner.ContainsRetention))
                    throw new AgentDependencyRetentionException("deferred startup", "retained application dependencies", failures,
                        new DeferredRetainedDependencies(new { App = app, Startup = startupTask, AppDisposal = appDisposalOriginal, ServicesDisposal = servicesDisposalOriginal,
                            PluginDrain = pluginDrainOriginal, QuiescePlugins = quiescePlugins,
                            Cancel = cancelStartup, BeginOwnedShutdown = beginOwnedShutdown, Update = disposeUpdate,
                            Presenter = disposePresenter, Source = disposeStartupCancellation }, originalFailures.ToArray()));
            }
            // A new disposal request must not excuse an already-terminal unrequested cancellation.
            var startupWasCompleted = startupTask?.IsCompleted == true;
            var cancellationWasRequested = startupToken.IsCancellationRequested;

            try
            {
                cancelStartup();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try { beginOwnedShutdown(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            try
            {
                pluginDrainOriginal = quiescePlugins() ?? throw new InvalidOperationException("Plugin quiescence returned no original.");
                await pluginDrainOriginal;
            }
            catch (Exception failure)
            {
                var originalFaults = pluginDrainOriginal?.Exception;
                var reported = originalFaults is { InnerExceptions.Count: > 1 } ? originalFaults : failure;
                (failures ??= []).Add(new AgentDependencyRetentionException("deferred startup", "plugin quiescence", [reported],
                    new { Original = pluginDrainOriginal, AwaitedFailure = failure, OriginalFaults = originalFaults, Quiesce = quiescePlugins }));
            }
            ThrowIfRetained();

            if (app is not null)
            {
                // No pending wait precedes frontend disposal in this branch.
                try
                {
                    appDisposalOriginal = app.DisposeAsync().AsTask();
                    await appDisposalOriginal;
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(CaptureOriginalFailure(appDisposalOriginal, ex).Reported);
                }
            }
            else if (startupTask is not null)
            {
                TServices? returnedServices = null;
                try
                {
                    returnedServices = await startupTask;
                }
                catch (Exception ex)
                {
                    var evidence = CaptureOriginalFailure(startupTask, ex);
                    var requestedAtObservation = startupWasCompleted
                        ? cancellationWasRequested
                        : startupToken.IsCancellationRequested;
                    if (evidence.HasRetention ||
                        (!ReferenceEquals(ex, reportedStartupFailure) && !IsExpectedDeferredStartupCancellation(
                            startupTask, ex, startupToken, requestedAtObservation)))
                    {
                        (failures ??= []).Add(evidence.Reported);
                    }
                }

                if (returnedServices is not null)
                {
                    try
                    {
                        servicesDisposalOriginal = returnedServices.DisposeAsync().AsTask();
                        await servicesDisposalOriginal;
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(CaptureOriginalFailure(servicesDisposalOriginal, ex).Reported);
                    }
                }
            }

            ThrowIfRetained();

            try
            {
                await disposeUpdate();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                disposePresenter();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                disposeStartupCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }

    // Failure evidence preserves the actual task (or missing synchronous launch), the exception
    // selected by await, and every original fault without flattening or removing shared references.
    internal sealed record DeferredOriginalFailure(Task? Original, Exception AwaitedFailure, AggregateException? OriginalFaults)
    {
        internal Exception Reported => OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : AwaitedFailure;
        internal bool HasRetention => Program.StartupOwner.ContainsRetention(AwaitedFailure)
            || OriginalFaults is not null && Program.StartupOwner.ContainsRetention(OriginalFaults);
    }

    internal sealed record DeferredRetainedDependencies(object Ownership, IReadOnlyList<DeferredOriginalFailure> OriginalFailures);

    /// <summary>
    /// Recognizes only canceled startup tasks carrying the exact requested, cancelable startup token.
    /// </summary>
    /// <remarks>
    /// For tasks already terminal before disposal requests cancellation, the supplied observation
    /// flag is the pre-request snapshot. For pending tasks and live polling, it is the request state
    /// at observation. Faulted OCEs, default/unrelated tokens and nested rollback failures do not match.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The startup task or caught exception is null.</exception>
    internal static bool IsExpectedDeferredStartupCancellation(
        Task startupTask,
        Exception exception,
        CancellationToken startupToken,
        bool cancellationRequestedAtObservation)
    {
        ArgumentNullException.ThrowIfNull(startupTask);
        ArgumentNullException.ThrowIfNull(exception);

        return startupTask.IsCanceled
            && exception is OperationCanceledException canceled
            && startupToken.CanBeCanceled
            && cancellationRequestedAtObservation
            && startupToken.IsCancellationRequested
            && canceled.CancellationToken == startupToken;
    }

    private static string GetGlobalRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta");

    private static Padder CreateStretchHost(Visual content)
        => new(content)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

    private static Visual BuildMessage(string text)
        => new TextBlock
        {
            Wrap = true,
            Text = text,
        };

    private static Visual BuildWorkspacePlaceholder(string text)
        => new Center(BuildMessage(text))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
}
