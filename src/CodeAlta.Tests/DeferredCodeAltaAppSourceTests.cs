using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class DeferredCodeAltaAppSourceTests
{
    [TestMethod]
    public void DeferredStartup_SourceWiring_UsesLinkedOneShotAdmission()
    {
        // Named-checkout wiring evidence only. No application, startup factory or disposer runs.
        var source = ReadSource("CodeAlta.Tui/Views/DeferredCodeAltaApp.cs");
        RequireOnce(source, "private CancellationTokenSource? _startupCancellation;");

        var fields = Scope(source, "internal sealed class DeferredCodeAltaApp : IAsyncDisposable", "    public DeferredCodeAltaApp(");
        RequireOnce(fields, "private CancellationToken _startupToken;");
        RequireOnce(fields, "private bool _runStarted;");
        RequireOnce(fields, "private bool _stopRequested;");
        RequireOnce(fields, "private Task<CodeAltaOwnedServices>? _ownedServicesTask;");
        RequireOnce(fields, "private CodeAltaApp? _app;");
        RequireOnce(fields, "private Exception? _startupFailure;");

        var run = Scope(source, "    public ValueTask<TerminalInstance> RunAsync(CancellationToken cancellationToken)", "\n    }\n");
        RequireOrdered(run,
            "_startupCancellation = BeginDeferredRun(",
            "ref _runStarted, _stopRequested, cancellationToken);",
            "_startupToken = _startupCancellation.Token;",
            "return Terminal.RunAsync(",
            "_rootHost,",
            "_ => OnIteration(cancellationToken),",
            "GraphicsPresenter = _graphicsPresenter,",
            "UpdateWaitDuration = TimeSpan.FromMilliseconds(1),",
            "            cancellationToken);");

        var begin = Scope(source, "    internal static CancellationTokenSource BeginDeferredRun(", "\n    }\n");
        RequireOnce(begin, """
                internal static CancellationTokenSource BeginDeferredRun(
                    ref bool runStarted,
                    bool stopRequested,
                    CancellationToken terminalToken)
            """);
        RequireOrdered(begin,
            "if (stopRequested)",
            "throw new ObjectDisposedException(nameof(DeferredCodeAltaApp));",
            "if (runStarted)",
            "throw new InvalidOperationException(",
            "runStarted = true;",
            "return CancellationTokenSource.CreateLinkedTokenSource(terminalToken);");

        var iteration = Scope(source, "    private TerminalLoopResult OnIteration(CancellationToken cancellationToken)", "\n    }\n");
        RequireOrdered(iteration,
            "if (_stopRequested || _exitRequested)",
            "if (_app is not null)",
            "if (!_configRecoveryChecked && !EnsureConfigCanLoadBeforeStartup())",
            "if (_configRecoveryDialog is not null)",
            "if (_startupFailure is not null)",
            "if (!TryStartDeferredServices(");
        RequireOnce(Scope(iteration, "if (_stopRequested || _exitRequested)", "if (_app is not null)"), "return TerminalLoopResult.Stop;");
        RequireOnce(Scope(iteration, "if (_app is not null)", "if (!_configRecoveryChecked"), """
                        SyncUpdateNotifications();
                        return _app.Tick(cancellationToken);
            """);
        RequireOnce(iteration, """
                    if (!TryStartDeferredServices(
                            _stopRequested,
                            ref _ownedServicesTask,
                            _startupToken,
                            token => CodeAltaOwnedServices.CreateAsync(
                                token, _prestartedPluginRuntime)))
                    {
                        return TerminalLoopResult.Stop;
                    }
            """);
        // The concrete factory occurs only here, never as a seam fallback.
        RequireOnce(source, "CodeAltaOwnedServices.CreateAsync(");
        RequireOnce(iteration, "var startupTask = _ownedServicesTask!;");
        var completion = Scope(iteration, "if (!startupTask.IsCompleted)", "var ownedServices = startupTask.GetAwaiter().GetResult();");
        RequireOrdered(completion,
            "return TerminalLoopResult.Continue;",
            "if (_stopRequested)",
            "return TerminalLoopResult.Stop;",
            "        try\n");
        RequireOnce(iteration, """
                        var ownedServices = startupTask.GetAwaiter().GetResult();
                        _app = CodeAltaApp.Create(ownedServices, _updateService);
                        _app.PrepareForRun();
                        _toastHost.Content = _app.GetRoot();
            """);
        RequireOnce(iteration, """
                        if (_openProvidersAfterStartup)
                        {
                            _openProvidersAfterStartup = false;
                            _ = _app.OpenModelProvidersAsync();
                        }
            """);
        RequireOnce(iteration, """
                    catch (OperationCanceledException ex) when (
                        IsExpectedDeferredStartupCancellation(
                            startupTask, ex, _startupToken,
                            _startupToken.IsCancellationRequested))
                    {
                        return TerminalLoopResult.Continue;
                    }
            """);
        RequireOnce(iteration, """
                    catch (Exception ex)
                    {
                        _startupFailure = ex;
                        _sidebarHost.Content = BuildMessage(SR.T("Startup failed."));
                        _workspaceHost.Content = BuildWorkspacePlaceholder(SR.T("CodeAlta startup failed: {0}", ex.Message));
                        _commandBarHost.Content = new Placeholder { IsVisible = false };
                        return TerminalLoopResult.Continue;
                    }
            """);
        RequireOnce(iteration, "        SyncUpdateNotifications();\n        return _app.Tick(cancellationToken);");
        Reject(iteration, "await ", "DisposeAsync(");

        var start = Scope(source, "    internal static bool TryStartDeferredServices<TServices>(", "\n    }\n");
        RequireOnce(start, """
                internal static bool TryStartDeferredServices<TServices>(
                    bool stopRequested,
                    ref Task<TServices>? startupTask,
                    CancellationToken startupToken,
                    Func<CancellationToken, Task<TServices>> startServices)
                    where TServices : class, IAsyncDisposable
            """);
        RequireOrdered(start,
            "ArgumentNullException.ThrowIfNull(startServices);",
            "if (stopRequested)",
            "return false;",
            "startupTask ??= startServices(startupToken);",
            "return true;");
        Reject(source, "_ownedServicesTask = null;", "startupTask = null;", "_runStarted = false;", "_stopRequested = false;");
        var recovery = Scope(source, "    private bool EnsureConfigCanLoadBeforeStartup()", "\n    }\n");
        var save = Scope(recovery, "saveAndContinue: () =>", "exit: () => _exitRequested = true");
        RequireOrdered(save,
            "if (_stopRequested || _ownedServicesTask is not null)",
            "return;",
            "_configRecoveryDialog = null;",
            "_startupFailure = null;",
            "_openProvidersAfterStartup = _configRecovery.CreatedDefault;");

        // Preserve Program's serialized loop-then-dispose and borrowed-plugin owner.
        // This does not fix early logging/plugin admission or Program's CTS lifetime.
        var program = ReadSource("CodeAlta.Tui/Program.cs");
        var programRun = Scope(program, "    internal static async ValueTask<int> RunAsync(", "\n    }\n");
        RequireOrdered(programRun,
            "using var singleInstanceGuard = CodeAltaSingleInstanceGuard.Acquire();",
            "var cancellationTokenSource = new CancellationTokenSource();",
            "await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);",
            "Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);",
            "await app.RunAsync(cancellationTokenSource.Token);",
            "PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);",
            "return 0;");
        var commandLine = Scope(program, "    var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);", "\ncatch (CodeAltaAlreadyRunningException ex)");
        RequireOrdered(commandLine,
            "var pluginCommandLineContributions = Program.GetPluginCommandLineContributions(commandLinePluginRuntime);",
            "options => Program.RunAsync(options, mainThreadId, commandLinePluginRuntime),",
            "return command.RunAsync(args).AsTask().GetAwaiter().GetResult();",
            "finally",
            "if (commandLinePluginRuntime is not null)",
            "commandLinePluginRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();");
    }

    [TestMethod]
    public void DeferredDisposal_SourceWiring_UsesCachedJoinAndExclusiveOwner()
    {
        // Source-only forwarding evidence, not concrete startup/shutdown or UI qualification.
        var source = ReadSource("CodeAlta.Tui/Views/DeferredCodeAltaApp.cs");
        RequireOnce(source, "private readonly Lazy<Task> _disposeTask;");

        var constructor = Scope(source, "    public DeferredCodeAltaApp(", "\n    }\n");
        RequireOnce(constructor, """
                    _disposeTask = CreateDeferredDisposal(
                        stopStartup: () => _stopRequested = true,
                        disposeCore: DisposeCoreAsync);
            """);
        RequireOrdered(constructor, "_rootHost.RegisterClipboardScreenshotCommand();", "_disposeTask = CreateDeferredDisposal(", "_updateService.Start();");
        RequireOnce(source, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        Reject(source, "public async ValueTask DisposeAsync()", "IsValueCreated", "ConfigureAwait(false)", ".WaitAsync(", "Task.Run(");

        var factory = Scope(source, "    internal static Lazy<Task> CreateDeferredDisposal(", "\n    }\n");
        RequireOnce(factory, """
                internal static Lazy<Task> CreateDeferredDisposal(
                    Action stopStartup,
                    Func<Task> disposeCore)
            """);
        RequireOrdered(factory,
            "ArgumentNullException.ThrowIfNull(stopStartup);",
            "ArgumentNullException.ThrowIfNull(disposeCore);",
            "return new Lazy<Task>(async () =>",
            "stopStartup();",
            "await disposeCore();");

        var snapshot = Scope(source, "    private Task DisposeCoreAsync()", "\n    }\n");
        RequireOrdered(snapshot,
            "var app = _app;",
            "var startupTask = _ownedServicesTask;",
            "var reportedStartupFailure = _startupFailure;",
            "var startupToken = _startupToken;",
            "var startupCancellation = _startupCancellation;",
            "return DisposeDeferredStartupAsync(");
        RequireOnce(snapshot, """
                    return DisposeDeferredStartupAsync(
                        app,
                        startupTask,
                        reportedStartupFailure,
                        startupToken,
                        cancelStartup: () => startupCancellation?.Cancel(),
                        disposeUpdate: _updateService.DisposeAsync,
                        disposePresenter: _graphicsPresenter.Dispose,
                        disposeStartupCancellation: () => startupCancellation?.Dispose());
            """);
        Reject(snapshot, "await ", "startupCancellation.Token", "_app.DisposeAsync(", "_ownedServicesTask.Result");

        var core = Scope(source, "    internal static Task DisposeDeferredStartupAsync<TServices>(", "\n    }\n");
        RequireOnce(core, """
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
            """);
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(cancelStartup);",
            "ArgumentNullException.ThrowIfNull(disposeUpdate);",
            "ArgumentNullException.ThrowIfNull(disposePresenter);",
            "ArgumentNullException.ThrowIfNull(disposeStartupCancellation);",
            "return CoreAsync();",
            "async Task CoreAsync()");
        // Check that the production-used core contains ownership decisions, not an observation-only seam.
        RequireOrdered(core,
            "var startupWasCompleted = startupTask?.IsCompleted == true;",
            "var cancellationWasRequested = startupToken.IsCancellationRequested;",
            "cancelStartup();",
            "if (app is not null)",
            "await app.DisposeAsync();",
            "else if (startupTask is not null)",
            "returnedServices = await startupTask;",
            "ReferenceEquals(ex, reportedStartupFailure)",
            "IsExpectedDeferredStartupCancellation(",
            "startupTask, ex, startupToken, requestedAtObservation)",
            "await returnedServices.DisposeAsync();",
            "await disposeUpdate();",
            "disposePresenter();",
            "disposeStartupCancellation();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        var appBranch = Scope(core, "if (app is not null)", "else if (startupTask is not null)");
        Reject(appBranch, "await startupTask", "returnedServices", "reportedStartupFailure");
        Reject(core, "CodeAltaOwnedServices", "CodeAltaApp.Create(", "GetAwaiter().GetResult()", ".Result", ".Flatten(");

        var predicate = Scope(source, "    internal static bool IsExpectedDeferredStartupCancellation(", "\n    }\n");
        RequireOnce(predicate, """
                internal static bool IsExpectedDeferredStartupCancellation(
                    Task startupTask,
                    Exception exception,
                    CancellationToken startupToken,
                    bool cancellationRequestedAtObservation)
            """);
        RequireOrdered(predicate,
            "ArgumentNullException.ThrowIfNull(startupTask);",
            "ArgumentNullException.ThrowIfNull(exception);",
            "startupTask.IsCanceled",
            "exception is OperationCanceledException canceled",
            "startupToken.CanBeCanceled",
            "&& cancellationRequestedAtObservation",
            "startupToken.IsCancellationRequested",
            "canceled.CancellationToken == startupToken");

        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var createApp = Scope(app, "    internal static CodeAltaApp Create(", "\n    }\n");
        RequireOrdered(createApp, "ArgumentNullException.ThrowIfNull(ownedServices);", "return new(", "ownedServices.ProviderDescriptors,");
        RequireOnce(createApp, "            ownedServices,\n            updateService);");
        var appConstructor = Scope(app, "    private CodeAltaApp(IReadOnlyList<ModelProviderDescriptor> providerDescriptors,", "\n    }\n");
        RequireOrdered(appConstructor, "_ownedServices = ownedServices;", "_frontendHost = new ShellFrontendHost(this);", "var composition = CodeAltaFrontendComposition.Create(");
        RequireOnce(app, "public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        RequireOnce(app, "IAsyncDisposable? IShellFrontendHostLifecycle.OwnedServices => _ownedServices;");
        var frontendCleanup = Scope(app, "    async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()", "\n    }\n");
        RequireOrdered(frontendCleanup,
            "_projectionCoordinator.Dispose();",
            "_reminderUiCoordinator.Dispose();",
            "await _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false);",
            "await _fileEditorWorkspaceCoordinator.DisposeAsync();",
            "await _runtimeEventPump.DisposeAsync();",
            "await _shellController.DisposeAsync();",
            "await _promptDraftUiCoordinator.DisposeAsync();");

        var shell = ReadSource("CodeAlta.Tui/App/ShellFrontendHost.cs");
        var shellCleanup = Scope(shell, "    public async ValueTask DisposeAsync()", "\n    }\n");
        RequireOrdered(shellCleanup,
            "await _lifecycle.DisposeFrontendAsync();",
            "frontendFailure = ex;",
            "if (_lifecycle.OwnedServices is { } ownedServices)",
            "await ownedServices.DisposeAsync();",
            "catch (Exception ex) when (frontendFailure is not null)",
            "throw new AggregateException(frontendFailure, ex);",
            "ExceptionDispatchInfo.Throw(frontendFailure);");

        var outer = ReadSource("CodeAlta.Tui/App/CodeAltaOwnedServices.cs");
        RequireOnce(outer, "private readonly CodeAltaHost _host;");
        RequireOnce(outer, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        var outerConstructor = Scope(outer, "    private CodeAltaOwnedServices(", "\n    }\n");
        RequireOrdered(outerConstructor, "_host = host;", "_disposeTask = CreateOwnedServicesDisposal(");
        RequireOnce(outerConstructor, """
                    _disposeTask = CreateOwnedServicesDisposal(
                        _host.DisposeAsync,
                        _modelsDevCatalogService.DisposeAsync,
                        LogManager.Shutdown,
                        ownsLogging);
            """);
        var createOuter = Scope(outer, "    public static async Task<CodeAltaOwnedServices> CreateAsync(", "\n    }\n");
        RequireOrdered(createOuter,
            "ModelsDevCatalogService? modelsDevCatalogService = null;",
            "CodeAltaHost? sharedHost = null;",
            "sharedHost = await CodeAltaHost.CreateAsync(",
            "PrestartedPluginRuntime = prestartedPluginRuntime,",
            "                    cancellationToken)",
            "return new CodeAltaOwnedServices(",
            "await RollbackOwnedServicesCreationAsync(");
        RequireOnce(createOuter, """
                            creationFailure,
                            () => sharedHost?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => modelsDevCatalogService?.DisposeAsync() ?? ValueTask.CompletedTask,
                            LogManager.Shutdown,
                            ownsLogging).ConfigureAwait(false);
            """);
        Reject(outer, "RuntimeService.DisposeAsync(", "AgentHub.DisposeAsync(", "_modelProviderRegistry.DisposeAsync(", "PluginRuntime.DisposeAsync(");

        var host = ReadSource("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        RequireOnce(host, "private readonly Lazy<Task> _disposeTask;");
        RequireOnce(host, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        var hostConstructor = Scope(host, "    private CodeAltaHost(", "\n    }\n");
        RequireOnce(hostConstructor, """
                    _disposeTask = CreateHostDisposal(
                        RuntimeService.DisposeAsync,
                        AgentHub.DisposeAsync,
                        ModelProviderRegistry.DisposeAsync,
                        PluginRuntime.DisposeAsync,
                        LogManager.Shutdown,
                        ownsPluginRuntime,
                        ownsLogging);
            """);
        var createHost = Scope(host, "    public static async Task<CodeAltaHost> CreateAsync(", "\n    }\n");
        RequireOrdered(createHost,
            "pluginRuntime = options.PrestartedPluginRuntime ?? new PluginRuntimeManager();",
            "ownsPluginRuntime = options.PrestartedPluginRuntime is null;",
            "if (options.StartPlugins && options.PrestartedPluginRuntime is null)",
            "return new CodeAltaHost(",
            "await RollbackHostCreationAsync(");
        RequireOnce(createHost, """
                            creationFailure,
                            () => runtimeService?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => agentHub?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => modelProviderRegistry?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => pluginRuntime?.DisposeAsync() ?? ValueTask.CompletedTask,
                            LogManager.Shutdown,
                            ownsPluginRuntime,
                            ownsLogging).ConfigureAwait(false);
            """);

        // The same Deferred stage now awaits updater-owned cancellation, join and source release.
        // This remains named-source evidence, not network/transport or complete shutdown qualification.
        var update = ReadSource("CodeAlta.Tui/Views/CodeAltaUpdateService.cs");
        RequireOnce(update, "internal sealed class CodeAltaUpdateService : IAsyncDisposable");
        RequireOnce(update, "private readonly Lazy<Task> _disposeTask;");
        RequireOnce(update, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        var updateConstructor = Scope(update, "    public CodeAltaUpdateService()", "\n    }\n");
        RequireOnce(updateConstructor, """
                    _disposeTask = CreateUpdateDisposal(
                        stopCheck: () => _stopRequested = true,
                        disposeCore: DisposeCoreAsync);
            """);
        var updateFactory = Scope(update, "    internal static Lazy<Task> CreateUpdateDisposal(", "\n    }\n");
        RequireOrdered(updateFactory,
            "ArgumentNullException.ThrowIfNull(stopCheck);",
            "ArgumentNullException.ThrowIfNull(disposeCore);",
            "return new Lazy<Task>(async () =>",
            "stopCheck();",
            "await disposeCore();");
        var updateSnapshot = Scope(update, "    private Task DisposeCoreAsync()", "\n    }\n");
        RequireOrdered(updateSnapshot,
            "var checkTask = _checkTask;",
            "var cancellation = _cancellationTokenSource;",
            "return DisposeUpdateCheckAsync(");
        RequireOnce(updateSnapshot, """
                    return DisposeUpdateCheckAsync(
                        checkTask,
                        cancelCheck: () => cancellation?.Cancel(),
                        disposeCancellation: () => cancellation?.Dispose());
            """);
        Reject(updateSnapshot, "await ", ".Token", "_cancellationTokenSource?.Cancel()", "_cancellationTokenSource?.Dispose()");
        var updateCore = Scope(update, "    internal static Task DisposeUpdateCheckAsync(", "\n    }\n");
        RequireOnce(updateCore, """
                internal static Task DisposeUpdateCheckAsync(
                    Task? checkTask,
                    Action cancelCheck,
                    Action disposeCancellation)
            """);
        RequireOrdered(updateCore,
            "ArgumentNullException.ThrowIfNull(cancelCheck);",
            "ArgumentNullException.ThrowIfNull(disposeCancellation);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "cancelCheck();",
            "if (checkTask is not null)",
            "await checkTask;",
            "disposeCancellation();",
            "ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        RequireOnce(updateCore, """
                        if (checkTask is not null)
                        {
                            try
                            {
                                await checkTask;
                            }
                            catch (Exception ex)
                            {
                                (failures ??= []).Add(ex);
                            }
                        }
            """);
        Reject(updateCore, "Task.Run(", "when (", "IsCanceled", "IsCompleted", "ReferenceEquals(");
        Reject(update,
            ": IDisposable", "public void Dispose()", "IsValueCreated", "ConfigureAwait(false)",
            ".WaitAsync(", ".Wait(", "GetAwaiter().GetResult()", ".Result", "Task.WhenAny(", ".Flatten(",
            "_checkTask = null;", "checkTask = null;", "_cancellationTokenSource = null;", "cancellationTokenSource = null;",
            "_startRequested = false;", "startRequested = false;", "_stopRequested = false;", "stopRequested = false;");
    }

    // These seven literal call-site paths are the complete source-content read inventory.
    // No upward discovery, profile access, localization or logging initialization is performed.
    // Named source reads and existing assembly-level logging are not zero I/O.
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
