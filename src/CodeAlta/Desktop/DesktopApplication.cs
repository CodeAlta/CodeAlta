using CodeAlta.Desktop.Rpc;
using System.Collections.Frozen;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using NeoAstra;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

internal sealed class DesktopApplication(DesktopLaunchOptions options, DesktopLogCapture? logCapture)
{
    private CodeAltaSingleInstanceGuard? _lease;
    private Task<CodeAltaHost>? _hostCreation;
    private Task? _hostDisposal;
    private Task? _closeFlow;
    private bool _nativeConfirmed;
    private readonly List<Task> _diagnosticWaits = [];
    internal int ExitCode { get; private set; } = 1;

    internal static int Run(DesktopLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // A profile in use is a running CodeAlta, possibly with its window closed. This start asks it to show
        // its window and ends there, before touching anything the running one holds (its log file, say).
        if (options.Owned is not null && ProfileInUse(options)) return ActivateRunningInstance(options);
        if (options.ExitRunning) return 0; // None is running: there is nothing to exit.
        // A start from a terminal gives the prompt back: the application runs in a process of its own.
        if (DesktopTerminalStart.TryHandOver(options, Console.Error) is { } handedOver) return handedOver;
        return RunWithCapture(options, RunCore);
    }

    private static string LockPath(DesktopLaunchOptions options) => Path.Combine(options.StateRoot ?? options.CatalogRoot!, "alta.lock");

    private static bool ProfileInUse(DesktopLaunchOptions options)
    {
        try
        {
            using var probe = CodeAltaSingleInstanceGuard.Acquire(LockPath(options));
            return false;
        }
        catch (CodeAltaAlreadyRunningException) { return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; } // The start itself reports it.
    }

    // Initialize before any host/provider acquisition. A failed/unconfirmed lifetime can still
    // own callbacks, so leave its logger available until process exit rather than breaking them.
    internal static int Run(DesktopLaunchOptions options, Func<DesktopLaunchOptions, int> run)
        => RunWithCapture(options, (launch, _) => run(launch));

    private static int RunWithCapture(DesktopLaunchOptions options, Func<DesktopLaunchOptions, DesktopLogCapture?, int> run)
    {
        var capture = DesktopLogging.Initialize(options.DataRoot);
        var result = run(options, capture);
        if (capture is not null && result == 0) LogManager.Shutdown();
        return result;
    }

    private static int RunCore(DesktopLaunchOptions options, DesktopLogCapture? capture)
    {
        if (options.Owned is not null) return RunOwned(options, capture);
        Directory.CreateDirectory(options.DataRoot);
        var desktop = new DesktopApplication(options, capture);
        var result = NeoApplication.Run(new NeoApplicationOptions
        {
            ApplicationName = "CodeAlta",
            // Keep the dispatcher alive through asynchronous browser/environment disposal.
            ShutdownMode = NeoApplicationShutdownMode.Explicit,
        }, desktop.RunAsync);
        return result == 0 ? desktop.ExitCode : result;
    }

    /// <summary>The start-up screen: the document the view shows while the host starts.</summary>
    internal static Uri StartupDocument { get; } = new("app://codealta/splash.html");

    /// <summary>The application's page.</summary>
    internal static Uri ApplicationDocument { get; } = new("app://codealta/index.html");

    internal static bool IsApplicationDocument(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "app" && uri.Host == "codealta" && uri.Port == -1 &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath is "/index.html" or "/splash.html" && string.IsNullOrEmpty(uri.Query);

    /// <summary>
    /// The identity under which running instances are found: one per profile, so the developer instance and
    /// an instance on explicit roots are never mistaken for the normal one.
    /// </summary>
    internal static string InstanceId(DesktopLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(options.StateRoot ?? options.CatalogRoot ?? options.DataRoot);
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return "org.codealta.desktop." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..24];
    }

