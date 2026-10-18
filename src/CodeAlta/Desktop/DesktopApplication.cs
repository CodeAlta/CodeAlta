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
    // The skills used most in the last days, for the prompts and for search: one ranking per day, kept by each session.
    private DesktopSkillUsage? _skillUsage;
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
        var capture = DesktopLogging.Initialize(options.DataRoot, out var flushPending);
        var result = 1;
        try
        {
            result = run(options, capture);
            if (result != 0)
                DesktopLogging.ReportFailure(null, $"Desktop startup or native lifetime ended with exit code {result}; termination is not confirmed.");
            return result;
        }
        catch (Exception failure)
        {
            DesktopLogging.ReportFailure(failure, "Desktop startup or native lifetime failed");
            throw;
        }
        finally
        {
            if (capture is not null && result == 0) LogManager.Shutdown();
            else flushPending?.Invoke(); // Persist failures without invalidating retained callbacks' loggers.
        }
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

    /// <summary>Allows a navigation to a document of the application, in the frame that asks, and cancels every other.</summary>
    /// <remarks>
    /// The view does not ask about its history: <see cref="DesktopWindowChrome.ViewOptions"/> turns history
    /// navigation off, so the back button of a mouse never brings the start-up screen again.
    /// </remarks>
    /// <param name="request">The navigation the view asks for.</param>
    internal static NeoNavigationDecision DecideNavigation(NeoNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsApplicationDocument(request.Uri) ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel;
    }

    /// <summary>
    /// Allows the microphone, for this request only, to a document of the application while speech to prompt is
    /// turned on, and denies every other permission. Nothing is kept in the profile of the view, so turning the
    /// feature off is enough to close the microphone.
    /// </summary>
    /// <param name="request">The permission the view asks for.</param>
    /// <param name="microphoneAllowed">Whether speech to prompt is turned on.</param>
    internal static NeoPermissionDecision DecidePermission(NeoPermissionRequest request, bool microphoneAllowed)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Every view says who asks (NeoAstra 1.4.0): a request without an origin is not one of the application.
        var own = request.Origin is { IsAbsoluteUri: true, Scheme: "app", Host: "codealta" };
        return microphoneAllowed && own && request.Kind == NeoPermissionKind.Microphone ? NeoPermissionDecision.AllowOnce : NeoPermissionDecision.DenyOnce;
    }

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
                // The running one exits, or adds the folders of this start as projects.
                var arguments = options.ExitRunning ? [DesktopCommandLine.ExitOption]
                    : options.Projects.Count > 0 ? DesktopProjectOpening.Arguments(options.Projects, options.Spaces) : null;
                await using var instance = await NeoSingleInstance.AcquireAsync(application, new NeoSingleInstanceOptions { ApplicationId = InstanceId(options) },
                    new NeoLaunchEvent(NeoLaunchReason.SecondInstance, arguments));
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
            DesktopLogging.ReportFailure(failure, "Owned desktop startup or native lifetime failed; termination is not confirmed.");
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
        DesktopSessionRetention? retention = null;
        DesktopBookmarks? bookmarkLife = null;
        Automations.AutomationService? automations = null;
        DesktopChangesView? changesView = null;
        DesktopEditorView? editorView = null;
        Terminals.DesktopTerminals? terminals = null;
        GitIssuesService? gitIssues = null;
        AppUpdateService? appUpdate = null;
        ModelCatalogService? providers = null;
        ProviderLoginService? providerLogin = null;
        ProviderUsageService? providerUsage = null;
        SpeechService? speech = null;
        ClaudeCliService? claudeCli = null;
        Search.DesktopSearch? search = null;
        McpServersService? mcpServers = null;
        PluginUiService? pluginCommands = null;
        DesktopCanvases? canvases = null;
        WorkspaceService? workspace = null;
        NeoWindow? window = null;
        DesktopWindowState? windowState = null;
        Mcp.DesktopMcpServer? mcp = null;
        IAsyncDisposable? environmentLifetime = null, rpcLifetime = null, viewLifetime = null, bindingLifetime = null, chromeLifetime = null, instanceLifetime = null;
        IAsyncDisposable? uiLifetime = null;
        IAsyncDisposable? sessionTaskbarProgress = null;
        var bodyFailed = false;
        try
        {
            // Before anything is set up: the page's files, which Windows cannot read from too deep a folder.
            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            DesktopAssetPaths.EnsureUsable(assets, manifest);
            var appearance = DesktopAppearance.Load(options.DataRoot);
            // The tools that see and drive the window, for the sessions that ask for them and for the MCP server.
            // They are on before the view loads its first document: they then know the page from its first message.
            var ui = new Ui.DesktopUiAutomation(application, Path.Combine(options.DataRoot, "ui"));
            uiLifetime = ui;
            var uiSessions = new Ui.DesktopUiSessions();
            // The Search plugin exists before the host; the index it signals is created once the host runs.
            var searchRelay = new Search.DesktopSearchRelay();
            window = application.CreateWindow(DesktopWindowChrome.WindowOptions(options.Developer, appearance));
            application.MainWindow = window;
            window.Closed += (_, _) => { retention?.ProtectionRpc.InvalidatePage(); closed.TrySetResult(); };
            var shutdownWindow = window;
            // The application's shutdown starts once, from the window being closed without a tray to stay in,
            // from Exit in the tray or in the page, or from the end of the user's session.
            var shell = new DesktopShell(window, application.Dispatcher, options.DataRoot, () =>
            {
                retention?.ProtectionRpc.InvalidatePage();
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
            // The MCP server other applications drive the window through. It listens from now on, with the tools
            // of the window; `alta` joins them once the host runs. A client saves a file in the folder of the
            // tools or in a project.
            Mcp.DesktopMcpTool? altaTool = null;
            var windowTools = Mcp.DesktopMcpTools.Window(ui, async token =>
            {
                var folders = new List<string> { ui.FilesDirectory };
                if (_hostCreation is { IsCompletedSuccessfully: true } created)
                {
                    try
                    {
                        var projects = await created.Result.ProjectCatalog.LoadAsync(token).ConfigureAwait(false);
                        folders.AddRange(projects.Where(static project => !project.Archived && Directory.Exists(project.ProjectPath)).Select(static project => project.ProjectPath));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        // The catalog cannot be read: a file is saved in the folder of the tools only.
                    }
                }

                return new Ui.DesktopUiFiles(ui.FilesDirectory, folders, $"the folder of a project of CodeAlta, or {ui.FilesDirectory}");
            });
            IReadOnlyList<Mcp.DesktopMcpTool> McpTools() => Volatile.Read(ref altaTool) is { } alta ? [.. windowTools, alta] : windowTools;
            mcp = new Mcp.DesktopMcpServer(Mcp.DesktopMcpEndpoint.For(options), McpTools, options.StateRoot ?? options.CatalogRoot!,
                DesktopCommandLine.Version, shell.McpServer, shell.SetMcpServer);
            _ = mcp.StartAsync(); // A server that cannot listen says so in Settings: the application does not wait for it.
            // The installed tool becomes an application of this desktop; the page says so the first time.
            var entry = options.Developer || roots.Home is not null ? Task.FromResult(default(DesktopIntegration.DesktopEntryChange))
                : Task.Run(() =>
                {
                    var change = DesktopIntegration.Ensure(options.DataRoot, DesktopCommandLine.Version);
                    var bundle = OperatingSystem.IsMacOS() ? DesktopIntegration.MacRunningBundleIdentifier() : null;
                    if (DesktopIntegration.AnnouncesEntry(change.Added, OperatingSystem.IsMacOS(), bundle)) shell.NotifyEntryAdded();
                    return change;
                });
            // The folders an alta names become projects (`alta .`), at this start or a later one, once the commands run.
            var projectOpening = new DesktopProjectOpening(shell.ShowProject,
                static message => LogManager.GetLogger("CodeAlta.Desktop").Warn(message));
            projectOpening.Request(options.Projects, options.Spaces);
            // Starting CodeAlta again, or selecting it in the Dock, brings back the window of the running one.
            application.LaunchReceived += launch =>
            {
                if (launch.Reason == NeoLaunchReason.SecondInstance && launch.Arguments.Contains(DesktopCommandLine.ExitOption)) shell.RequestUserExit();
                else if (launch.Reason == NeoLaunchReason.SecondInstance && DesktopProjectOpening.TryRead(launch.Arguments, out var folders, out var spaces))
                {
                    shell.Show();
                    projectOpening.Request(folders, spaces);
                }
                else if (launch.Reason is NeoLaunchReason.Activated or NeoLaunchReason.SecondInstance) shell.Show();
                return ValueTask.CompletedTask;
            };
            // The window is shown once its view has the start-up screen (see below): a window without a view
            // is a white rectangle, whatever the theme.
            var startupClock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string step) => LogManager.GetLogger("CodeAlta.Desktop").Info($"Startup: {step} at {startupClock.ElapsedMilliseconds} ms");
            var startedWindow = window;
            var catalog = new CatalogOptions { GlobalRoot = options.CatalogRoot!, StateRoot = options.StateRoot ?? options.CatalogRoot! };
            var startupScratchpadCleanup = new ScratchpadStartupCleanup(
                CodeAltaInstanceProfile.Create(options.CatalogRoot!, options.Developer), _lease);
            // Plugins read the user's profile (the MCP servers and their sign-ins, the provider CLIs): they
            // run in the normal launch and stay off when the roots are explicit.
            var pluginAlta = roots.Home is null ? new PluginAltaServiceBridge() : null;
            var startupStatus = new DesktopStartupStatus();
            using var scratchpadStartupStatus = startupScratchpadCleanup.Subscribe(startupStatus.SetScratchpadCleanup);
            var retentionProtection = DesktopSessionRetention.CreateProtection();
            var pluginUi = new DesktopPluginUi(retentionProtection);
            var pluginIdentities = new DesktopPluginIdentities();
            // The tabs that plugins provide: created with the plugins, which start before the window has a page, and attached to their runtime once it exists.
            canvases = pluginAlta is null ? null : new DesktopCanvases(pluginUi);
            pluginUi.Modules = canvases?.Modules;
            // A configuration file that cannot be loaded is repaired in the window before anything reads it: the
            // host would fail on it. A missing file is created with the defaults.
            var configRecovery = new ConfigRecoveryService(options.CatalogRoot!, new TextFileCodec());
            configRecovery.Reload();
            // Off the window's thread: the host reads the catalog and the configuration and starts the plugins
            // while the window creates its view and shows the start-up screen.
            void StartHost()
            {
            var skillUsage = _skillUsage = new DesktopSkillUsage(() => _hostCreation is { IsCompletedSuccessfully: true } created ? created.Result : null);
            _hostCreation = Task.Run(() => CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = options.CatalogRoot, StateRoot = options.StateRoot, CurrentProjectPath = roots.Project,
                StartupScratchpadCleanup = startupScratchpadCleanup,
                RetentionProtection = retentionProtection,
                DiscoveryScope = roots.Home is null || roots.Instructions is null
                    ? null
                    : new SessionDiscoveryScope(roots.Home, roots.Instructions),
                BuiltInSkillRoot = roots.Builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                ReviewOwnedCommandPermissions = options.ReviewOwnedCommandPermissions,
                AutoApproveOwnedPermissions = !options.ReviewOwnedCommandPermissions,
                // The user's setting decides, read at every send. The launch flag of the isolated-roots mode
                // still forces the review on for a host started with it.
                ReviewOwnedPermissionsPolicy = () => options.ReviewOwnedCommandPermissions || shell.ReviewPermissions,
                // The mode chosen for a session comes first; a host started with that flag reviews every session.
                SessionPermissionModes = !options.ReviewOwnedCommandPermissions,
                // The prompt of a session shows what its provider suggests.
                ShowsPromptSuggestions = true,
                // A session works under the Git, GitHub and Jira identity of its space.
                AppliesSpaceIdentities = true,
                // The prompt lists many skills in a short form, which sends agents to alta search, once the index exists.
                SkillPromptCatalog = (project, spaceId) => search?.SkillPromptCatalogFor(project, spaceId) ?? CodeAlta.Catalog.Search.SearchSettings.PromptCatalogFull,
                // The skills used most in the last days come first in the prompt: one ranking per day, kept by each session.
                PreferredSkills = skillUsage.PreferredFor,
                // A session an agent creates does not ask, unless the user chose that it asks what its creator asks.
                InheritPermissionModePolicy = () => shell.InheritPermissions,
                EnableOwnedAsks = true,
                EnableOwnedUserInput = options.EnableOwnedUserInput,
                // Source plugins are the same build as in the terminal application; the start-up screen
                // names the ones being built.
                StartPlugins = pluginAlta is not null, OwnsLogging = false, IsHeadless = false, HasInteractiveUi = true,
                PluginFrontend = PluginFrontends.Desktop, PluginAuthoringProfile = PluginAuthoringProfile.Terminal,
                PluginStartupFeedback = new DesktopPluginStartupFeedback(startupStatus),
                PluginBuiltIns = DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions,
                    DesktopPlugins.CreateJournalCatalog(catalog), new StatisticsArchiveStore(catalog.StateRoot), searchRelay), PluginSafeMode = DesktopPlugins.SafeMode,
                PluginServices = pluginAlta is null ? null : new DesktopPluginServices(pluginAlta, pluginUi, canvases, pluginIdentities),
                ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!),
            }, CancellationToken.None));
            // The retained application flow reacts even while a native acquisition is awaiting.
            // No native callback awaits this work; a close during host creation waits its actual result.
            _closeFlow = CloseOwnedHostWhenRequestedAsync(closeRequested.Task, _hostCreation, async () =>
            {
                await workspacePrepared.Task;
                if (mcp is not null) await mcp.DisposeAsync(); // No client starts a command on a host that is closing.
                if (retention is not null) await retention.DisposeAsync(); // Join original archive/retirement work before releasing its owners.
                bookmarkLife?.Dispose();
                if (workspace is not null) await Task.WhenAll(workspace.CloseImportsAsync(), workspace.CloseSessionsAsync());
                if (automations is not null) await automations.DisposeAsync(); // Nothing more is started; sessions end with the host.
                if (terminals is not null) await terminals.CloseAsync(); // The programs of the terminals end with the application.
                if (reminders is not null) await reminders.DisposeAsync();
                if (providers is not null) await providers.DrainAsync();
                if (providerLogin is not null) await providerLogin.CloseAsync(); // A running sign-in is canceled and joined.
                providerUsage?.Dispose();
                if (speech is not null) await speech.CloseAsync(); // A download of a speech model stops, and the speech worker ends.
                if (claudeCli is not null) await claudeCli.CloseAsync(); // So does a download of the Claude Code CLI.
                if (search is not null) await search.DisposeAsync(); // The indexer stops at the end of a line, and the index is closed.
                if (mcpServers is not null) await mcpServers.CloseAsync(); // So is a running MCP authorization.
                if (pluginCommands is not null) await pluginCommands.CloseAsync(); // Plugin commands still waiting in a dialog end.
                canvases?.Dispose(); // The tabs of plugins end with the application.
            });
            }
            if (configRecovery.IsReady) StartHost();
            // The window's own parts do not wait for the host: the view exists and shows the start-up screen
            // (the logo on the window's theme) as soon as the browser is ready.
            var creatingEnvironment = application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new DesktopStartupResources(new NeoManifestResourceProvider(assets, manifest), startupStatus, canvases?.Modules))],
            });
            var environment = await creatingEnvironment;
            Mark("environment created");
            environmentLifetime = environment;
            var chrome = await DesktopWindowChrome.StartAsync(application, options.DataRoot);
            chromeLifetime = chrome;
            // On macOS the icon depends on what the start did to the bundle, a few files written by now; the
            // other desktops do not wait for their entry.
            await chrome.ApplyWindowIconAsync(window, OperatingSystem.IsMacOS() && (await entry).IconChanged);
            shell.Dialogs = chrome.Services.Dialogs;
            await shell.StartTrayAsync(chrome.Services, options.Developer);
            await shell.StartApplicationMenuAsync(chrome.Services, options.Developer);
            instanceLifetime = await AcquireInstanceAsync(application, options);
            var creatingView = environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
            var view = await creatingView;
            Mark("view created");
            viewLifetime = view;
            // The window gives the keyboard to its page when it is first shown, which NeoAstra does not do for a view
            // created while its window was hidden, and when it is used again.
            DesktopWindowFocus.Follow(window, view, application.Dispatcher);
            // The kept zoom, before the first document: the window never shows at another one.
            view.ZoomFactor = shell.Zoom / 100d;
            shell.ApplyZoom = factor => view.ZoomFactor = factor;
            view.NavigationRequested = request =>
            {
                if (request.IsMainFrame && IsApplicationDocument(request.Uri)) retention?.BeforeMainFrameNavigation(request.Uri);
                return ValueTask.FromResult(DecideNavigation(request));
            };
            view.ProcessFailed += (_, _) => retention?.ProtectionRpc.InvalidatePage();
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            // The page records the microphone for speech to prompt, and only while that is turned on.
            view.PermissionRequested = request => ValueTask.FromResult(DecidePermission(request, speech?.IsMicrophoneAllowed() == true));
            // The start-up screen first, then the window: it appears with the logo on its theme, never empty.
            // The browser draws into a shown window only, so the window is shown out of sight until the
            // document is there and has had a moment to be drawn. A view that does not report the document
            // does not keep the window out of sight.
            var startupShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => startupShown.TrySetResult();
            // Where the window was when the application last ended, and whether it was maximized: given back
            // while the window is hidden, so that it appears there and not in its default place first.
            windowState = await DesktopWindowState.StartAsync(window, options.DataRoot, chrome.Services.SystemInfo);
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
            // The page's theme: kept for the next start, and the window controls and background follow it now.
            // Two reports may cross: the window takes the one kept last, whichever posted first.
            var appearances = new DesktopAppearanceKeeper(options.DataRoot, appearance);
            void RememberAppearance(DesktopAppearance remembered)
            {
                appearances.Remember(remembered);
                application.Dispatcher.Post(() =>
                {
                    if (startedWindow.IsClosed) return;
                    var latest = appearances.Latest;
                    startedWindow.TitleBar = DesktopWindowChrome.TitleBar(latest);
                    // What the window shows where the page has not drawn yet, such as what a resize uncovers.
                    startedWindow.BackgroundColor = latest.Background;
                });
            }
            // Look for a newer version at startup and periodically while the desktop runs. An instance
            // on explicit roots is automation and stays off the network.
            // Only an installed tool can replace itself: a helper waits for this process to end, runs
            // the update and starts CodeAlta again.
            AppUpdateService StartAppUpdate()
            {
                var updateLauncher = options.Developer ? null : DesktopIntegration.InstalledLauncher(AppContext.BaseDirectory, OperatingSystem.IsWindows());
                var updateDotnet = DesktopUpdateInstaller.DotnetPath();
                var selfUpdate = updateLauncher is not null && updateDotnet is not null && File.Exists(updateLauncher);
                var service = roots.Home is not null ? new AppUpdateService() : new AppUpdateService(DesktopCommandLine.Version)
                {
                    Installed = DesktopUpdateInstaller.ConsumeResult(options.DataRoot),
                    Install = !selfUpdate ? null : prerelease =>
                    {
                        if (!DesktopUpdateInstaller.Start(options.DataRoot, updateLauncher!, updateDotnet!,
                            CodeAltaNuGetUpdateChecker.UpdateArguments(AppUpdateService.PackageId, prerelease))) return false;
                        // The workspace exits in its own way, with its questions; the configuration editor has none to ask.
                        if (shell.HasWorkspace) shell.RequestUserExit();
                        else shell.RequestExit(confirmed: true);
                        return true;
                    },
                    CancelInstall = () => DesktopUpdateInstaller.Cancel(options.DataRoot),
                };
                service.Start();
                return service;
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
                // A file written by a newer version is the usual reason an installed one cannot read it: the
                // editor says when a newer version is published, and can install it.
                appUpdate = StartAppUpdate();
                recoveryBuilder.AddAppUpdateService(appUpdate);
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
            // A session whose background job runs is at work too: exiting ends the command.
            shell.RunningSessions = host.RuntimeService.CountSessionsAtWork;
            sessionTaskbarProgress = DesktopWindowsTaskbarProgress.StartIfAvailable(window, chrome.Services.WindowPolish, host.RuntimeService);
            shell.HasWorkspace = true;
            if (!closeRequested.Task.IsCompleted)
            {
                var epoch = Guid.NewGuid().ToString("D");
                // The bookmarks of the sessions of this instance, in its database. A bookmarked session is kept by the
                // retention of sessions, which deletes nothing before the bookmarks were read.
                var bookmarks = new CodeAlta.Catalog.Bookmarks.BookmarkService(host.ApplicationDatabase,
                    new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()));
                bookmarkLife = new DesktopBookmarks(bookmarks, host.RetentionProtection, host.RuntimeService);
                retention = new DesktopSessionRetention(host, epoch);
                // The git worktrees sessions can work in: created with a session, listed and removed from the changes of a project.
                var worktreeConfig = new CodeAltaConfigStore(host.CatalogOptions);
                var worktrees = new CodeAlta.Catalog.Worktrees.GitWorktreeService(host.CatalogOptions, worktreeConfig);
                workspace = new WorkspaceService(host, epoch, worktrees);
                operations = new SessionOperationsService(host.Commands, epoch) { RemoteControlChanged = shell.NoteRemoteControl };
                if (shell.ReconnectRemoteControl) DesktopRemoteControl.Reconnect(host.Commands, shell);
                asks = new SessionAsksService(host.Commands.Asks, epoch);
                reminders = new ReminderService(host.WorkspaceReads, host.Commands, host.RuntimeService, epoch, host.RetentionProtection);
                changesView = new DesktopChangesView();
                // The space the window shows, and what a command asks of it. A catalog that never had spaces
                // gets its first ones before the page reads them.
                var spaceView = new DesktopSpaceView();
                try { await host.SpaceCatalog.SeedAsync(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The window then has the default space alone. */ }
                editorView = new DesktopEditorView();
                canvases?.Attach(host.PluginRuntime, () => spaceView.ShownSpaceId);
                // A plugin works for a session under the identity of its space, and for the window under the one shown.
                pluginIdentities.Attach(
                    (sessionId, token) => sessionId is null
                        ? host.Identities.ResolveSpaceAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, token)
                        : host.RuntimeService.GetSessionIdentityAsync(sessionId, token),
                    async (projectPath, token) =>
                    {
                        var full = Path.GetFullPath(projectPath);
                        var project = (await host.ProjectCatalog.LoadAsync(token).ConfigureAwait(false))
                            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.ProjectPath)
                                && string.Equals(Path.GetFullPath(candidate.ProjectPath), full, StringComparison.OrdinalIgnoreCase));
                        return await host.Identities.ResolveAsync(null, project?.Id, token).ConfigureAwait(false);
                    });
                terminals = new Terminals.DesktopTerminals(DesktopCommandLine.Version, Path.Combine(options.DataRoot, "terminal"))
                {
                    // A terminal of the window has the identity of the space it shows; one a session opens, the session's.
                    Identity = request => (request.Agent && request.SessionId is { } owner
                        ? host.RuntimeService.GetSessionIdentityAsync(owner)
                        : host.Identities.ResolveSpaceAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId)).GetAwaiter().GetResult().Environment,
                };
                shell.BusyTerminals = () => terminals.Busy;
                // The definitions are shared with every instance; what ran is this instance's. The developer
                // instance starts paused, so that it does not run again what the normal one already runs.
                automations = new Automations.AutomationService(host.CatalogOptions.ConfigPath, token => host.ProjectCatalog.LoadAsync(token),
                    new Automations.AutomationStateStore(Path.Combine(host.CatalogOptions.StateRoot, "automations.json"), pausedByDefault: options.Developer),
                    new Automations.AutomationRunner(host), TimeProvider.System, TimeZoneInfo.Local, new Automations.GitAutomationFeed(),
                    () => [.. (host.PluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueEventSource>()],
                    // A command runs with the identity of the space its runs go to (AutomationRunner): the space of its project.
                    new Automations.ShellAutomationCommands(projectId => host.Identities.ResolveAsync(null, projectId).GetAwaiter().GetResult().Environment));
                // A host that has the user review the commands of its sessions lets no session type in a terminal, nor
                // give an automation a command to run.
                // A host that started its plugins builds one again while it runs, and tells the page when it did. Building
                // and loading a plugin runs code: a host that has the user review the commands of its sessions does not let
                // a session do it with an alta command.
                if (pluginAlta is not null) host.PluginRuntime.Changed += (_, _) => shell.NotifyPluginsChanged();
                var pluginWorkshop = pluginAlta is null || options.ReviewOwnedCommandPermissions ? null : DesktopPlugins.Workshop(host.PluginRuntime, editorView);
                // The tasks and the plans of the projects: files of each project, and the sessions of this instance that proposed or run them.
                var workItems = new CodeAlta.Catalog.WorkItems.WorkItemService(worktreeConfig, host.CatalogOptions.StateRoot);
                // The user guide that ships beside the application: the Documentation tab shows it, and agents read it.
                var documentation = new CodeAlta.Catalog.Documentation.ShippedDocumentation(
                    new CodeAlta.Orchestration.Runtime.SystemPrompts.FileSystemPromptContentLocator().GetRoots(new()).ShippedUserGuideRoot);
                var documentationView = new DesktopDocumentationView();
                // The local search of the window: the sessions of this instance, the guide, the skills, the tools and the plugins.
                // Its indexer waits a little before its first pass, so it does not slow the start.
                search = Search.DesktopSearch.Start(host, searchRelay, DesktopPlugins.CreateJournalCatalog(host.CatalogOptions), documentation,
                    roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions, search: searchRelay),
                    options.ReviewOwnedCommandPermissions ? null : () => [.. ui.Tools.Select(static tool => (tool.Name, tool.Description))],
                    static line => LogManager.GetLogger("CodeAlta.Search").Info(line), skillUsage: _skillUsage);
                // Search by meaning: a model downloaded once the user turns it on, run in a worker process of its own. An
                // instance on explicit roots is automation and stays off the network: it has keyword search alone.
                if (roots.Home is null)
                {
                    var searchLog = static (string line) => LogManager.GetLogger("CodeAlta.Search").Info(line);
                    var embeddingInstaller = new Search.Embedding.EmbeddingInstaller(host.CatalogOptions.SearchCacheRoot, host.CatalogOptions.RuntimeRoot, host.CatalogOptions.SpeechRoot,
                        Speech.SpeechModelCatalog.FindEngine(Speech.SpeechModelCatalog.CurrentRuntimeIdentifier()), handler: null);
                    var workerFolder = Directory.CreateDirectory(host.CatalogOptions.SearchCacheRoot).FullName;
                    search.AttachMeaning(new Search.DesktopSearchMeaning(search.Host, search.Indexer, embeddingInstaller, new Search.StoreMeaningVectors(search.Store, searchLog),
                        load => new Search.Embedding.EmbeddingClient(() =>
                        {
                            var worker = CodeAlta.NativeEngines.WorkerProcess.Start(Search.Embedding.EmbeddingWorker.Option, workerFolder, line => searchLog($"embedding worker: {line}"));
                            // The indexing is background work: the window, the sessions and a dictation go first.
                            worker.LowerPriority();
                            return worker;
                        }, load), searchLog));
                }
                var altaCommands = DesktopAltaTools.Attach(host, reminders.Reminders, pluginAlta, changesView, editorView,
                    new DesktopAltaTerminals(terminals, acceptsInput: !options.ReviewOwnedCommandPermissions),
                    new DesktopAltaAutomations(automations, host.ProjectCatalog, acceptsCommands: !options.ReviewOwnedCommandPermissions), worktrees, pluginWorkshop, workItems, new DesktopAltaAppearance(shell), spaceView,
                    // A command a session starts in the background, what it types in a terminal, the command it gives an
                    // automation and the plugin it builds are reviewed by nobody: only a session whose requests are all
                    // approved does any of it. A caller that is no session (a client of the MCP server) has the policy
                    // of the application.
                    new AltaCommandReviewPolicy(AcceptsCommands: !options.ReviewOwnedCommandPermissions)
                    {
                        AcceptsCommandsOf = sessionId => host.RuntimeService.GetPermissionPolicy(sessionId ?? string.Empty) == SessionPermissionPolicy.Approve,
                    },
                    // The tabs plugins provide: only a host that started its plugins has them.
                    canvases is null ? null : new DesktopAltaCanvases(canvases), documentation, documentationView, bookmarks, search);
                search.AttachCommands(altaCommands);
                projectOpening.Attach(async arguments => await altaCommands.InvokeAsync(arguments).ConfigureAwait(false));
                // The clients of the MCP server run the same commands, as callers that belong to no session.
                Volatile.Write(ref altaTool, Mcp.DesktopMcpTools.Alta(altaCommands, roots.Project, shell.NotifySessionsChanged));
                uiSessions.WorkFolder = (sessionId, token) => SessionFolderAsync(host, sessionId, token);
                // A session whose commands the user reviews does not drive the window, where the review is answered.
                uiSessions.Reviewed = sessionId => host.RuntimeService.GetPermissionPolicy(sessionId) != SessionPermissionPolicy.Approve;
                automations.Start();
                workspacePrepared.TrySetResult();
                {
                    // Leave room for ordinary pasted images and their base64/JSON overhead.
                    // Owned-only host-wide inbound UTF-8 framing cap, not a per-image/response limit.
                    var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions
                    {
                        ContractHash = NeoRpcGeneratedContract.Hash, Release = true, MaximumFrameBytes = 128 * 1024 * 1024,
                        // Keep one additional channel for the page's retention lifetime fence.
                        MaximumChannelsPerSession = 36, MaximumUnacknowledgedChannelItems = 2,
                        // Up to 32 open session panes, plus workspace/control observations.
                        MaximumConcurrentInvocationsPerSession = 128,
                        RequestRatePerSecond = 512, RequestRateBurst = 1024,
                        DiagnosticSink = new DesktopRpcDiagnostics(),
                    }));
                    chrome.AddHandlers(builder);
                    var configStore = new CodeAltaConfigStore(catalog);
                    builder.AddStorageService(new StorageService(catalog, epoch, host.StartupScratchpadCleanup, retention.Service));
                    // The window can always answer a request: the setting changes while the host runs, and a send
                    // started with it on goes on asking after it is turned off.
                    builder.AddBootService(new BootService(epoch, commandReview: true, options.EnableOwnedUserInput, options.Developer)
                    {
                        RememberAppearance = RememberAppearance, ProviderSetup = NeedsProviderSetup(configStore),
                    });
                    builder.AddDesktopShellService(new DesktopShellService(shell));
                    builder.AddColorSchemesService(new ColorSchemesService(options.CatalogRoot!));
                    // The one of the configuration editor when the application started with it.
                    appUpdate ??= StartAppUpdate();
                    builder.AddAppUpdateService(appUpdate);
                    builder.AddWorkspaceService(workspace);
                    builder.AddConfigurationService(new ConfigurationService(host.ModelProviderRegistry, host.PluginRuntime, new DesktopDefaultProvider(host.CatalogOptions), host.CatalogOptions));
                    var globalConfig = new GlobalConfigService(configStore, host.ModelProviderRegistry, options.CatalogRoot!, epoch)
                    {
                        ProviderStates = () => host.ModelProviderInitializationService.CurrentStates,
                        Spaces = host.SpaceCatalog,
                        SessionSpace = (sessionId, token) => host.RuntimeService.GetSessionSpaceAsync(sessionId, token),
                    };
                    builder.AddGlobalConfigService(globalConfig);
                    providerLogin = new ProviderLoginService(configStore, globalConfig, options.CatalogRoot!, epoch);
                    builder.AddProviderLoginService(providerLogin);
                    providerUsage = new ProviderUsageService(configStore, options.CatalogRoot!, epoch);
                    builder.AddProviderUsageService(providerUsage);
                    // An instance on explicit roots is automation and stays off the network: it downloads no model.
                    speech = roots.Home is not null ? new SpeechService() : new SpeechService(catalog, epoch);
                    speech.DictatingChanged += dictating => search?.Meaning?.SetDictating(dictating);
                    builder.AddSpeechService(speech);
                    // On explicit roots the state is read and checked, and nothing is downloaded.
                    claudeCli = new ClaudeCliService(catalog, epoch)
                    {
                        FindProvider = key => configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
                            .FirstOrDefault(definition => string.Equals(definition.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is { } definition
                            ? new ClaudeCliProvider(definition.ProviderType, definition.Command, definition.ConfigDir) : null,
                        DownloadAllowed = roots.Home is null,
                    };
                    builder.AddClaudeCliService(claudeCli);
                    mcpServers = new McpServersService(host.ProjectCatalog, epoch, roots.Home);
                    builder.AddMcpServersService(mcpServers);
                    var agentPrompts = new AgentPromptsService(host.ProjectCatalog, epoch,
                        userProfileRoot: roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    builder.AddAgentPromptsService(agentPrompts);
                    // The standard launch has no explicit discovery home: common skills come from the profile, like the TUI.
                    var skills = new SkillsService(host.ProjectCatalog, host.SkillCatalog,
                        roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), epoch);
                    builder.AddSkillsService(skills);
                    builder.AddPluginsService(new PluginsService(host.ProjectCatalog, epoch, host.PluginRuntime));
                    builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
                    providers = new ModelCatalogService(host.ModelProviderRegistry, host.ModelProviderInitializationService, epoch, new DesktopDefaultProvider(host.CatalogOptions));
                    _ = providers.StartInitialization(); // Retained and joined by providers.DrainAsync.
                    builder.AddModelCatalogService(providers);
                    builder.AddReminderService(reminders);
                    builder.AddSessionRetentionProtectionService(retention.ProtectionRpc);
                    builder.AddAutomationsService(new AutomationsService(automations, host.ProjectCatalog, epoch));
                    var pullRequestPrompts = new CodeAlta.Catalog.PullRequests.PullRequestPromptCatalog(host.CatalogOptions);
                    builder.AddPullRequestPromptsService(new PullRequestPromptsService(pullRequestPrompts, host.ProjectCatalog, epoch));
                    builder.AddSpacesService(new SpacesService(host.SpaceCatalog, spaceView, host.RuntimeService.ListOverview, async () =>
                    {
                        // A question a session asked, a command to review, a form to fill.
                        var reviews = await host.RuntimeService.Permissions.ListWaitingSessionsAsync();
                        return [.. host.Commands.Asks.ListWaitingSessions(), .. reviews];
                    }, epoch, id => host.Commands.MoveSpaceChatsAsync(id, CodeAlta.Catalog.SpaceDescriptor.DefaultId)) {
                        Fallbacks = () => host.Commands.LastFallbacks,
                        ConfigStore = new CodeAltaConfigStore(host.CatalogOptions),
                        SessionResources = new DesktopSessionResources(host, roots.Home, DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions, search: searchRelay)).ReadAsync,
                    });
                    builder.AddSpaceIdentityService(new SpaceIdentityService(host.SpaceCatalog, (sessionId, token) => host.RuntimeService.GetSessionIdentityAsync(sessionId, token), epoch));
                    var sessionStarter = new WorkItems.SessionStarter(host, worktrees, () => spaceView.ShownSpaceId);
                    builder.AddWorkItemsService(new WorkItemsService(workItems, host.ProjectCatalog, new WorkItems.WorkItemRunner(sessionStarter, workItems), epoch));
                    // The local search: the queries of the search window and of the Search canvas, in the space the window shows.
                    builder.AddLocalSearchService(new LocalSearchService(search,
                        async (spaceId, token) => await host.RuntimeService.GetSpaceFilterAsync(spaceId, null, token).ConfigureAwait(false),
                        async (spaceId, token) => (await host.ProjectCatalog.LoadAsync(token).ConfigureAwait(false))
                            .Where(project => project.Spaces.Contains(spaceId, StringComparer.Ordinal)).Select(static project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()), epoch));
                    builder.AddBookmarksService(new BookmarksService(bookmarks,
                        BookmarksService.Sessions(host.WorkspaceReads, host.SessionViewCatalog.JournalStore), epoch, host.RetentionProtection));
                    // The trackers are the ones of the plugins that are active when the page asks: the Git plugin for the
                    // repository of a project, and plugins such as Jira for trackers that are elsewhere.
                    var pluginRuntime = host.PluginRuntime;
                    builder.AddIssuesService(new IssuesService(
                        () => [.. (pluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueTrackerSource>()],
                        host.ProjectCatalog, sessionStarter, epoch));
                    gitIssues = new GitIssuesService(host.ProjectCatalog, epoch,
                        async token => (await host.Identities.ResolveSpaceAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, token).ConfigureAwait(false)).Environment);
                    builder.AddGitIssuesService(gitIssues);
                    builder.AddSessionOperationsService(operations);
                    builder.AddSessionAsksService(asks);
                    builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, host.RuntimeService, epoch));
                    builder.AddSessionPluginEventsService(new SessionPluginEventsService(host.WorkspaceReads, host.ProjectCatalog, epoch, host.PluginRuntime, canvases?.Modules));
                    // The code editor also opens on the folder of a skill that the Skills page names, and on the
                    // folder of a file that a link names when no project has it.
                    var diskFolders = new DiskFolders();
                    // The editors of the machine are looked for when the page first asks for them, not at start.
                    var externalEditors = new ExternalEditors(() => shell.ExternalEditor);
                    builder.AddExternalEditorsService(new ExternalEditorsService(externalEditors, shell.SetExternalEditor, host.ProjectCatalog, epoch,
                        async (sessionId, token) => await SessionFolderAsync(host, sessionId, token).ConfigureAwait(false)));
                    var projectFiles = new ProjectFilesService(host.ProjectCatalog, epoch, host.ProjectFileSearchService, editorView, skills: skills.Folders, folders: diskFolders)
                    {
                        ExternalEditors = externalEditors,
                    };
                    builder.AddProjectFilesService(projectFiles);
                    // The files and the folders the pages of Settings read: the page names one, the host finds and opens it.
                    builder.AddSettingsFilesService(new SettingsFilesService(host.ProjectCatalog, epoch, diskFolders, editorView, agentPrompts, skills, mcpServers, pullRequestPrompts));
                    // A link of the Markdown that the window shows: an address of the web for the system browser, a
                    // file for the code editor. A relative path starts from the folder the session works in.
                    builder.AddMarkdownLinksService(new MarkdownLinksService(epoch, DesktopLinks.Open, new DesktopFileLinks(host.ProjectCatalog, diskFolders, editorView,
                        (sessionId, token) => SessionFolderAsync(host, sessionId, token), projectFiles.RootAsync, DesktopLinks.Open,
                        roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    {
                        GuidePage = (file, anchor) => documentation.FindPage(file) is { } page && documentationView.Show(page, anchor),
                    }));
                    builder.AddProjectGitService(new ProjectGitService(host.ProjectCatalog, epoch, changesView));
                    // The window of the worktrees also names the sessions that record each checkout, and opens the code editor on one.
                    builder.AddWorktreesService(new WorktreesService(worktrees, host.ProjectCatalog, worktreeConfig, host.RuntimeService.ListBusySessionFolders, epoch,
                        async token => (await host.WorkspaceReads.ReadSnapshotAsync(token).ConfigureAwait(false)).Sessions, diskFolders, editorView)
                    {
                        ExternalEditors = externalEditors,
                    });
                    // A terminal opened from a session starts in the folder that session works in: its worktree
                    // while that folder is there, the folder of its project otherwise.
                    builder.AddTerminalsService(new TerminalsService(terminals, host.ProjectCatalog, epoch,
                        async (sessionId, token) => await SessionFolderAsync(host, sessionId, token).ConfigureAwait(false)));
                    builder.AddMcpHostService(new McpHostService(mcp, McpTools, epoch));
                    builder.AddPromptImagesService(new PromptImagesService(host.WorkspaceReads, epoch));
                    builder.AddToolCallsService(new ToolCallsService(host.WorkspaceReads, host.RuntimeService.ToolOutput, epoch) { Jobs = host.RuntimeService.Jobs });
                    builder.AddComposerStatusService(new ComposerStatusService(host.ProjectCatalog, epoch, roots.Home, host.PluginRuntime)
                    {
                        SessionSpace = async (sessionId, token) => await host.RuntimeService.GetSessionSpaceIdAsync(sessionId, token).ConfigureAwait(false),
                        SessionSpaceFilter = (sessionId, token) => host.RuntimeService.GetSessionSpaceFilterAsync(sessionId, token),
                        // A new session is created in the space the window shows, as the page asks for it.
                        NewSessionSpaceFilter = async (projectId, token) => await host.RuntimeService.GetSpaceFilterAsync(spaceView.ShownSpaceId,
                            projectId is null ? null : await host.ProjectCatalog.GetByIdAsync(projectId, token).ConfigureAwait(false), token).ConfigureAwait(false),
                    });
                    pluginCommands = pluginAlta is null ? new PluginUiService() : new PluginUiService(host.ProjectCatalog, host.PluginRuntime, pluginUi, epoch, canvases)
                    {
                        ShownSpace = () => spaceView.ShownSpaceId,
                        SessionSpace = async (sessionId, token) => await host.RuntimeService.GetSessionSpaceIdAsync(sessionId, token).ConfigureAwait(false),
                    };
                    builder.AddPluginUiService(pluginCommands);
                    builder.AddCanvasesService(canvases is null ? new CanvasesService() : new CanvasesService(canvases, epoch));
                    builder.AddDocumentationService(new DocumentationService(documentation, epoch, documentationView, new DocumentationAsker(host, documentation, () => spaceView.ShownSpaceId), shell.NotifySessionsChanged));
                    builder.AddSessionUserInputService(new SessionUserInputService(host.RuntimeService.Permissions, epoch, options.EnableOwnedUserInput));
                    builder.AddSessionDisplayService(new SessionDisplayService(host.RuntimeService.Display, epoch));
                    builder.AddSessionRuntimeStateService(new SessionRuntimeStateService(host.RuntimeService, epoch));
                    builder.AddSessionUsageService(new SessionUsageService(host.RuntimeService, host.WorkspaceReads, epoch));
                    // A request exists only for a send that was reviewed: it stays readable and answerable after the
                    // setting is turned off, until that send ends.
                    builder.AddSessionPermissionsService(new SessionPermissionsService(host.RuntimeService.Permissions, epoch, static () => true));
                    var rpc = builder.Build();
                    rpcLifetime = rpc;
                    // All session-work producers now share the original admission registry. This does
                    // not qualify desktop restoration: its authenticated lifetime/inventory handshake
                    // is still unknown until the application document has completed it.
                    DesktopSessionRetention.CompleteExternalRegistration(host, retentionProtection);
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
            DesktopLogging.ReportFailure(failure, "Owned desktop initialization or application flow failed");
            bodyFailed = true;
            closeRequested.TrySetResult();
        }
        retention?.ProtectionRpc.InvalidatePage();
        operations?.CloseAdmission();
        asks?.CloseAdmission();
        reminders?.CloseAdmission();
        providers?.CloseAdmission();
        // This application's own timer must stop even if later host/native cleanup is unconfirmed.
        if (appUpdate is not null)
        {
            try { await appUpdate.DisposeAsync(); }
            catch (Exception failure)
            {
                bodyFailed = true;
                LogManager.GetLogger("CodeAlta.Desktop").Error(failure, "The update check did not stop cleanly");
            }
        }
        if (_closeFlow is not null)
        {
            try
            {
                if (window is not null) await AwaitOwnedAsync(_closeFlow, window);
                else await _closeFlow;
            }
            catch (Exception) { bodyFailed = true; }
        }
        if (mcp is not null)
        {
            // Whatever became of the host: nothing answers a client any more.
            try { await mcp.DisposeAsync(); }
            catch (Exception failure) { LogManager.GetLogger("CodeAlta.Desktop").Error(failure, "The MCP server did not stop"); }
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
            GC.KeepAlive(sessionTaskbarProgress);
            GC.KeepAlive(window);
            return; // No native-resource disposal, lease release or ForceShutdown on this path.
        }
        // The placement of the window for the next start, written while the window is still there.
        if (windowState is not null) await windowState.DisposeAsync();
        var nativeFailed = false;
        foreach (var resource in new[] { uiLifetime, bindingLifetime, viewLifetime, rpcLifetime, instanceLifetime, sessionTaskbarProgress, chromeLifetime, environmentLifetime })
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
            GC.KeepAlive(sessionTaskbarProgress);
            GC.KeepAlive(window);
            return;
        }
        gitIssues?.Dispose(); // Its RPC host is gone: no lookup can still use the HTTP client.
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

    // The folder a session works in: its git worktree while that folder is there, the folder of its project otherwise.
    private static async ValueTask<string?> SessionFolderAsync(CodeAltaHost host, string sessionId, CancellationToken cancellationToken)
        => (await host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Sessions
            .FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) is { } session
            ? !string.IsNullOrWhiteSpace(session.WorktreePath) && Directory.Exists(session.WorktreePath) ? session.WorktreePath : session.WorkspacePath
            : null;

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
            builder.AddStorageService(new StorageService());
            builder.AddProviderLoginService(new ProviderLoginService());
            builder.AddProviderUsageService(new ProviderUsageService());
            builder.AddSpeechService(new SpeechService());
            builder.AddClaudeCliService(new ClaudeCliService());
            builder.AddMcpServersService(new McpServersService());
            builder.AddAgentPromptsService(new AgentPromptsService());
            builder.AddSkillsService(new SkillsService());
            builder.AddPluginsService(new PluginsService());
            builder.AddSessionPluginEventsService(new SessionPluginEventsService());
            builder.AddPluginUiService(new PluginUiService());
            builder.AddCanvasesService(new CanvasesService());
            builder.AddDocumentationService(new DocumentationService());
            builder.AddProjectFilesService(new ProjectFilesService());
            builder.AddExternalEditorsService(new ExternalEditorsService());
            builder.AddSettingsFilesService(new SettingsFilesService());
            builder.AddProjectGitService(new ProjectGitService());
            builder.AddWorktreesService(new WorktreesService());
            builder.AddMcpHostService(new McpHostService());
            builder.AddTerminalsService(new TerminalsService());
            builder.AddAutomationsService(new AutomationsService());
            builder.AddWorkItemsService(new WorkItemsService());
            builder.AddBookmarksService(new BookmarksService());
            builder.AddLocalSearchService(new LocalSearchService());
            builder.AddSessionRetentionProtectionService(new SessionRetentionProtectionService());
            builder.AddIssuesService(new IssuesService());
            builder.AddPullRequestPromptsService(new PullRequestPromptsService());
            builder.AddSpacesService(new SpacesService());
            builder.AddSpaceIdentityService(new SpaceIdentityService());
            builder.AddPromptImagesService(new PromptImagesService());
            builder.AddToolCallsService(new ToolCallsService());
            builder.AddComposerStatusService(new ComposerStatusService());
            builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
            builder.AddModelCatalogService(new ModelCatalogService());
            await using var rpc = builder.Build();
            window.Show();
            await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
            view.NavigationRequested = static request => ValueTask.FromResult(DecideNavigation(request));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            await using var binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(ApplicationDocument);
            await closed.Task;
            ExitCode = 0;
        }
        catch (Exception exception)
        {
            ExitCode = 1; // Also covers failures from asynchronous disposal after normal close.
            DesktopLogging.ReportFailure(exception, "Desktop initialization or application flow failed");
        }
        finally
        {
            application.ForceShutdown();
        }
    }
}