    // The running instance listens for the next ones. Without this endpoint the application still runs; a
    // second start then only finds the profile in use.
    private static async ValueTask<IAsyncDisposable?> AcquireInstanceAsync(NeoApplication application, DesktopLaunchOptions options)
    {
        try
        {
            return await NeoSingleInstance.AcquireAsync(application, new NeoSingleInstanceOptions { ApplicationId = InstanceId(options) },
                new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"Second starts cannot reach this instance: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// The profile is in use by a running CodeAlta, possibly with its window closed: this start asks that one
    /// to show its window, then ends.
    /// </summary>
    private static int ActivateRunningInstance(DesktopLaunchOptions options)
    {
        var routed = false;
        var result = NeoApplication.Run(new NeoApplicationOptions { ApplicationName = "CodeAlta", ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
        {
            try
            {
                // Routing happens inside the acquisition: this process is not the first one.
                await using var instance = await NeoSingleInstance.AcquireAsync(application, new NeoSingleInstanceOptions { ApplicationId = InstanceId(options) },
                    new NeoLaunchEvent(NeoLaunchReason.SecondInstance, options.ExitRunning ? [DesktopCommandLine.ExitOption] : null));
                routed = !instance.IsPrimary;
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Console.Error.WriteLine($"The running CodeAlta could not be reached: {exception.Message}");
            }
            finally { application.ForceShutdown(); }
        });
        if (!routed) Console.Error.WriteLine("CodeAlta is already running with this profile.");
        return result == 0 && routed ? 0 : 1;
    }

    private static int RunOwned(DesktopLaunchOptions options, DesktopLogCapture? capture)
    {
        var desktop = new DesktopApplication(options, capture);
        try
        {
            // Only the normal instance is the installed application; the developer instance and one on explicit
            // roots stay apart from it in the taskbar and leave the desktop's entry alone.
            var installed = !options.Developer && options.Owned!.Home is null;
            DesktopIntegration.IdentifyProcess(developer: !installed);
            try { desktop._lease = CodeAltaSingleInstanceGuard.Acquire(LockPath(options)); }
            catch (CodeAltaAlreadyRunningException) { return ActivateRunningInstance(options); } // Another start won the profile meanwhile.

            Directory.CreateDirectory(options.DataRoot);
            var result = NeoApplication.Run(new NeoApplicationOptions
            {
                ApplicationName = "CodeAlta", ShutdownMode = NeoApplicationShutdownMode.Explicit,
            }, desktop.RunOwnedAsync);
            // A native loop ending externally is NOT evidence that host work terminated.
            if (CanReleaseOwnedLease(desktop._hostCreation, desktop._hostDisposal, desktop._nativeConfirmed)) desktop._lease.Dispose();
            else Console.Error.WriteLine("Owned shutdown unconfirmed; the application did not release its lease.");
            return result == 0 ? desktop.ExitCode : result;
        }
        catch (Exception failure)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Error(failure, "Owned desktop startup or native lifetime failed");
            Console.Error.WriteLine("Owned desktop startup or native lifetime failed; termination is not confirmed.");
            return 1;
        }
        finally { GC.KeepAlive(desktop); } // Strong owner/lease lifetime across the synchronous native loop.
    }

    /// <summary>
    /// Whether no model provider is enabled, which is how a new profile starts: the page then opens the
    /// provider settings, as the terminal application does. A configuration that cannot be read asks nothing.
    /// </summary>
    internal static bool NeedsProviderSetup(CodeAltaConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try { return store.LoadGlobal().Providers?.Values.Any(static definition => definition.Enabled != false) != true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool CanReleaseOwnedLease(Task? creation, Task? disposal, bool nativeConfirmed)
        => nativeConfirmed && ((creation is null && disposal is null) ||
            (creation?.IsCompletedSuccessfully == true && disposal?.IsCompletedSuccessfully == true));

    private async Task AwaitOwnedAsync(Task task, NeoWindow window)
    {
        var diagnostic = task.WaitAsync(TimeSpan.FromSeconds(5));
        _diagnosticWaits.Add(diagnostic);
        try { await diagnostic; }
        catch (TimeoutException) { window.Title = "CodeAlta — owned work pending; lease retained"; }
        await task; // The timeout above never substitutes for joining the actual work.
    }

    private async ValueTask RunOwnedAsync(NeoApplication application)
    {
        var roots = options.Owned!;
        var closeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspacePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowClose = false;
        var shutdownUnconfirmed = false;
        SessionOperationsService? operations = null;
        SessionAsksService? asks = null;
        ReminderService? reminders = null;
        DesktopChangesView? changesView = null;
        DesktopEditorView? editorView = null;
        GitIssuesService? gitIssues = null;
        AppUpdateService? appUpdate = null;
        ModelCatalogService? providers = null;
        ProviderLoginService? providerLogin = null;
        McpServersService? mcpServers = null;
        PluginUiService? pluginCommands = null;
        WorkspaceService? workspace = null;
        NeoWindow? window = null;
        IAsyncDisposable? environmentLifetime = null, rpcLifetime = null, viewLifetime = null, bindingLifetime = null, chromeLifetime = null, instanceLifetime = null;
        var bodyFailed = false;
        try
        {
            // Before anything is set up: the page's files, which Windows cannot read from too deep a folder.
            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            DesktopAssetPaths.EnsureUsable(assets, manifest);
            var appearance = DesktopAppearance.Load(options.DataRoot);
            appearance.ApplyToBrowser();
            window = application.CreateWindow(DesktopWindowChrome.WindowOptions(options.Developer, appearance));
            application.MainWindow = window;
            window.Closed += (_, _) => closed.TrySetResult();
            var shutdownWindow = window;
            // The application's shutdown starts once, from the window being closed without a tray to stay in,
            // from Exit in the tray or in the page, or from the end of the user's session.
            var shell = new DesktopShell(window, application.Dispatcher, options.DataRoot, () =>
            {
                operations?.CloseAdmission();
                asks?.CloseAdmission();
                reminders?.CloseAdmission();
                providers?.CloseAdmission();
                closeRequested.TrySetResult();
                application.Dispatcher.Post(() => { if (!shutdownUnconfirmed && !shutdownWindow.IsClosed) shutdownWindow.Title = "CodeAlta — shutdown pending; lease retained"; });
            });
            window.CloseRequested += request =>
            {
                // Closing the window hides it while the application stays in the tray; otherwise it exits,
                // after a question when sessions are running.
                if (!allowClose) shell.Close(request);
                return ValueTask.CompletedTask; // Never await host cleanup inside the native deadline.
            };
            // The installed tool becomes an application of this desktop; the page says so the first time.
            if (!options.Developer && roots.Home is null)
                _ = Task.Run(() => { if (DesktopIntegration.Ensure(options.DataRoot, DesktopCommandLine.Version)) shell.NotifyEntryAdded(); });
            // Starting CodeAlta again, or selecting it in the Dock, brings back the window of the running one.
            application.LaunchReceived += launch =>
            {
                if (launch.Reason == NeoLaunchReason.SecondInstance && launch.Arguments.Contains(DesktopCommandLine.ExitOption)) shell.RequestUserExit();
                else if (launch.Reason is NeoLaunchReason.Activated or NeoLaunchReason.SecondInstance) shell.Show();
                return ValueTask.CompletedTask;
            };
            // The window is shown once its view has the start-up screen (see below): a window without a view
            // is a white rectangle, whatever the theme.
            var startupClock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string step) => LogManager.GetLogger("CodeAlta.Desktop").Info($"Startup: {step} at {startupClock.ElapsedMilliseconds} ms");
            var startedWindow = window;
            var catalog = new CatalogOptions { GlobalRoot = options.CatalogRoot!, StateRoot = options.StateRoot ?? options.CatalogRoot! };
            // Plugins read the user's profile (the MCP servers and their sign-ins, the provider CLIs): they
            // run in the normal launch and stay off when the roots are explicit.
            var pluginAlta = roots.Home is null ? new PluginAltaServiceBridge() : null;
            var startupStatus = new DesktopStartupStatus();
            var pluginUi = new DesktopPluginUi();
            // A configuration file that cannot be loaded is repaired in the window before anything reads it: the
            // host would fail on it. A missing file is created with the defaults.
            var configRecovery = new ConfigRecoveryService(options.CatalogRoot!, new TextFileCodec());
            configRecovery.Reload();
            // Off the window's thread: the host reads the catalog and the configuration and starts the plugins
            // while the window creates its view and shows the start-up screen.
            void StartHost()
            {
            _hostCreation = Task.Run(() => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = options.CatalogRoot, StateRoot = options.StateRoot, CurrentProjectPath = roots.Project,
                DiscoveryScope = roots.Home is null || roots.Instructions is null
                    ? null
                    : new SessionDiscoveryScope(roots.Home, roots.Instructions),
                BuiltInSkillRoot = roots.Builtin,
                OwnedCommandReceiptCapacity = 256, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                ReviewOwnedCommandPermissions = options.ReviewOwnedCommandPermissions,
                AutoApproveOwnedPermissions = !options.ReviewOwnedCommandPermissions,
                EnableOwnedAsks = true,
                EnableOwnedUserInput = options.EnableOwnedUserInput,
                // Source plugins are the same build as in the terminal application; the start-up screen
                // names the ones being built.
                StartPlugins = pluginAlta is not null, OwnsLogging = false, IsHeadless = false, HasInteractiveUi = true,
                PluginFrontend = PluginFrontends.Desktop, PluginAuthoringProfile = PluginAuthoringProfile.Terminal,
                PluginStartupFeedback = new DesktopPluginStartupFeedback(startupStatus),
                PluginBuiltIns = DesktopPlugins.BuiltIns, PluginSafeMode = DesktopPlugins.SafeMode,
                PluginServices = pluginAlta is null ? null : new DesktopPluginServices(pluginAlta, pluginUi),
                ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!),
            }, CancellationToken.None));
            // The retained application flow reacts even while a native acquisition is awaiting.
            // No native callback awaits this work; a close during host creation waits its actual result.
            _closeFlow = CloseOwnedHostWhenRequestedAsync(closeRequested.Task, _hostCreation, async () =>
            {
                await workspacePrepared.Task;
                if (workspace is not null) await Task.WhenAll(workspace.CloseImportsAsync(), workspace.CloseSessionsAsync());
                if (reminders is not null) await reminders.DisposeAsync();
                if (providers is not null) await providers.DrainAsync();
                if (providerLogin is not null) await providerLogin.CloseAsync(); // A running sign-in is canceled and joined.
                if (mcpServers is not null) await mcpServers.CloseAsync(); // So is a running MCP authorization.
                if (pluginCommands is not null) await pluginCommands.CloseAsync(); // Plugin commands still waiting in a dialog end.
            });
            }
            if (configRecovery.IsReady) StartHost();
            // The window's own parts do not wait for the host: the view exists and shows the start-up screen
            // (the logo on the window's theme) as soon as the browser is ready.
            var creatingEnvironment = application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new DesktopStartupResources(new NeoManifestResourceProvider(assets, manifest), startupStatus))],
            });
            var environment = await creatingEnvironment;
            Mark("environment created");
            environmentLifetime = environment;
            var chrome = await DesktopWindowChrome.StartAsync(application, options.DataRoot);
            chromeLifetime = chrome;
            await chrome.ApplyWindowIconAsync(window);
            shell.Dialogs = chrome.Services.Dialogs;
            await shell.StartTrayAsync(chrome.Services, options.Developer);
            await shell.StartApplicationMenuAsync(chrome.Services, options.Developer);
            instanceLifetime = await AcquireInstanceAsync(application, options);
            var creatingView = environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
            var view = await creatingView;
            Mark("view created");
            viewLifetime = view;
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            // The start-up screen first, then the window: it appears with the logo on its theme, never empty.
            // The browser draws into a shown window only, so the window is shown out of sight until the
            // document is there and has had a moment to be drawn. A view that does not report the document
            // does not keep the window out of sight.
            var startupShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => startupShown.TrySetResult();
            var cloaked = DesktopWindowReveal.ShowCloaked(window);
            var starting = view.NavigateAsync(StartupDocument);
            await starting;
            await Task.WhenAny(startupShown.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (cloaked)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80));
                DesktopWindowReveal.Reveal(window);
            }
            Mark("window shown with the start-up screen");
            // This method runs for the whole life of the application: say now that it is ready, or the launches
            // routed to it (a second start, the Dock) would wait for it to return.
            application.NotifyReady();
            // So does the start that waits in a terminal for this window.
            DesktopTerminalStart.NotifyShown(options.StartToken);
            // The page's theme: kept for the next start, and the window controls follow it now.
            void RememberAppearance(DesktopAppearance remembered)
            {
                remembered.Save(options.DataRoot);
                application.Dispatcher.Post(() => { if (!startedWindow.IsClosed) startedWindow.TitleBar = DesktopWindowChrome.TitleBar(remembered); });
            }
            if (_hostCreation is null)
            {
                // No host yet: the page gets the configuration editor alone. Saving a valid file starts the
                // application as if it had been valid from the start; leaving closes the window.
                Mark("configuration recovery");
                var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var recoveryBuilder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true }));
                chrome.AddHandlers(recoveryBuilder);
                recoveryBuilder.AddBootService(new BootService { ConfigRecovery = true, Developer = options.Developer, RememberAppearance = RememberAppearance });
                recoveryBuilder.AddStartupConfigService(new StartupConfigService(configRecovery)
                {
                    Continue = () => repaired.TrySetResult(), Exit = () => shell.RequestExit(confirmed: true),
                });
                recoveryBuilder.AddDesktopShellService(new DesktopShellService(shell));
                recoveryBuilder.AddColorSchemesService(new ColorSchemesService(options.CatalogRoot!));
                await using (var recoveryRpc = recoveryBuilder.Build())
                {
                    await using (NeoRpcViewBinding.Bind(recoveryRpc, view))
                    {
                        var recovering = view.NavigateAsync(ApplicationDocument);
                        await recovering;
                        await Task.WhenAny(repaired.Task, closeRequested.Task);
                        // Back to the start-up screen while the host starts: the editor is gone before its bridge is.
                        if (!closeRequested.Task.IsCompleted)
                        {
                            var restarting = view.NavigateAsync(StartupDocument);
                            await restarting;
                        }
                    }
                }
                if (!closeRequested.Task.IsCompleted) StartHost();
            }
            if (_hostCreation is not null)
            {
            await AwaitOwnedAsync(_hostCreation, window);
            var host = await _hostCreation;
            Mark("host created");
            DesktopPlugins.LogStartupDiagnostics(host.PluginRuntime);
            if (DesktopPlugins.DescribeStartupFailures(host.PluginRuntime.Diagnostics) is { } pluginFailures) pluginUi.NotifyProblem(pluginFailures);
            shell.RunningSessions = host.RuntimeService.CountActiveRuns;
            shell.HasWorkspace = true;
            if (!closeRequested.Task.IsCompleted)
            {
                var epoch = Guid.NewGuid().ToString("D");
                workspace = new WorkspaceService(host, epoch);
                operations = new SessionOperationsService(host.Commands, epoch);
                asks = new SessionAsksService(host.Commands.Asks, epoch);
                reminders = new ReminderService(host.WorkspaceReads, host.Commands, epoch);
                changesView = new DesktopChangesView();
                editorView = new DesktopEditorView();
                DesktopAltaTools.Attach(host, reminders.Reminders, pluginAlta, changesView, editorView);
                workspacePrepared.TrySetResult();
                {
                    // Leave room for ordinary pasted images and their base64/JSON overhead.
                    // Owned-only host-wide inbound UTF-8 framing cap, not a per-image/response limit.
                    var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions
                    {
                        ContractHash = NeoRpcGeneratedContract.Hash, Release = true, MaximumFrameBytes = 128 * 1024 * 1024,
                        MaximumChannelsPerSession = 34, MaximumUnacknowledgedChannelItems = 2,
                        // Up to 32 open session panes, plus workspace/control observations.
                        MaximumConcurrentInvocationsPerSession = 128,
                        RequestRatePerSecond = 512, RequestRateBurst = 1024,
                        // NeoAstra 0.2 retains completed IDs for the document lifetime, not just
                        // concurrent calls. Background observations exhaust its 4096 default.
                        MaximumRetainedRequestIds = 1_000_000,
                        DiagnosticSink = new DesktopRpcDiagnostics(),
                    }));
                    chrome.AddHandlers(builder);
                    var configStore = new CodeAltaConfigStore(catalog);
                    builder.AddBootService(new BootService(epoch, options.ReviewOwnedCommandPermissions, options.EnableOwnedUserInput, options.Developer)
                    {
                        RememberAppearance = RememberAppearance, ProviderSetup = NeedsProviderSetup(configStore),
                    });
                    builder.AddDesktopShellService(new DesktopShellService(shell));
                    builder.AddColorSchemesService(new ColorSchemesService(options.CatalogRoot!));
                    // As the terminal application does: one look at nuget.org for a newer version. An instance on
                    // explicit roots is automation and stays off the network.
                    // Only an installed tool can replace itself: a helper waits for this process to end, runs
                    // the update and starts CodeAlta again.
                    var updateLauncher = options.Developer ? null : DesktopIntegration.InstalledLauncher(AppContext.BaseDirectory, OperatingSystem.IsWindows());
                    var updateDotnet = DesktopUpdateInstaller.DotnetPath();
                    var selfUpdate = updateLauncher is not null && updateDotnet is not null && File.Exists(updateLauncher);
                    appUpdate = roots.Home is not null ? new AppUpdateService() : new AppUpdateService(DesktopCommandLine.Version)
                    {
                        Installed = DesktopUpdateInstaller.ConsumeResult(options.DataRoot),
                        Install = !selfUpdate ? null : prerelease =>
                        {
                            if (!DesktopUpdateInstaller.Start(options.DataRoot, updateLauncher!, updateDotnet!,
                                CodeAltaNuGetUpdateChecker.UpdateArguments(AppUpdateService.PackageId, prerelease))) return false;
                            shell.RequestUserExit();
                            return true;
                        },
                        CancelInstall = () => DesktopUpdateInstaller.Cancel(options.DataRoot),
                    };
                    appUpdate.Start();
                    builder.AddAppUpdateService(appUpdate);
                    builder.AddWorkspaceService(workspace);
                    builder.AddConfigurationService(new ConfigurationService(host.ModelProviderRegistry, host.PluginRuntime));
                    var globalConfig = new GlobalConfigService(configStore, host.ModelProviderRegistry, options.CatalogRoot!, epoch);
                    builder.AddGlobalConfigService(globalConfig);
                    providerLogin = new ProviderLoginService(configStore, globalConfig, options.CatalogRoot!, epoch);
                    builder.AddProviderLoginService(providerLogin);
                    mcpServers = new McpServersService(host.ProjectCatalog, epoch, roots.Home);
                    builder.AddMcpServersService(mcpServers);
                    builder.AddAgentPromptsService(new AgentPromptsService(host.ProjectCatalog, epoch));
                    // The standard launch has no explicit discovery home: common skills come from the profile, like the TUI.
                    builder.AddSkillsService(new SkillsService(host.ProjectCatalog, host.SkillCatalog,
                        roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), epoch));
                    builder.AddPluginsService(new PluginsService(host.ProjectCatalog, epoch, host.PluginRuntime));
                    builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
                    providers = new ModelCatalogService(host.ModelProviderRegistry, host.ModelProviderInitializationService, epoch);
                    _ = providers.StartInitialization(); // Retained and joined by providers.DrainAsync.
                    builder.AddModelCatalogService(providers);
                    builder.AddReminderService(reminders);
                    gitIssues = new GitIssuesService(host.ProjectCatalog, epoch);
                    builder.AddGitIssuesService(gitIssues);
                    builder.AddSessionOperationsService(operations);
                    builder.AddSessionAsksService(asks);
                    builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, host.RuntimeService, epoch));
                    builder.AddSessionPluginEventsService(new SessionPluginEventsService(host.WorkspaceReads, host.ProjectCatalog, epoch, host.PluginRuntime));
                    builder.AddProjectFilesService(new ProjectFilesService(host.ProjectCatalog, epoch, host.ProjectFileSearchService, editorView));
                    builder.AddProjectGitService(new ProjectGitService(host.ProjectCatalog, epoch, changesView));
                    builder.AddPromptImagesService(new PromptImagesService(host.WorkspaceReads, epoch));
                    builder.AddComposerStatusService(new ComposerStatusService(host.ProjectCatalog, epoch, roots.Home, host.PluginRuntime));
                    pluginCommands = pluginAlta is null ? new PluginUiService() : new PluginUiService(host.ProjectCatalog, host.PluginRuntime, pluginUi, epoch);
                    builder.AddPluginUiService(pluginCommands);
                    builder.AddSessionUserInputService(new SessionUserInputService(host.RuntimeService.Permissions, epoch, options.EnableOwnedUserInput));
                    builder.AddSessionDisplayService(new SessionDisplayService(host.RuntimeService.Display, epoch));
                    builder.AddSessionRuntimeStateService(new SessionRuntimeStateService(host.RuntimeService, epoch));
                    builder.AddSessionUsageService(new SessionUsageService(host.RuntimeService, epoch));
                    builder.AddSessionPermissionsService(new SessionPermissionsService(host.RuntimeService.Permissions, epoch, options.ReviewOwnedCommandPermissions));
                    var rpc = builder.Build();
                    rpcLifetime = rpc;
                    if (!closeRequested.Task.IsCompleted)
                    {
                        bindingLifetime = NeoRpcViewBinding.Bind(rpc, view);
                        var navigation = view.NavigateAsync(ApplicationDocument);
                        await navigation;
                        Mark("application loading");
                    }
                }
            }
            else workspacePrepared.TrySetResult();
            }
            else workspacePrepared.TrySetResult();
            await closeRequested.Task;
        }
        catch (Exception failure)
        {
            workspacePrepared.TrySetResult();
            LogManager.GetLogger("CodeAlta.Desktop").Error(failure, "Owned desktop initialization or application flow failed");
            bodyFailed = true;
            closeRequested.TrySetResult();
        }
        operations?.CloseAdmission();
        asks?.CloseAdmission();
        reminders?.CloseAdmission();
        providers?.CloseAdmission();
        if (_closeFlow is not null)
        {
            try
            {
                if (window is not null) await AwaitOwnedAsync(_closeFlow, window);
                else await _closeFlow;
            }
            catch (Exception) { bodyFailed = true; }
        }
        var hostConfirmed = _hostCreation is null ||
            (_hostCreation.IsCompletedSuccessfully && _hostDisposal?.IsCompletedSuccessfully == true);
        if (!hostConfirmed)
        {
            shutdownUnconfirmed = true;
            if (window is not null) window.Title = "CodeAlta — shutdown unconfirmed; native resources and lease retained";
            Console.Error.WriteLine("Owned host termination is unconfirmed. Admission is closed; ordinary close is blocked. External termination is not confirmed cleanup.");
            if (window is not null) await closed.Task;
            GC.KeepAlive(bindingLifetime);
            GC.KeepAlive(viewLifetime);
            GC.KeepAlive(rpcLifetime);
            GC.KeepAlive(chromeLifetime);
            GC.KeepAlive(environmentLifetime);
            GC.KeepAlive(window);
            return; // No native-resource disposal, lease release or ForceShutdown on this path.
        }
        var nativeFailed = false;
        foreach (var resource in new[] { bindingLifetime, viewLifetime, rpcLifetime, instanceLifetime, chromeLifetime, environmentLifetime })
        {
            if (resource is null) continue;
            try
            {
                var disposal = resource.DisposeAsync();
                await disposal;
            }
            catch (Exception) { nativeFailed = true; }
        }
        if (nativeFailed)
        {
            shutdownUnconfirmed = true;
            if (window is not null) window.Title = "CodeAlta — native shutdown unconfirmed; lease retained";
            Console.Error.WriteLine("Native resource cleanup failed after host joining; lease release is not confirmed.");
            if (window is not null) await closed.Task;
            GC.KeepAlive(bindingLifetime);
            GC.KeepAlive(viewLifetime);
            GC.KeepAlive(rpcLifetime);
            GC.KeepAlive(chromeLifetime);
            GC.KeepAlive(environmentLifetime);
            GC.KeepAlive(window);
            return;
        }
        gitIssues?.Dispose(); // Its RPC host is gone: no lookup can still use the HTTP client.
        appUpdate?.Dispose();
        if (window is not null)
        {
            allowClose = true;
            window.Close();
            await closed.Task;
            try { var disposal = window.DisposeAsync(); await disposal; }
            catch (Exception) { Console.Error.WriteLine("Native window disposal failed; lease retained."); return; }
        }
        ExitCode = bodyFailed ? 1 : 0;
        _nativeConfirmed = true;
        application.ForceShutdown(); // Only after confirmed host and native-resource disposal.
    }

    private async Task CloseOwnedHostWhenRequestedAsync(Task closeRequested, Task<CodeAltaHost> creation, Func<Task> closeImports)
    {
        await closeRequested;
        var host = await creation;
        await closeImports();
        _hostDisposal = host.DisposeAsync().AsTask();
        await _hostDisposal;
    }

    private async ValueTask RunAsync(NeoApplication application)
    {
        try
        {
            var appearance = DesktopAppearance.Load(options.DataRoot);
            appearance.ApplyToBrowser();
            await using var window = application.CreateWindow(DesktopWindowChrome.WindowOptions(developer: false, appearance));
            application.MainWindow = window;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();

            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            DesktopAssetPaths.EnsureUsable(assets, manifest);
            await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
            });
            await using var chrome = await DesktopWindowChrome.StartAsync(application, options.DataRoot);
            await chrome.ApplyWindowIconAsync(window);
            var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true }));
            chrome.AddHandlers(builder);
            builder.AddBootService(new BootService());
            // A copy of a catalog is browsed, not written: the schemes it holds are not offered.
            builder.AddColorSchemesService(new ColorSchemesService());
            builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));
            builder.AddConfigurationService(new ConfigurationService(options.CatalogRoot!));
            builder.AddGlobalConfigService(new GlobalConfigService());
            builder.AddProviderLoginService(new ProviderLoginService());
            builder.AddMcpServersService(new McpServersService());
            builder.AddAgentPromptsService(new AgentPromptsService());
            builder.AddSkillsService(new SkillsService());
            builder.AddPluginsService(new PluginsService());
            builder.AddSessionPluginEventsService(new SessionPluginEventsService());
            builder.AddPluginUiService(new PluginUiService());
            builder.AddProjectFilesService(new ProjectFilesService());
            builder.AddProjectGitService(new ProjectGitService());
            builder.AddPromptImagesService(new PromptImagesService());
            builder.AddComposerStatusService(new ComposerStatusService());
            builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
            builder.AddModelCatalogService(new ModelCatalogService());
            await using var rpc = builder.Build();
            window.Show();
            await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            await using var binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(ApplicationDocument);
            await closed.Task;
            ExitCode = 0;
        }
        catch (Exception exception)
        {
            ExitCode = 1; // Also covers failures from asynchronous disposal after normal close.
            Console.Error.WriteLine(exception);
        }
        finally
        {
            application.ForceShutdown();
        }
    }
}
