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
    // The line of the start-up screen: a start that fails after the window is shown says it there.
    private DesktopStartupStatus? _startupStatus;
    // The skills used most in the last days, for the prompts and for search: one ranking per day, kept by each session.
    private DesktopSkillUsage? _skillUsage;
    // The reading of the policy of the organizations of the Copilot accounts, which the host started and disposes.
    private CopilotManagedPolicySource? _copilotPolicies;
    private Task? _hostDisposal;
    private Task? _closeFlow;
    private bool _nativeConfirmed;
    // Whether the window was shown: a failure before it is the user's to read, wherever the start came from.
    private bool _windowShown;
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
        // The proxy of [network.proxy] before any HTTP client sends, and in the environment of every program started.
        if (options.Owned is not null && options.CatalogRoot is { } networkRoot) options = options with { Network = CodeAltaNetwork.Start(networkRoot) };
        // The traces of the runs go to the OpenTelemetry collector the environment names, if any: the end sends the last ones.
        using var telemetry = CodeAltaTelemetry.StartFromEnvironment("alta");
        return RunWithCapture(options, RunCore);
    }

    /// <summary>What the start-up screen says when the start failed: the reason, and where the log is.</summary>
    internal static string StartFailure(Exception failure, string logs)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var reason = (failure is AggregateException { InnerException: { } inner } ? inner : failure).Message.ReplaceLineEndings(" ").Trim();
        return $"CodeAlta could not start: {reason} The log is in {logs}.";
    }

    private static string LockPath(DesktopLaunchOptions options) => Path.Combine(options.StateRoot ?? options.CatalogRoot!, "alta.lock");

    // What an alta command that failed said: the message of its first error record, else its first line.
    private static string FirstLine(string output)
    {
        var line = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "The prompt was not sent.";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text) line = text;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not a record: the line as it is.
        }

        return line.Length <= 300 ? line : line[..300];
    }

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
        // The secrets of the machine never land in the log: their values are masked in its file and its rows.
        var masker = options.CatalogRoot is { } secretsRoot ? new CodeAlta.Agent.Runtime.AgentSecretMasker(new CodeAlta.Catalog.Secrets.LocalSecretStore(secretsRoot).GetSecretValues) : null;
        var capture = DesktopLogging.Initialize(options.DataRoot, LoggingSettingsOf(options), masker, out var flushPending);
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

    // The levels of the features of the log, from the configuration of the user, before the host reads it.
    private static Func<CodeAlta.Catalog.Logging.LoggingSettings>? LoggingSettingsOf(DesktopLaunchOptions options)
        => options.CatalogRoot is { } root
            ? () => new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root, StateRoot = options.StateRoot ?? root }).LoadGlobalLoggingSettings()
            : null;

    private static int RunCore(DesktopLaunchOptions options, DesktopLogCapture? capture)
    {
        DesktopThreadPool.Configure();
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
    /// turned on, and the list of the installed fonts, which Settings → Appearance → Fonts offers for the code
    /// font, to a document of the application, and the reading of the clipboard, which the Paste of the editors'
    /// context menu needs (Ctrl+V needs none), to a document of the application; denies every other permission.
    /// Nothing is kept in the profile of the view, so turning the feature off is enough to close the microphone.
    /// </summary>
    /// <param name="request">The permission the view asks for.</param>
    /// <param name="microphoneAllowed">Whether speech to prompt is turned on.</param>
    internal static NeoPermissionDecision DecidePermission(NeoPermissionRequest request, bool microphoneAllowed)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Every view says who asks (NeoAstra 1.4.0): a request without an origin is not one of the application.
        var own = request.Origin is { IsAbsoluteUri: true, Scheme: "app", Host: "codealta" };
        var allowed = request.Kind switch
        {
            NeoPermissionKind.Microphone => microphoneAllowed,
            NeoPermissionKind.LocalFonts => true,
            NeoPermissionKind.ClipboardRead => true,
            _ => false,
        };
        return own && allowed ? NeoPermissionDecision.AllowOnce : NeoPermissionDecision.DenyOnce;
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
            WarnOfSandbox(options);
            if (options.Network is { } network)
            {
                var logger = LogManager.GetLogger("CodeAlta.Desktop");
                if (network.Error is { } networkError) logger.Warn(networkError);
                logger.Info($"Network proxy: {CodeAlta.Catalog.Network.ProxySettings.NameOf(network.Proxy.Settings.Mode)}{(network.Sandboxed ? " (sandboxed)" : "")}");
            }
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
            DesktopStartupFailure.Report(options, failure);
            return 1;
        }
        finally { GC.KeepAlive(desktop); } // Strong owner/lease lifetime across the synchronous native loop.
    }

    // WebKitGTK aborts the process when its sandbox cannot start, which no code catches: what is known of it is said
    // before. A start that waits in a terminal explains it once the application ended; others write it here.
    private static void WarnOfSandbox(DesktopLaunchOptions options)
    {
        if (!DesktopStartupFailure.SandboxRestricted()) return;
        var guidance = DesktopStartupFailure.SandboxGuidance(Environment.ProcessPath ?? "alta");
        LogManager.GetLogger("CodeAlta.Desktop").Warn($"Unprivileged user namespaces are restricted for this process: the window may not open.{Environment.NewLine}{guidance}");
        if (options.StartToken is null) Console.Error.WriteLine($"warning: if the window does not open, this is why.{Environment.NewLine}{guidance}");
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
        catch (TimeoutException)
        {
            window.Title = "CodeAlta — owned work pending; lease retained";
            // The steps logged before this line say which one has not ended.
            LogManager.GetLogger("CodeAlta.Desktop").Warn("Owned work is still pending after 5 s; the window waits for it.");
        }
        await task; // The timeout above never substitutes for joining the actual work.
    }

    private async ValueTask RunOwnedAsync(NeoApplication application)
    {
        var roots = options.Owned!;
        var closeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Whether the host closes and what its close waits for: the page shows it, and offers to end the process.
        var closing = new DesktopClosing();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspacePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowClose = false;
        var shutdownUnconfirmed = false;
        SessionOperationsService? operations = null;
        SessionAsksService? asks = null;
        ReminderService? reminders = null;
        DesktopSessionRetention? retention = null;
        DesktopBookmarks? bookmarkLife = null;
        Fleet.DesktopFleet? fleet = null;
        // Where the providers reach the model servers (llama.cpp) of this computer and of the paired machines, filled once they run.
        var modelServerTransports = new CodeAlta.Fleet.Client.FleetModelServerTransports();
        ModelServers.DesktopModelServers? modelServers = null;
        Fleet.DesktopFleetServers? fleetServers = null;
        Fleet.DesktopNode? desktopNode = null;
        Fleet.DesktopRemoteChildren? remoteChildren = null;
        Automations.AutomationService? automations = null;
        Automations.AutomationRunner? automationRunner = null;
        Fleet.DesktopAutomationPlacement? placement = null;
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
        // The providers whose type a plugin runs (ACP agents), and the MCP endpoints that give them the tools of a session.
        var toolEndpoints = new CodeAlta.Agent.Harness.LoopbackHarnessToolEndpoints(LogManager.GetLogger("CodeAlta.Desktop.ToolEndpoints"));
        var pluginProviderTypes = new PluginModelProviderTypes(toolEndpoints);
        // What models.dev knows of the models (limits, prices, what they take), as in the terminal application.
        CodeAlta.Agent.ModelCatalog.ModelsDevCatalogService? modelsDev = null;
        Search.DesktopSearch? search = null;
        McpServersService? mcpServers = null;
        // The views of MCP Apps the window shows, on a scheme of their own (DesktopMcpAppViews.Scheme).
        var mcpAppViews = new DesktopMcpAppViews();
        PluginUiService? pluginCommands = null;
        DesktopCanvases? canvases = null;
        WorkspaceService? workspace = null;
        NeoWindow? window = null;
        DesktopWindowState? windowState = null;
        // The main window and the secondary ones, which hold the tabs moved out of it.
        Windows.DesktopWindows? desktopWindows = null;
        Mcp.DesktopMcpServer? mcp = null;
        DesktopKeepAwake? keepAwake = null;
        DesktopExitSignals? exitSignals = null;
        DesktopUiWatchdog? uiWatchdog = null;
        // The files dropped on the windows where the page does not get them.
        var fileDrops = new DesktopFileDrops();
        IDisposable? fileDropsLifetime = null;
        IAsyncDisposable? environmentLifetime = null, rpcLifetime = null, viewLifetime = null, bindingLifetime = null, chromeLifetime = null, instanceLifetime = null;
        IAsyncDisposable? uiLifetime = null;
        IAsyncDisposable? sessionTaskbarProgress = null;
        IAsyncDisposable? sessionTurnAlerts = null;
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
            // The web browsers sessions drive: what the browser plugin learns of a session comes once the host runs.
            // The MCP ports of the instances of this user, whatever their profile: this one records its own there, and its
            // browser and sandboxes keep away from all of them. An instance on explicit roots keeps a record of its own.
            var mcpPorts = new CodeAlta.Core.IO.McpPortRecord(roots.Home is null ? CodeAlta.Core.IO.McpPortRecord.DefaultFolder() : Path.Combine(options.DataRoot, "mcp-ports"));
            // A page never reads the files of the profile, wherever it is (--profile).
            var browserHost = new CodeAlta.Plugin.Browser.BrowserPluginHost(options.StateRoot ?? options.CatalogRoot!)
            {
                SecretRoots = [options.CatalogRoot!, options.StateRoot ?? options.CatalogRoot!],
            };
            // The Search plugin exists before the host; the index it signals is created once the host runs.
            var searchRelay = new Search.DesktopSearchRelay();
            window = application.CreateWindow(DesktopWindowChrome.WindowOptions(options.Developer, appearance));
            application.MainWindow = window;
            var windows = desktopWindows = new Windows.DesktopWindows(application, options.DataRoot, options.Developer);
            window.Closed += (_, _) => { retention?.ProtectionRpc.InvalidatePage(); closed.TrySetResult(); };
            var shutdownWindow = window;
            // The application's shutdown starts once, from the window being closed without a tray to stay in,
            // from Exit in the tray or in the page, or from the end of the user's session.
            var shell = new DesktopShell(window, application.Dispatcher, options.DataRoot, () =>
            {
                retention?.ProtectionRpc.InvalidatePage();
                // From now on a signal ends the process as it does by default: a close that lasts can be cut short.
                exitSignals?.Dispose();
                operations?.CloseAdmission();
                asks?.CloseAdmission();
                reminders?.CloseAdmission();
                providers?.CloseAdmission();
                closing.Begin();
                closeRequested.TrySetResult();
                application.Dispatcher.Post(() => { if (!shutdownUnconfirmed && !shutdownWindow.IsClosed) shutdownWindow.Title = "CodeAlta — shutdown pending; lease retained"; });
            }) { Automated = options.Automated };
            // The program that started an automated CodeAlta may exit it with a signal: it then exits as alta --exit asks.
            if (options.Automated) exitSignals = DesktopExitSignals.Start(shell.RequestUserExit);
            // The computer does not go to idle sleep while CodeAlta runs, its window shown or hidden, unless the user
            // turned it off: sessions, automations and reminders keep going. A system that refuses says so in the log.
            // The secondary windows hide and come back with the main one; a dialog opens over the window the user used last.
            shell.SecondaryWindowsVisible = windows.SetSecondaryVisible;
            shell.DialogOwner = () => windows.FocusedWindow;
            shell.FocusedLabel = () => windows.FocusedLabel;
            keepAwake = new DesktopKeepAwake(DesktopKeepAwake.PlatformBackend(), static message => LogManager.GetLogger("CodeAlta.Desktop").Warn(message));
            shell.ApplyKeepAwake = keepAwake.Set;
            keepAwake.Set(shell.KeepAwake);
            // Every call of the page enters and leaves the host through the thread of the window: a stall of it is logged.
            uiWatchdog = new DesktopUiWatchdog(callback => application.Dispatcher.Post(callback));
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
            // The Tailscale network is a choice of Settings, unless the start named the address.
            var tailnet = options.McpHost is null ? new Mcp.DesktopTailnetChoice(shell.McpTailnet, shell.SetMcpTailnet, Mcp.DesktopTailnet.FindAsync) : null;
            mcp = new Mcp.DesktopMcpServer(Mcp.DesktopMcpEndpoint.For(options), McpTools, options.StateRoot ?? options.CatalogRoot!,
                DesktopCommandLine.Version, shell.McpServer, shell.SetMcpServer, tailnet) { Ports = mcpPorts };
            // A server that cannot listen says so in Settings: the application does not wait for it. Nothing in its start waits on a
            // loopback address, so it would build and start its web host on this thread, before the window is shown.
            _ = Task.Run(mcp.StartAsync);
            // The installed tool becomes an application of this desktop; the page says so the first time.
            var entry = options.Developer || roots.Home is not null ? Task.FromResult(default(DesktopIntegration.DesktopEntryChange))
                : Task.Run(() =>
                {
                    var change = DesktopIntegration.Ensure(options.IntegrationRoot ?? options.DataRoot, DesktopCommandLine.Version);
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
            // The private networks of this computer (Tailscale, Dev Tunnels) and those of the active plugins: the page of the phones is
            // published through one, and the fleet names this computer by one in its pairing links.
            var privateNetworks = new CodeAlta.Plugins.PrivateNetworks.PrivateNetworkRegistry(
                () => _hostCreation is { IsCompletedSuccessfully: true } created ? created.Result.PluginRuntime.ActivePlugins.Select(static plugin => (object?)plugin.Instance) : []);
            var startupStatus = _startupStatus = new DesktopStartupStatus();
            using var scratchpadStartupStatus = startupScratchpadCleanup.Subscribe(startupStatus.SetScratchpadCleanup);
            var retentionProtection = DesktopSessionRetention.CreateProtection();
            var pluginUi = new DesktopPluginUi(retentionProtection);
            var pluginIdentities = new DesktopPluginIdentities();
            var pluginAsks = new DesktopPluginAsks();
            var pluginPermissions = new DesktopPluginPermissions();
            // The values of the settings plugins declare, in config.toml: a setting of each project is in the file of its project.
            var settingsProjects = new ProjectCatalog(catalog);
            var pluginSettingsStore = new PluginSettingsStore(catalog.ConfigPath,
                projectId => settingsProjects.GetByIdAsync(projectId).GetAwaiter().GetResult() is { Archived: false } project ? project.ProjectPath : null);
            // The tabs that plugins provide: created with the plugins, which start before the window has a page, and attached to their runtime once it exists.
            canvases = pluginAlta is null ? null : new DesktopCanvases(pluginUi);
            pluginUi.Modules = canvases?.Modules;
            // What plugins ask goes to one window: the one of the session, the one the command came from, the one used last.
            pluginUi.SessionWindow = session => windows.WindowOfSession(session) is { } owner ? Windows.DesktopWindows.LabelOf(owner) : null;
            pluginUi.FocusedWindow = () => windows.FocusedLabel;
            pluginUi.WindowOpen = label => windows.Current(label) is not null;
            windows.Changed += pluginUi.WindowsChanged;
            // A configuration file that cannot be loaded is repaired in the window before anything reads it: the
            // host would fail on it. A missing file is created with the defaults.
            var configRecovery = new ConfigRecoveryService(options.CatalogRoot!, new TextFileCodec());
            configRecovery.Reload();
            // Off the window's thread: the host reads the catalog and the configuration and starts the plugins
            // while the window creates its view and shows the start-up screen.
            void StartHost()
            {
            var skillUsage = _skillUsage = new DesktopSkillUsage(() => _hostCreation is { IsCompletedSuccessfully: true } created ? created.Result : null);
            _hostCreation = Task.Run(() =>
            {
            // Read from its cache or the snapshot of the build. An instance on explicit roots is automation and stays
            // off the network: it does not refresh it.
            modelsDev = new CodeAlta.Agent.ModelCatalog.ModelsDevCatalogService(new CodeAlta.Agent.ModelCatalog.ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(options.CatalogRoot!, "cache", "model-catalog", "models_dev_db.json"),
            });
            if (roots.Home is null) modelsDev.StartBackgroundRefresh();
            // The models of the agents of plugins are described by it too.
            pluginProviderTypes.ModelCatalog = modelsDev;
            return CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = options.CatalogRoot, StateRoot = options.StateRoot, CurrentProjectPath = roots.Project,
                StartupScratchpadCleanup = startupScratchpadCleanup,
                // The start-up screen names the step the host is at.
                StartupStep = step => startupStatus.Text = step,
                RetentionProtection = retentionProtection,
                DiscoveryScope = roots.Home is null || roots.Instructions is null
                    ? null
                    : new SessionDiscoveryScope(roots.Home, roots.Instructions),
                // A separate profile reads none of the user's home: not the skills, agents and instructions other tools
                // keep there, as altacli --profile.
                ReadsUserHome = options.OtherToolsHome is not null,
                BuiltInSkillRoot = roots.Builtin,
                ModelServerTransports = modelServerTransports.Resolve,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                ReviewOwnedCommandPermissions = options.ReviewOwnedCommandPermissions,
                // No sandbox reaches the MCP server of the window, on whatever port it listens.
                ReservedLocalPorts = () => mcp?.ListeningPort is { } listening ? [listening, .. mcpPorts.Current()] : mcpPorts.Current(),
                // The normal instance asks a locked vault to unlock; the developer instance never does, beside the one the user works in.
                CredentialPrompts = !options.Developer,
                // The developer instance asks a vault to unlock on a click, once the user lets it in Settings › Secrets.
                CredentialsDeveloperInstance = options.Developer,
                // A provider whose key waits for a vault stays listed, and a session that uses it asks the vault to unlock.
                WaitForVaultProviders = true,
                // Windows Hello opens over the window of CodeAlta; Touch ID needs no window.
                UserPresenceVerifier = CodeAlta.Credentials.SystemUserPresence.ForThisSystem(() => window is { IsClosed: false } shown && OperatingSystem.IsWindows()
                    ? shown.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value : 0),
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
                // A Claude Code conversation open in VS Code or a terminal: the window asks what to do.
                AskWhenConversationHeld = true,
                // The sessions this window runs or has a tab of are leased: the command line cannot run them meanwhile. The
                // sessions it writes into the sessions folder are listed, and followed while it runs them.
                SessionLeaseOwner = CodeAlta.Catalog.SessionLeaseOwner.DesktopKind,
                FollowExternalSessions = true,
                // Source plugins are the same build as in the terminal application; the start-up screen
                // names the ones being built.
                StartPlugins = pluginAlta is not null, OwnsLogging = false, IsHeadless = false, HasInteractiveUi = true,
                PluginFrontend = PluginFrontends.Desktop, PluginAuthoringProfile = PluginAuthoringProfile.Terminal,
                PluginStartupFeedback = new DesktopPluginStartupFeedback(startupStatus),
                PluginBuiltIns = DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions,
                    DesktopPlugins.CreateJournalCatalog(catalog), new StatisticsArchiveStore(catalog.StateRoot), searchRelay, browserHost, options.Profile), PluginSafeMode = DesktopPlugins.SafeMode,
                PluginServices = pluginAlta is null ? null : new DesktopPluginServices(pluginAlta, pluginUi, canvases, pluginIdentities, new DesktopPluginAppFiles(assets, manifest), pluginAsks, pluginPermissions, pluginSettingsStore,
                    privateNetworks),
                // With the provider types of the plugins, which start later: their providers wait for them, as the log says.
                ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!, modelsDev, pluginProviderTypes),
                ConfigurePluginModelProviders = (registry, runtime) =>
                {
                    pluginProviderTypes.Attach(runtime.GetModelProviderTypes);
                    pluginProviderTypes.RegisterOrReplace(registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!, includeDisabled: false);
                },
                // The policy the organization of a Copilot account of this computer sets narrows every session.
                // The developer instance can take a managed-settings.json as the policy of every Copilot account
                // (CODEALTA_DEV_COPILOT_MANAGED_SETTINGS), to check the window without an enterprise.
                StartManagedPolicies = state => _copilotPolicies = CopilotManagedPolicySource.Start(state, options.CatalogRoot!, new CodeAltaConfigStore(catalog),
                    options.Developer ? Environment.GetEnvironmentVariable("CODEALTA_DEV_COPILOT_MANAGED_SETTINGS") : null),
            }, CancellationToken.None);
            });
            // The retained application flow reacts even while a native acquisition is awaiting.
            // No native callback awaits this work; a close during host creation waits its actual result.
            _closeFlow = CloseOwnedHostWhenRequestedAsync(closeRequested.Task, _hostCreation, closing, async () =>
            {
                await workspacePrepared.Task;
                LogManager.GetLogger("CodeAlta.Desktop").Info("Closing the host.");
                // Each step is logged and runs whatever the one before did: a step that fails must not leave the
                // host half closed, its services disposed while the process and its window stay.
                await CloseStepAsync(closing, "MCP server", async () => { if (mcp is not null) await mcp.DisposeAsync(); }); // No client starts a command on a host that is closing.
                await CloseStepAsync(closing, "session retention", async () => { if (retention is not null) await retention.DisposeAsync(); }); // Join original archive/retirement work before releasing its owners.
                await CloseStepAsync(closing, "bookmarks", () => { bookmarkLife?.Dispose(); return ValueTask.CompletedTask; });
                await CloseStepAsync(closing, "placed automations", async () => { if (placement is not null) await placement.DisposeAsync(); }); // What was copied runs on its machines.
                await CloseStepAsync(closing, "remote children", async () => { if (remoteChildren is not null) await remoteChildren.DisposeAsync(); }); // Their answers wait on their machines.
                await CloseStepAsync(closing, "model servers", async () => { if (modelServers is not null) await modelServers.DisposeAsync(); }); // Those kept running are taken back at the next start.
                await CloseStepAsync(closing, "paired machines", async () => { if (fleet is not null) await fleet.DisposeAsync(); }); // Their channels close; their sessions go on where they run.
                await CloseStepAsync(closing, "paired computers", async () => { if (desktopNode is not null) await desktopNode.DisposeAsync(); }); // The computers paired with this one see it offline.
                await CloseStepAsync(closing, "servers", async () => { if (fleetServers is not null) await fleetServers.DisposeAsync(); }); // Their control connections close; the servers see this computer offline.
                await CloseStepAsync(closing, "sessions", async () => { if (workspace is not null) await Task.WhenAll(workspace.CloseImportsAsync(), workspace.CloseSessionsAsync()); });
                await CloseStepAsync(closing, "automations", async () => { if (automations is not null) await automations.DisposeAsync(); }); // Nothing more is started; sessions end with the host.
                await CloseStepAsync(closing, "terminals", async () => { if (terminals is not null) await terminals.CloseAsync(); }); // The programs of the terminals end with the application.
                await CloseStepAsync(closing, "reminders", async () => { if (reminders is not null) await reminders.DisposeAsync(); });
                await CloseStepAsync(closing, "providers", async () => { if (providers is not null) await providers.DrainAsync(); });
                await CloseStepAsync(closing, "models.dev catalog", async () => { if (modelsDev is not null) await modelsDev.DisposeAsync(); }); // Its refresh stops; the runtimes that borrowed it ended with their sessions.
                await CloseStepAsync(closing, "tool endpoints", () => toolEndpoints.DisposeAsync()); // The agents of the providers of plugins ended with their sessions.
                await CloseStepAsync(closing, "provider sign-in", async () => { if (providerLogin is not null) await providerLogin.CloseAsync(); }); // A running sign-in is canceled and joined.
                await CloseStepAsync(closing, "provider usage", () => { providerUsage?.Dispose(); return ValueTask.CompletedTask; });
                await CloseStepAsync(closing, "speech", async () => { if (speech is not null) await speech.CloseAsync(); }); // A download of a speech model stops, and the speech worker ends.
                await CloseStepAsync(closing, "Claude Code CLI", async () => { if (claudeCli is not null) await claudeCli.CloseAsync(); }); // So does a download of the Claude Code CLI.
                await CloseStepAsync(closing, "search", async () => { if (search is not null) await search.DisposeAsync(); }); // The indexer stops at the end of a line, and the index is closed.
                await CloseStepAsync(closing, "MCP servers", async () => { if (mcpServers is not null) await mcpServers.CloseAsync(); }); // So is a running MCP authorization.
                await CloseStepAsync(closing, "plugin commands", async () => { if (pluginCommands is not null) await pluginCommands.CloseAsync(); }); // Plugin commands still waiting in a dialog end.
                await CloseStepAsync(closing, "canvases", () => { canvases?.Dispose(); return ValueTask.CompletedTask; }); // The tabs of plugins end with the application.
            });
            }
            if (configRecovery.IsReady) StartHost();
            // The window's own parts do not wait for the host: the view exists and shows the start-up screen
            // (the logo on the window's theme) as soon as the browser is ready.
            var creatingEnvironment = application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes =
                [
                    NeoCustomScheme.Application("app", new DesktopStartupResources(new NeoManifestResourceProvider(assets, manifest), startupStatus, canvases?.Modules)),
                    // Not the application: each origin of the scheme is one of its own, which the bridge does not trust.
                    NeoCustomScheme.Create(DesktopMcpAppViews.Scheme, mcpAppViews),
                ],
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
            // Where NeoAstra takes the files dropped from the file manager away from the page, the page is told of them.
            fileDropsLifetime = fileDrops.Follow(chrome.Services.DragDrop, shell.NotifyDrop, DesktopFileDrops.PageGetsDrops,
                label => OperatingSystem.IsWindows() && application.TryGetView(label, out var dropped) && dropped?.Window is { } owner ? owner.ScaleFactor : 1,
                () => shell.Zoom / 100d);
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
            windows.AttachMain(window, view);
            // A window on a display that is gone goes to the primary one; one pinned to a display that came back goes to it.
            application.DisplaysChanged += (_, changed) => windows.OnDisplaysChanged(changed.Displays);
            // The kept zoom, before the first document: the window never shows at another one. Every window has the same.
            view.ZoomFactor = shell.Zoom / 100d;
            shell.ApplyZoom = windows.ApplyZoom;
            view.NavigationRequested = request =>
            {
                if (request.IsMainFrame && IsApplicationDocument(request.Uri)) retention?.BeforeMainFrameNavigation(request.Uri);
                return ValueTask.FromResult(DecideNavigation(request));
            };
            view.ProcessFailed += (_, _) => retention?.ProtectionRpc.InvalidatePage();
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            // The files attached to a prompt are chosen with the dialog of the system.
            view.FileChooserRequested = request => DesktopFileChooser.ChooseAsync(window, shell.Dialogs, request,
                problem => shell.NotifyDialogProblem(Windows.DesktopWindows.MainLabel, "files", problem));
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
            // A monitor the main window is pinned to, in Settings > Windows, wins when it is connected.
            windowState = await DesktopWindowState.StartAsync(application, window, options.DataRoot, windows.MainPinnedDisplayId, chrome.Services.SystemInfo);
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
            _windowShown = true;
            // The page's theme: kept for the next start, and the window controls and background follow it now.
            // Two reports may cross: the window takes the one kept last, whichever posted first.
            var appearances = new DesktopAppearanceKeeper(options.DataRoot, appearance);
            void RememberAppearance(DesktopAppearance remembered)
            {
                appearances.Remember(remembered);
                application.Dispatcher.Post(() =>
                {
                    if (startedWindow.IsClosed) return;
                    // The title bar, and what each window shows where the page has not drawn yet, such as what a resize uncovers.
                    windows.ApplyAppearance(appearances.Latest);
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
                        // The entry of the desktop is one for the computer; the application starts again on its profile.
                        if (!DesktopUpdateInstaller.Start(options.DataRoot, options.IntegrationRoot ?? options.DataRoot, updateLauncher!, updateDotnet!,
                            CodeAltaNuGetUpdateChecker.UpdateArguments(AppUpdateService.PackageId, prerelease), DesktopCommandLine.RelaunchArguments(options))) return false;
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
                var recoveryBuilder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions
                {
                    ContractHash = NeoRpcGeneratedContract.Hash, Release = true,
                    DiagnosticSink = new DesktopRpcDiagnostics(), ErrorMappers = [new DesktopRpcExceptionLog()], InvocationTimeout = DesktopRpcDiagnostics.InvocationTimeout,
                }));
                chrome.AddHandlers(recoveryBuilder);
                recoveryBuilder.AddBootService(new BootService { ConfigRecovery = true, Developer = options.Developer, RememberAppearance = RememberAppearance });
                recoveryBuilder.AddStartupConfigService(new StartupConfigService(configRecovery)
                {
                    Continue = () => repaired.TrySetResult(), Exit = () => shell.RequestExit(confirmed: true),
                });
                recoveryBuilder.AddDesktopShellService(new DesktopShellService(shell));
                // The configuration editor is in the main window alone.
                recoveryBuilder.AddWindowsService(new WindowsService());
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
            startupStatus.Text = "Preparing the window…";
            DesktopPlugins.LogStartupDiagnostics(host.PluginRuntime);
            if (DesktopPlugins.DescribeStartupFailures(host.PluginRuntime.Diagnostics) is { } pluginFailures) pluginUi.NotifyProblem(pluginFailures);
            // A session whose background job runs is at work too: exiting ends the command.
            shell.RunningSessions = host.RuntimeService.CountSessionsAtWork;
            sessionTaskbarProgress = DesktopWindowsTaskbarProgress.StartIfAvailable(window, chrome.Services.WindowPolish, host.RuntimeService);
            // A question a session asked, a command to review, a form to fill, a conversation open elsewhere.
            // A send of the fleet a budget holds until its owner continues it waits for a person too.
            ValueTask<IReadOnlyList<string>> SessionsWaitingAsync() => host.ListSessionsWaitingForPersonAsync();
            // A session that is done or waits for the user while the application is in the background: a notification,
            // which opens the session, and the mark of the application, until a window has the focus again.
            var alertSurface = new DesktopTurnAlertSurface(window, chrome.Services, host.RuntimeService,
                async _ => await SessionsWaitingAsync(), () => shell.TurnAlerts, () => windows.AnyFocused,
                DesktopTurnAlertSurface.CanNotify(OperatingSystem.IsMacOS() ? DesktopIntegration.MacRunningBundleIdentifier() : null));
            sessionTurnAlerts = new DesktopTurnAlerts(alertSurface, DesktopTurnAlerts.Period);
            // A use of a secret that asks before use waits for the user while the application is in the background: a notification,
            // which brings the window and its prompt back.
            // The prompt of the page reads them again at each change.
            var secretPrompts = host.Credentials is { } promptedCredentials ? new DesktopSecretPrompts(alertSurface, promptedCredentials) : null;
            if (host.Credentials is { } watchedCredentials) watchedCredentials.PendingUsesChanged += (_, _) => shell.NotifySecretsPending();
            chrome.Services.Notifications.Activated += (_, activation) =>
            {
                if (DesktopTurnAlerts.SessionOf(activation) is { } notifiedSession) shell.ShowSession(notifiedSession);
                else if (DesktopSecretPrompts.IsPrompt(activation, out var askingSession))
                {
                    if (askingSession is not null) shell.ShowSession(askingSession);
                    else shell.Show();
                }
            };
            shell.HasWorkspace = true;
            if (!closeRequested.Task.IsCompleted)
            {
                var epoch = Guid.NewGuid().ToString("D");
                // The hosts of the sessions the window shows: the host of this process, and one per paired machine, which the
                // fleet of the instance adds and removes.
                var sessionHosts = new SessionHosts.SessionHostRegistry(new SessionHosts.LocalSessionHost(host));
                // The paired machines, the sessions agents start on them and the CodeAlta Servers of this instance: wired here,
                // started once the window shows the application, with no key and no network for a profile that stays local.
                // The model host of this computer: its node API offers its servers to the paired computers too. An instance on
                // explicit roots (automation, tests) runs none and downloads nothing.
                var localModelHost = roots.Home is null ? CodeAlta.Fleet.FleetModelHosts.Create(host.CatalogOptions, serversOutliveHost: false) : null;
                var fleetStartup = Fleet.DesktopFleetStartup.Create(host, sessionHosts, Environment.MachineName, DesktopCommandLine.Version, privateNetworks: privateNetworks,
                    modelServers: localModelHost);
                fleet = fleetStartup.Fleet;
                remoteChildren = fleetStartup.RemoteChildren;
                fleetServers = fleetStartup.Servers;
                desktopNode = fleetStartup.Node;
                // The model servers of this computer, and those of the paired machines. An instance on explicit roots
                // (automation, tests) runs none and downloads nothing.
                modelServers = new ModelServers.DesktopModelServers(localModelHost, fleet, modelServerTransports, new CodeAlta.Fleet.ModelServerProviderRefresh(host, null));
                modelServers.SaveLocalProvider = async (key, name, server, token) =>
                {
                    var store = new CodeAltaConfigStore(host.CatalogOptions);
                    var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true).ToList();
                    var definition = definitions.FirstOrDefault(existing => string.Equals(existing.ProviderKey, key, StringComparison.OrdinalIgnoreCase));
                    if (definition is null) definitions.Add(definition = new CodeAltaProviderDocument { ProviderKey = key, ProviderType = "llama-cpp" });
                    definition.ProviderType = "llama-cpp";
                    definition.DisplayName = name;
                    definition.Server = server;
                    definition.ApiUrl = null;
                    definition.Enabled = true;
                    store.SaveGlobalProviderDefinitions(definitions);
                    GlobalConfigService.ApplyProviders(store, host.ModelProviderRegistry, options.CatalogRoot!, pluginProviderTypes, modelsDev);
                    await host.ModelProviderInitializationService.RefreshProviderAfterChangeAsync(new CodeAlta.Agent.ModelProviderId(key), token).ConfigureAwait(false);
                };
                var startingModelServers = modelServers;
                _ = Task.Run(async () =>
                {
                    try { await startingModelServers.StartAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                });
                // The bookmarks of the sessions of this instance, in its database. A bookmarked session is kept by the
                // retention of sessions, which deletes nothing before the bookmarks were read.
                var bookmarks = new CodeAlta.Catalog.Bookmarks.BookmarkService(host.ApplicationDatabase,
                    new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()));
                bookmarkLife = new DesktopBookmarks(bookmarks, host.RetentionProtection, host.RuntimeService);
                retention = new DesktopSessionRetention(host, epoch);
                // The git worktrees sessions can work in: created with a session, listed and removed from the changes of a project.
                var worktreeConfig = new CodeAltaConfigStore(host.CatalogOptions);
                var worktrees = new CodeAlta.Catalog.Worktrees.GitWorktreeService(host.CatalogOptions, worktreeConfig);
                workspace = new WorkspaceService(sessionHosts, host, epoch, worktrees, fleet.Locations);
                // The work the first page does not need waits until the window shows the application.
                var shown = new DesktopStartupGate();
                workspace.Served = shown.WorkspaceServed;
                // Until then, a list of sessions rebuilt from the journals (the first start, after a repair) is counted on the start-up screen.
                var journalStore = host.SessionViewCatalog.JournalStore;
                journalStore.RebuildProgress = (done, total) => startupStatus.Text = $"Rebuilding the session list… {done:N0} of {total:N0}";
                _ = shown.Shown.ContinueWith(_ =>
                {
                    journalStore.RebuildProgress = null;
                    Mark("application shown, the work after the start begins");
                }, TaskScheduler.Default);
                // The paired machines are reached once the window shows the application.
                _ = shown.Shown.ContinueWith(_ => fleetStartup.StartRemoteChildren(), TaskScheduler.Default);
                _ = shown.Shown.ContinueWith(_ => fleetStartup.StartAsync(CancellationToken.None), TaskScheduler.Default).Unwrap();
                operations = new SessionOperationsService(sessionHosts, host.Commands, epoch) { RemoteControlChanged = shell.NoteRemoteControl };
                if (shell.ReconnectRemoteControl) DesktopRemoteControl.Reconnect(host.Commands, shell);
                // The reviews of the user: comments on plans, files and changes, kept per checkout in the state folder.
                var reviewService = new CodeAlta.Catalog.Reviews.ReviewService(host.CatalogOptions.StateRoot);
                workspace.ProjectDeleted = projectId => reviewService.DeleteProject(projectId);
                // A session that reviews before commit may commit the work tree its last review approved.
                host.RuntimeService.Reviews = reviewService;
                asks = new SessionAsksService(sessionHosts, epoch, reviewService, host.ProjectCatalog);
                pluginAsks.Attach(host.Commands.Asks, asks, epoch);
                pluginPermissions.Attach(host.RuntimeService.Permissions);
                reminders = new ReminderService(host.WorkspaceReads, host.Commands, host.RuntimeService, epoch, host.RetentionProtection);
                changesView = new DesktopChangesView();
                // The space the window shows, and what a command asks of it. A catalog that never had spaces
                // gets its first ones before the page reads them.
                var spaceView = new DesktopSpaceView();
                try { await host.SpaceCatalog.SeedAsync(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The window then has the default space alone. */ }
                editorView = new DesktopEditorView();
                // A request goes to the window that has its tab, else to the one of the session that asked, else to the main one.
                changesView.Router = (tab, session) => windows.Route(tab, null, session);
                editorView.Router = windows.Route;
                // Each window shows its space; what is "the space the window shows" is the one of the window used last.
                spaceView.FocusedLabel = () => windows.FocusedLabel;
                spaceView.ShownChanged += windows.SetShownSpace;
                // The sessions of the tabs of the secondary windows are protected before their pages claim them.
                retention.ProtectionRpc.HostHeld = windows.SecondarySessions;
                // A session open in a tab holds its lease; one no tab shows lets it go once it is idle.
                spaceView.OpenSessionsChanged += host.RuntimeService.SetShownSessions;
                // A session another process wrote, runs or released: the windows read the sessions again.
                if (host.ExternalSessions is { } externalSessions) externalSessions.Changed += (_, _) => spaceView.NotifySessionsChanged();
                // A paired machine that changes its sessions or its state: the windows read the sessions again.
                fleet.Changed += spaceView.NotifySessionsChanged;
                canvases?.Attach(host.PluginRuntime, () => spaceView.ShownSpaceId);
                // The project the catalog lists for a folder; null for none.
                async Task<string?> ProjectIdOfAsync(string projectPath, CancellationToken token)
                {
                    var full = Path.GetFullPath(projectPath);
                    return (await host.ProjectCatalog.LoadAsync(token).ConfigureAwait(false))
                        .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.ProjectPath)
                            && string.Equals(Path.GetFullPath(candidate.ProjectPath), full, StringComparison.OrdinalIgnoreCase))?.Id;
                }

                // A plugin works for a session under the identity of its space, and for the window under the one shown,
                // with the CLI profiles of the project it works on.
                pluginIdentities.Attach(
                    (sessionId, token) => sessionId is null
                        ? host.Identities.ResolveSpaceAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, token)
                        : host.RuntimeService.GetSessionIdentityAsync(sessionId, token),
                    async (projectPath, token) => await host.Identities.ResolveAsync(null, await ProjectIdOfAsync(projectPath, token).ConfigureAwait(false), token).ConfigureAwait(false),
                    async (projectPath, token) => await host.Identities.ResolveAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId,
                        await ProjectIdOfAsync(projectPath, token).ConfigureAwait(false), token).ConfigureAwait(false));
                terminals = new Terminals.DesktopTerminals(DesktopCommandLine.Version, Path.Combine(options.DataRoot, "terminal"))
                {
                    Network = options.Network is { Sandboxed: false } network ? network.Proxy.ApplyTo : null,
                    // A terminal of the window has the identity of the space it shows, with the CLI profiles of the project it
                    // is listed under; one a session opens, the environment of the session's commands.
                    Identity = request => request.Agent && request.SessionId is { } owner
                        ? host.RuntimeService.GetSessionProcessEnvironmentAsync(owner).GetAwaiter().GetResult()
                        : host.Identities.ResolveAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, request.ProjectId).GetAwaiter().GetResult().Environment,
                };
                shell.BusyTerminals = () => terminals.Busy;
                // The definitions are shared with every instance; what ran is this instance's. The developer
                // instance starts paused, so that it does not run again what the normal one already runs.
                automations = new Automations.AutomationService(host.CatalogOptions.ConfigPath, token => host.ProjectCatalog.LoadAsync(token),
                    new Automations.AutomationStateStore(Path.Combine(host.CatalogOptions.StateRoot, "automations.json"), pausedByDefault: options.Developer),
                    automationRunner = new Automations.AutomationRunner(host), TimeProvider.System, TimeZoneInfo.Local, new Automations.GitAutomationFeed(),
                    () => [.. (host.PluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueEventSource>()],
                    // A command runs with the identity of the space its runs go to (AutomationRunner): the space of its project.
                    new Automations.ShellAutomationCommands(projectId => host.Identities.ResolveAsync(null, projectId).GetAwaiter().GetResult().Environment),
                    // An automation that runs a script is allowed with the script as the library holds it.
                    entry => entry.Definition.Script is { } script ? host.CodeModeScripts.FindHash(script, entry.ProjectPath) : null)
                {
                    // Jira and Slack, with the secrets of this computer, while the desktop runs; the hooks of a server go to its machines.
                    Integrations = Automations.AutomationIntegrationOptions.ForHost(host.CatalogOptions.GlobalRoot, host.Credentials),
                };
                // The triggers of CodeAlta, and those the plugins give, as they run now.
                Automations.AutomationBuiltInSources.AddProjectTrigger(automations, host.ProjectCatalog);
                automations.Sources.Plugins = () => host.PluginRuntime?.GetAutomationTriggers() ?? [];
                // An automation of the user can run on a paired machine: it is copied there, and runs there while this
                // computer is closed. A profile that placed none makes no call and creates no key.
                var placedFleet = fleet;
                // A machine of an organization this computer is not paired with gets it through its server, sealed for it.
                var placedServers = fleetServers;
                placement = new Fleet.DesktopAutomationPlacement(automations, fleet.Find, host.CatalogOptions.FleetRoot, () => placedFleet.DeviceId, placedFleet.SignAutomation)
                {
                    ServerRoute = placedServers is null ? null : placedServers.AutomationRoute,
                    // An automation that runs on the machines with some tags is given to every node of its server that has them.
                    PoolMachines = placedServers is null ? null : placedServers.PoolMachines,
                    SignRun = placedFleet.SignAutomationRun,
                    // Pausing every automation pauses those on machines too; a developer instance starts paused and leaves them.
                    PausesPlaced = !options.Developer,
                };
                automations.Placement = placement;
                // An automation of a team another maintainer gave is opened here in the editor, recorded as given at its version.
                if (placedServers is not null) placedServers.Handover = new Fleet.DesktopAutomationHandover(automations, token => host.ProjectCatalog.LoadAsync(token)) { Placement = placement };
                fleet.Changed += placement.Poke;
                if (placedServers is not null) placedServers.Changed += placement.Poke;
                // A host that has the user review the commands of its sessions lets no session type in a terminal, nor
                // give an automation a command to run.
                // A host that started its plugins builds one again while it runs, and tells the page when it did. Building
                // and loading a plugin runs code: a host that has the user review the commands of its sessions does not let
                // a session do it with an alta command.
                if (pluginAlta is not null) host.PluginRuntime.Changed += (_, _) => shell.NotifyPluginsChanged();
                var pluginWorkshop = pluginAlta is null || options.ReviewOwnedCommandPermissions ? null : DesktopPlugins.Workshop(host.PluginRuntime, editorView);
                // The tasks and the plans of the projects: files of each project, and the sessions of this instance that proposed or run them.
                var workItems = new CodeAlta.Catalog.WorkItems.WorkItemService(worktreeConfig, host.CatalogOptions.StateRoot);
                // A plan that is deleted takes its review with it.
                workItems.PlanDeleted += (project, folder, path) => ReviewsService.ForgetPlan(reviewService, host.Commands.Asks, project, folder, path);
                // The user guide that ships beside the application: the Documentation tab shows it, and agents read it.
                var documentation = new CodeAlta.Catalog.Documentation.ShippedDocumentation(
                    new CodeAlta.Orchestration.Runtime.SystemPrompts.FileSystemPromptContentLocator().GetRoots(new()).ShippedUserGuideRoot);
                var documentationView = new DesktopDocumentationView { Router = (tab, session) => windows.Route(tab, null, session) };
                // The local search of the window: the sessions of this instance, the guide, the skills, the tools and the plugins.
                // Its indexer waits a little before its first pass, so it does not slow the start.
                search = Search.DesktopSearch.Start(host, searchRelay, DesktopPlugins.CreateJournalCatalog(host.CatalogOptions), documentation,
                    options.OtherToolsHome,
                    DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions, search: searchRelay, browser: browserHost, profileRoot: options.Profile),
                    options.ReviewOwnedCommandPermissions ? null : () => [.. ui.Tools.Select(static tool => (tool.Name, tool.Description))],
                    static line => LogManager.GetLogger("CodeAlta.Search").Info(line), skillUsage: _skillUsage, mcpHome: options.McpHome);
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
                // The runs of scripts are kept with the results of their steps: one CodeAlta ended during is resumed from its card.
                _ = host.KeepCodeModeRunsAsync();
                // A session recorded for a project in a folder that is gone follows the project to its new folder (its file names it).
                host.FollowMovedProjectFolders();
                // The settings as one catalog: the built-in ones bound to the stores their pages use, for alta settings and the search of Settings.
                Action? loggingSaved = null;
                // The settings of the plugins: their values in config.toml, their changes told to them.
                var pluginSettings = new Settings.DesktopPluginSettings(() => host.PluginRuntime.ActivePlugins, pluginSettingsStore);
                host.PluginRuntime.Changed += (_, _) => pluginSettings.OnPluginsChanged();
                var desktopSettings = new Settings.DesktopSettings(Settings.DesktopSettingsCatalog.Load(), Settings.DesktopSettingBindings.Create(new Settings.DesktopSettingSources
                {
                    Config = new CodeAltaConfigStore(host.CatalogOptions), Shell = shell, Windows = windows, Search = search.Host, WorkItems = workItems,
                    LoggingSaved = () => loggingSaved?.Invoke(),
                    ProxySaved = options.Network is { Sandboxed: false } savedNetwork ? () => savedNetwork.Refresh() : null,
                }))
                {
                    // Settings is in the main window: a request brings it to the front.
                    Router = _ => { shell.Show(); return Windows.DesktopWindows.MainLabel; },
                    Log = static line => LogManager.GetLogger("CodeAlta.Settings").Info(line),
                    Plugins = pluginSettings,
                };
                // The release notes of CodeAlta, read from GitHub for the life of the application: the window shows them, and
                // `alta release-notes` reads them; the dialog opens through the requests of Settings to the page.
                var releaseNotes = new Rpc.DesktopReleaseNotes(new CodeAltaReleaseNotes(DesktopCommandLine.Version), DesktopCommandLine.Version, desktopSettings);
                var altaCommands = DesktopAltaTools.Attach(host, reminders.Reminders, pluginAlta, changesView, editorView,
                    new DesktopAltaTerminals(terminals, acceptsInput: !options.ReviewOwnedCommandPermissions),
                    new DesktopAltaAutomations(automations, host.ProjectCatalog, acceptsCommands: !options.ReviewOwnedCommandPermissions, placement,
                        fleetServers is { } waitingServers ? waitingServers.WaitingRunsAsync : null), worktrees, pluginWorkshop, workItems, new DesktopAltaAppearance(shell), spaceView,
                    // A command a session starts in the background, what it types in a terminal, the command it gives an
                    // automation and the plugin it builds are reviewed by nobody: only a session whose requests are all
                    // approved does any of it. A caller that is no session (a client of the MCP server) has the policy
                    // of the application.
                    new AltaCommandReviewPolicy(AcceptsCommands: !options.ReviewOwnedCommandPermissions)
                    {
                        AcceptsCommandsOf = sessionId => host.RuntimeService.GetPermissionPolicy(sessionId ?? string.Empty) == SessionPermissionPolicy.Approve,
                    },
                    // The tabs plugins provide: only a host that started its plugins has them.
                    canvases is null ? null : new DesktopAltaCanvases(canvases), documentation, documentationView, bookmarks, search, reviewService,
                    new RuntimeAltaReviewPolicy(host.RuntimeService), new Windows.DesktopAltaWindows(windows), desktopSettings,
                    // `alta session move`: the sessions of this computer and of its paired machines go from one to another.
                    fleet?.Moves is { } fleetMoves ? new Fleet.DesktopAltaMachineMoves(fleetMoves, fleet, host) : null,
                    // `alta fleet`: the machines, the servers, the moves and where the automations run, as the window shows them.
                    fleet is null ? null : new Fleet.DesktopAltaFleet(fleet, fleet.Moves?.MachineName ?? Environment.MachineName, DesktopCommandLine.Version)
                    {
                        Servers = fleetServers, Moves = fleet.Moves, Automations = automations, Placement = placement, Host = host,
                    }, releaseNotes,
                    // `alta llama`: the llama.cpp servers of this computer and of the machines it manages.
                    modelServers is null ? null : ModelServers.DesktopAltaModelServers.Of(modelServers, host.RuntimeService),
                    // `alta provider refresh`: the providers of config.toml, applied again as Save and apply does.
                    new DesktopAltaProviderConfiguration(new CodeAltaConfigStore(host.CatalogOptions), host.ModelProviderRegistry, options.CatalogRoot!,
                        pluginProviderTypes, modelsDev, () => _copilotPolicies?.RefreshSoon()));
                // A script of an automation without a model runs with these commands, as the automation allows them.
                if (automationRunner is not null) automationRunner.Unattended = DesktopUnattendedScripts.Create(altaCommands, host);
                search.AttachCommands(altaCommands);
                search.AttachSettings(desktopSettings);
                projectOpening.Attach(async arguments => await altaCommands.InvokeAsync(arguments).ConfigureAwait(false));
                // The clients of the MCP server run the same commands, as callers that belong to no session.
                Volatile.Write(ref altaTool, Mcp.DesktopMcpTools.Alta(altaCommands, roots.Project, shell.NotifySessionsChanged));
                uiSessions.WorkFolder = (sessionId, token) => SessionFolderAsync(host, sessionId, token);
                // A session whose commands the user reviews does not drive the window, where the review is answered.
                uiSessions.Reviewed = sessionId => host.RuntimeService.GetPermissionPolicy(sessionId) != SessionPermissionPolicy.Approve;
                // Nor does a session without the shell: the window has terminals and the settings that turn the shell back on.
                uiSessions.ShellTurnedOff = async (sessionId, token) =>
                    (await host.RuntimeService.GetDisabledToolsAsync(sessionId, token).ConfigureAwait(false)).Contains(CodeAlta.Orchestration.Runtime.SystemPrompts.PromptToolConditions.ShellCommand);
                // Nor does a sandboxed session: the window is outside its sandbox.
                uiSessions.Sandboxed = async (sessionId, token) => (await host.RuntimeService.GetSessionSandboxAsync(sessionId, token).ConfigureAwait(false)).IsSandboxed;
                // The browser of a session works in its folder, keeps to its sandbox, asks its user on its card, and never
                // reaches the ports of CodeAlta.
                browserHost.WorkFolder = (sessionId, token) => SessionFolderAsync(host, sessionId, token);
                browserHost.Sandbox = async (sessionId, token) => DesktopBrowser.Sandbox(await host.RuntimeService.GetSandboxReachAsync(sessionId, token).ConfigureAwait(false));
                browserHost.RequestPermission = async (sessionId, request, token) => await host.RuntimeService.Permissions.RequestForSessionAsync(sessionId, request, token).ConfigureAwait(false);
                // fill_login types a sign-in the user shared for the site of the tab, after the card of the session.
                browserHost.SignIn = (sessionId, origin, signInId, token) =>
                    DesktopSignIns.FillAsync(host.Credentials, host.RuntimeService.Permissions.RequestForSessionAsync, sessionId, origin, signInId, token);
                var mcpPort = Mcp.DesktopMcpEndpoint.For(options).Port;
                // The port it listens on is read at each check: it is another one when the one asked for was taken, and
                // after a restart of the server.
                browserHost.ReservedPorts = () => DesktopBrowser.ReservedPorts(mcpPort, mcp?.ListeningPort, mcpPorts.Current());
                // The UI tools and the browser tools have the same names: a session has one set or the other.
                browserHost.Activated = sessionId => uiSessions.Set(sessionId, active: false);
                uiSessions.Activated = browserHost.ReleaseTools;
                automations.Start();
                placement.Start();
                workspacePrepared.TrySetResult();
                {
                    // Leave room for ordinary pasted images and their base64/JSON overhead.
                    // Owned-only host-wide inbound UTF-8 framing cap, not a per-image/response limit.
                    var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions
                    {
                        ContractHash = NeoRpcGeneratedContract.Hash, Release = true, MaximumFrameBytes = 128 * 1024 * 1024,
                        // A channel the limit refuses is refused after its handler ran, so the page cannot send it again:
                        // the limit is far above what the page opens (the panes, the observations, the retention lifetime).
                        // Two items wait for the page's acknowledgement at most: a slow page holds its stream back
                        // instead of letting the items pile up in the host.
                        MaximumChannelsPerSession = 512, MaximumUnacknowledgedChannelItems = 2,
                        // The page waits for every call, so the calls it makes while the host is busy add up instead of
                        // timing out: up to 32 open session panes and their observations must all wait, never be refused.
                        MaximumConcurrentInvocations = 4096, MaximumConcurrentInvocationsPerSession = 2048,
                        // Every pane reads the same commands: a command is not held to the eight calls at once NeoAstra
                        // gives one by default, which a busy host with many tabs passes.
                        DefaultCommandMaximumConcurrency = 1024,
                        // The page is the application's own: when a busy host runs again, the frames it queued meanwhile are
                        // admitted at once. The bounds that protect from a page that floods would refuse them, and close the
                        // session of the page after enough refusals.
                        RequestRatePerSecond = 100_000, RequestRateBurst = 1_000_000, AbuseClosureThreshold = 10_000,
                        DiagnosticSink = new DesktopRpcDiagnostics(),
                        // The exception of a command that failed is logged: the page only gets internal_error.
                        ErrorMappers = [new DesktopRpcExceptionLog()], InvocationTimeout = DesktopRpcDiagnostics.InvocationTimeout,
                    }));
                    chrome.AddHandlers(builder);
                    var configStore = new CodeAltaConfigStore(catalog);
                    builder.AddStorageService(new StorageService(catalog, epoch, host.StartupScratchpadCleanup, retention.Service));
                    // The proxy: saved, applied at once, and checked through settings not saved yet.
                    builder.AddNetworkService(options.Network is { Sandboxed: false } ownedNetwork
                        ? new NetworkService(catalog, ownedNetwork, epoch)
                        {
                            SecretChanged = () => host.AgentHub.SecretMasker?.Refresh(), RestartAdvised = () => ownedNetwork.Proxy.RestartAdvised,
                        }
                        : new NetworkService());
                    // The window can always answer a request: the setting changes while the host runs, and a send
                    // started with it on goes on asking after it is turned off.
                    builder.AddBootService(new BootService(epoch, commandReview: true, options.EnableOwnedUserInput, options.Developer)
                    {
                        RememberAppearance = RememberAppearance, ProviderSetup = NeedsProviderSetup(configStore), Closing = closing,
                    });
                    builder.AddDesktopShellService(new DesktopShellService(shell) { ForceExitHost = () => ForceExit(closing), Drops = fileDrops });
                    // The windows: which one a page is, the tabs it holds, the tabs it moves or drags to another one.
                    builder.AddWindowsService(new WindowsService(windows, () => application.GetWindowCapability(NeoWindowCapability.Placement).IsSupported));
                    builder.AddColorSchemesService(new ColorSchemesService(options.CatalogRoot!));
                    // The one of the configuration editor when the application started with it.
                    appUpdate ??= StartAppUpdate();
                    builder.AddAppUpdateService(appUpdate);
                    builder.AddWorkspaceService(workspace);
                    var machinesService = fleet is null ? new MachinesService() : MachinesService.Create(fleet, epoch, host.ProjectCatalog);
                    // This computer as a machine of the person's other computers: visible to them, its pairing links and its paired computers.
                    machinesService.Node = desktopNode;
                    if (fleet is not null && fleetServers is not null)
                    {
                        // A machine this computer manages joins a server this computer owns, and is approved here.
                        machinesService.ServerAddress = linkId => fleetServers.Connections.FirstOrDefault(connection => connection.Link.Id == linkId)?.Link.ShareableUrl;
                        // A paired machine that is not on a server of this computer yet joins it from its page, or at the end of a setup over SSH.
                        machinesService.ServersToJoin = fleetServers.ServersToJoin;
                        // A machine of a team or of the organization lets in who its signed access document names.
                        machinesService.OwnerOfMachine = fleetServers.OwnerOfMachineAsync;
                        // The names of the people a machine lets in, from the servers this computer is linked to.
                        machinesService.PersonNames = async token =>
                        {
                            var names = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var connection in fleetServers.Connections)
                            {
                                var people = await fleetServers.AccessCallAsync(connection.Link.Id, static (client, inner) => client.ListPeopleAsync(inner), token).ConfigureAwait(false);
                                foreach (var person in people.Items.Where(static person => person.Status == CodeAlta.Fleet.Protocol.FleetPersonStatuses.Active))
                                    names[person.Id] = person.DisplayName;
                            }

                            return names;
                        };
                        machinesService.CreateJoinCode = async (linkId, token) => (await fleetServers.CreateEnrollmentTokenAsync(linkId, TimeSpan.FromMinutes(10),
                            "Added from CodeAlta Desktop", token).ConfigureAwait(false)).Token;
                        machinesService.ApproveDevice = async (linkId, machine, token) => await fleetServers.ApproveMachineAsync(linkId, machine, token).ConfigureAwait(false);
                    }

                    builder.AddMachinesService(machinesService);
                    builder.AddModelServersService(modelServers is null ? new ModelServersService() : new ModelServersService(modelServers, epoch));
                    // A secret set here is masked at once in the sessions of the host.
                    builder.AddSecretsService(new SecretsService(new CodeAlta.Catalog.Secrets.LocalSecretStore(host.CatalogOptions.GlobalRoot), epoch,
                        () => host.AgentHub.SecretMasker?.Refresh())
                    {
                        Credentials = host.Credentials,
                    });
                    builder.AddFleetServerService(fleetServers is null ? new FleetServerService() : new FleetServerService(fleetServers, epoch)
                    {
                        Spaces = host.SpaceCatalog, IsPaired = machineId => fleet?.Find(machineId) is { IsPaired: true },
                        IsReachable = machineId => fleet?.Find(machineId) is not null, Fleet = fleet,
                        PersonName = CodeAlta.Desktop.Fleet.PersonName.ReadOnce(),
                        PrivateNetworks = privateNetworks,
                        ChooseSavePath = shell.ChooseSavePathAsync,
                    });
                    builder.AddConfigurationService(new ConfigurationService(host.ModelProviderRegistry, host.PluginRuntime, new DesktopDefaultProvider(host.CatalogOptions), host.CatalogOptions)
                    {
                        PluginProviderTypes = pluginProviderTypes,
                    });
                    var applicationLogs = new ApplicationLogsService(logCapture, configStore, epoch);
                    loggingSaved = applicationLogs.Reload;
                    builder.AddSettingsService(new SettingsService(desktopSettings, epoch));
                    builder.AddReleaseNotesService(new ReleaseNotesService(releaseNotes));
                    var globalConfig = new GlobalConfigService(configStore, host.ModelProviderRegistry, options.CatalogRoot!, epoch)
                    {
                        // The file may change the levels of the log: they are taken at once.
                        // A provider added, removed or changed in the file can change the policy that applies.
                        Saved = () => { applicationLogs.Reload(); _copilotPolicies?.RefreshSoon(); },
                        ProvidersChanged = () => _copilotPolicies?.RefreshSoon(),
                        SecretChanged = () => host.AgentHub.SecretMasker?.Refresh(),
                        ProviderStates = () => host.ModelProviderInitializationService.CurrentStates,
                        Spaces = host.SpaceCatalog,
                        SessionSpace = (sessionId, token) => host.RuntimeService.GetSessionSpaceAsync(sessionId, token),
                        PluginProviderTypes = pluginProviderTypes,
                        AbortProviderRuns = host.RuntimeService.AbortRunsOfProvidersAsync,
                        ModelCatalog = modelsDev,
                        RefreshProviderAfterSignIn = (key, token) =>
                            host.ModelProviderInitializationService.RefreshProviderAfterChangeAsync(new CodeAlta.Agent.ModelProviderId(key), token),
                    };
                    // A plugin that starts, stops or is built again brings or takes its provider types: the running turns
                    // of the providers that stop are stopped, then the providers are registered again.
                    host.PluginRuntime.Changed += (_, _) => _ = globalConfig.ApplyPluginProviderChangesAsync();
                    if (host.Credentials is { } credentials)
                    {
                        // A secret kept in a vault that can be given now (a vault unlocked, a secret bound, a use confirmed)
                        // brings the providers that were left out for want of it.
                        credentials.Changed += (_, change) => _ = globalConfig.ApplySecretsChangedAsync(change.Names);
                        // A plugin that starts can bring the type of a provider: the vaults are read again.
                        host.PluginRuntime.Changed += (_, _) => _ = credentials.RefreshAsync();
                        // The vaults that are locked are asked to unlock once, after the window is shown, in the normal instance only.
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            try
                            {
                                await credentials.UnlockAtStartAsync().ConfigureAwait(false);
                            }
                            catch (Exception exception) when (exception is not OutOfMemoryException)
                            {
                                LogManager.GetLogger("CodeAlta.Credentials").Warn($"The vaults were not unlocked at start: {exception.Message}");
                            }
                        });
                    }
                    builder.AddGlobalConfigService(globalConfig);
                    // The policy of the organization of a Copilot account is read again once it signs in or out.
                    providerLogin = new ProviderLoginService(configStore, globalConfig, options.CatalogRoot!, epoch, _ => _copilotPolicies?.RefreshSoon());
                    builder.AddProviderLoginService(providerLogin);
                    providerUsage = new ProviderUsageService(configStore, options.CatalogRoot!, epoch);
                    builder.AddProviderUsageService(providerUsage);
                    builder.AddManagedPolicyService(new ManagedPolicyService(host.RuntimeService.ManagedPolicies, _copilotPolicies, epoch));
                    builder.AddSandboxSettingsService(new SandboxSettingsService(configStore, host.RuntimeService, host.Commands, host.ProjectCatalog, host.SpaceCatalog, epoch));
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
                    mcpServers = new McpServersService(host.ProjectCatalog, epoch, options.McpHome);
                    builder.AddMcpServersService(mcpServers);
                    var agentPrompts = new AgentPromptsService(host.ProjectCatalog, epoch, userProfileRoot: options.OtherToolsHome);
                    builder.AddAgentPromptsService(agentPrompts);
                    // The standard launch has no explicit discovery home: common skills come from the profile, like the TUI. A
                    // separate profile reads none of the home.
                    var skills = new SkillsService(host.ProjectCatalog, host.SkillCatalog, options.OtherToolsHome, epoch)
                    {
                        // The skills MCP servers publish, beside the local ones.
                        Mcp = () => host.PluginRuntime?.ActivePlugins.Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugin.Mcp.McpPlugin>().FirstOrDefault(),
                        McpHome = options.McpHome,
                        Spaces = host.SpaceCatalog,
                    };
                    builder.AddSkillsService(skills);
                    builder.AddPluginsService(new PluginsService(host.ProjectCatalog, epoch, host.PluginRuntime)
                    {
                        ProviderTypes = pluginProviderTypes,
                        Sessions = host.RuntimeService.ListOverview,
                    });
                    builder.AddApplicationLogsService(applicationLogs);
                    providers = new ModelCatalogService(host.ModelProviderRegistry, host.ModelProviderInitializationService, epoch, new DesktopDefaultProvider(host.CatalogOptions), modelsDev);
                    _ = providers.StartInitialization(shown.Shown); // Retained and joined by providers.DrainAsync.
                    builder.AddModelCatalogService(providers);
                    builder.AddReminderService(reminders);
                    builder.AddSessionRetentionProtectionService(retention.ProtectionRpc);
                    builder.AddAutomationsService(new AutomationsService(automations, host.ProjectCatalog, epoch, host.CodeModeScripts, placement)
                    {
                        PublishTargets = fleetServers is { } publishing ? publishing.PublishTargetsAsync : null,
                        TeamAutomations = fleetServers is { } teams ? teams.TeamAutomationsAsync : null,
                        OpenTeamAutomation = fleetServers is { } opening ? opening.OpenTeamAutomationAsync : null,
                    });
                    var pullRequestPrompts = new CodeAlta.Catalog.PullRequests.PullRequestPromptCatalog(host.CatalogOptions);
                    builder.AddPullRequestPromptsService(new PullRequestPromptsService(pullRequestPrompts, host.ProjectCatalog, epoch));
                    var startPrompts = new CodeAlta.Catalog.StartPrompts.StartPromptCatalog(host.CatalogOptions);
                    builder.AddStartPromptsService(new StartPromptsService(startPrompts, host.ProjectCatalog, epoch));
                    builder.AddSpacesService(new SpacesService(host.SpaceCatalog, spaceView, host.RuntimeService.ListOverview, SessionsWaitingAsync, epoch, id => host.Commands.MoveSpaceChatsAsync(id, CodeAlta.Catalog.SpaceDescriptor.DefaultId)) {
                        Fallbacks = () => host.Commands.LastFallbacks,
                        MoveProjectSessions = host.Commands.MoveProjectSessionsAsync,
                        HeldElsewhere = () => host.ExternalSessions?.Held ?? [],
                        RemoteSessions = () => fleet is { } machines ? [.. machines.Hosts.SelectMany(static machine => machine.Listing.Sessions.Select(session => (machine.MachineId!, session)))] : [],
                        ConfigStore = new CodeAltaConfigStore(host.CatalogOptions),
                        SessionResources = new DesktopSessionResources(host, options.McpHome, DesktopPlugins.ForWindow(ui, uiSessions, options.ReviewOwnedCommandPermissions, search: searchRelay, browser: browserHost, profileRoot: options.Profile)).ReadAsync,
                    });
                    builder.AddLabelsService(new LabelsService(host.SpaceCatalog.Labels, spaceView, epoch));
                    builder.AddSubProjectsService(new SubProjectsService(host.ProjectCatalog, spaceView, epoch));
                    builder.AddSpaceIdentityService(new SpaceIdentityService(host.SpaceCatalog, (sessionId, token) => host.RuntimeService.GetSessionIdentityAsync(sessionId, token), epoch,
                        new Spaces.SpaceIdentityChecks(host.SpaceCatalog.Projects.Options.GlobalRoot, host.Credentials)));
                    var sessionStarter = new WorkItems.SessionStarter(host, worktrees, () => spaceView.ShownSpaceId);
                    builder.AddWorkItemsService(new WorkItemsService(workItems, host.ProjectCatalog, new WorkItems.WorkItemRunner(sessionStarter, workItems), epoch));
                    // A round the user submits on their own goes to a session as a prompt, queued when the session is busy.
                    builder.AddReviewsService(new ReviewsService(reviewService, host.ProjectCatalog, host.Commands.Asks, async (sessionId, text, token) =>
                    {
                        // A round of the review of the user: the session records it as such, sent or queued.
                        var sent = await altaCommands.InvokeAsync(["session", "send", sessionId, "--stdin", "--queue-if-busy"], stdin: text,
                            caller: AltaCallerIdentity.Host with { PromptProvenance = CodeAlta.Agent.AgentPromptProvenance.UserReview }, cancellationToken: token).ConfigureAwait(false);
                        return sent.ExitCode == 0 ? null : FirstLine(sent.Stderr.Length > 0 ? sent.Stderr : sent.Stdout);
                    }, sessionStarter, epoch));
                    // The Workflows tab: a script runs in a new session started from the window; Save as script asks the session of a run.
                    builder.AddWorkflowsService(new WorkflowsService(host.CodeModeScripts, host.CodeModeRuns, host.ProjectCatalog, sessionStarter, async (sessionId, text, token) =>
                    {
                        // The answer comes once the prompt is accepted, not when the turn it starts ends; a session at work gets it queued.
                        var admission = host.Commands.AdmitSend(new CodeAlta.Orchestration.Runtime.OwnedTextSendRequest("workflow-save:" + Guid.NewGuid().ToString("N"), sessionId, text),
                            CancellationToken.None);
                        if (admission.Kind is CodeAlta.Orchestration.Runtime.OwnedSessionCommandAdmissionKind.Accepted or CodeAlta.Orchestration.Runtime.OwnedSessionCommandAdmissionKind.Replay)
                            return null;
                        if (admission.Kind is not CodeAlta.Orchestration.Runtime.OwnedSessionCommandAdmissionKind.Busy)
                            return $"The prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).";
                        var queued = await altaCommands.InvokeAsync(["session", "send", sessionId, "--stdin", "--queue-if-busy"], stdin: text,
                            caller: AltaCallerIdentity.Host, cancellationToken: token).ConfigureAwait(false);
                        return queued.ExitCode == 0 ? null : FirstLine(queued.Stderr.Length > 0 ? queued.Stderr : queued.Stdout);
                    }, epoch, (project, token) => host.RuntimeService.GetSpaceFilterAsync(spaceView.ShownSpaceId, project, token),
                        // The tab of a run reads its records in the journal of its session, the one journal it reads.
                        async (sessionId, runId, token) =>
                            await host.RuntimeService.TryGetResumableSessionAsync(sessionId, token).ConfigureAwait(false) is { } session
                            && await host.RuntimeService.TryReadStoredHistoryAsync(session, token).ConfigureAwait(false) is { } history
                                ? [.. history.OfType<CodeAlta.Agent.AgentWorkflowEvent>().Where(record => record.WorkflowRunId == runId)]
                                : []));
                    // The local search: the queries of the search window and of the Search canvas, in the space the window shows.
                    builder.AddLocalSearchService(new LocalSearchService(search,
                        async (spaceId, token) => await host.RuntimeService.GetSpaceFilterAsync(spaceId, null, token).ConfigureAwait(false),
                        async (spaceId, token) => (await host.ProjectCatalog.LoadAsync(token).ConfigureAwait(false))
                            .Where(project => project.Spaces.Contains(spaceId, StringComparer.Ordinal)).Select(static project => project.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore()), epoch));
                    builder.AddBookmarksService(new BookmarksService(bookmarks,
                        BookmarksService.Sessions(host.WorkspaceReads), epoch, host.RetentionProtection));
                    // The trackers are the ones of the plugins that are active when the page asks: the Git plugin for the
                    // repository of a project, and plugins such as Jira for trackers that are elsewhere.
                    var pluginRuntime = host.PluginRuntime;
                    builder.AddIssuesService(new IssuesService(
                        () => [.. (pluginRuntime?.ActivePlugins ?? []).Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugins.Abstractions.IIssueTrackerSource>()],
                        host.ProjectCatalog, sessionStarter, epoch)
                    {
                        Views = new CodeAlta.Catalog.Issues.IssueViewStore(),
                        StartPrompts = startPrompts,
                    });
                    gitIssues = new GitIssuesService(host.ProjectCatalog, epoch,
                        async (projectId, token) => (await host.Identities.ResolveAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, projectId, token)
                            .ConfigureAwait(false)).Environment);
                    builder.AddGitIssuesService(gitIssues);
                    builder.AddSessionOperationsService(operations);
                    builder.AddSessionAsksService(asks);
                    builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, host.RuntimeService, epoch));
                    builder.AddSideQuestionService(new SideQuestionService(host.RuntimeService, epoch));
                    // A command of a session runs in its folder with the variables of its commands; one of the session to start,
                    // in the folder of its project with the identity of the space the window shows, as a terminal of the window.
                    builder.AddShellCommandsService(new ShellCommandsService(async (target, token) =>
                    {
                        if (target.SessionId is { } sessionId)
                            return await SessionFolderAsync(host, sessionId, token).ConfigureAwait(false) is { } folder
                                ? new ShellCommandPlace(folder, await host.RuntimeService.GetSessionProcessEnvironmentAsync(sessionId, token).ConfigureAwait(false)) : null;
                        if (await host.ProjectCatalog.GetByIdAsync(target.ProjectId!, token).ConfigureAwait(false) is not { Archived: false } project) return null;
                        var identity = await host.Identities.ResolveAsync(spaceView.ShownSpaceId ?? CodeAlta.Catalog.SpaceDescriptor.DefaultId, project.Id, token).ConfigureAwait(false);
                        return new ShellCommandPlace(project.ProjectPath, identity.Environment);
                    }, epoch));
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
                    builder.AddSettingsFilesService(new SettingsFilesService(host.ProjectCatalog, epoch, diskFolders, editorView, agentPrompts, skills, mcpServers, pullRequestPrompts)
                    {
                        StartPrompts = startPrompts,
                    });
                    // A link of the Markdown that the window shows: an address of the web for the system browser, a
                    // file for the code editor. A relative path starts from the folder the session works in.
                    // An address opens in the browser and the profile the space of the session or the window sets, or the project over it.
                    var linkBrowsers = new Browser.Links.LinkBrowsers();
                    var linkConfig = new CodeAltaConfigStore(host.CatalogOptions);
                    var linkOpener = new DesktopLinkOpener(host.SpaceCatalog, (folder, _) => Task.FromResult(linkConfig.LoadProjectLinks(folder)),
                        (sessionId, projectId, originLabel, token) => LinkOriginAsync(host, spaceView, sessionId, projectId, originLabel, token), linkBrowsers.Plan);
                    builder.AddSpaceLinksService(new SpaceLinksService(host.SpaceCatalog, linkBrowsers.Find, epoch));
                    builder.AddMarkdownLinksService(new MarkdownLinksService(epoch, DesktopLinks.Open, new DesktopFileLinks(host.ProjectCatalog, diskFolders, editorView,
                        (sessionId, token) => SessionFolderAsync(host, sessionId, token), projectFiles.RootAsync, DesktopLinks.Open,
                        roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    {
                        GuidePage = (file, anchor) => documentation.FindPage(file) is { } page && documentationView.Show(page, anchor),
                        ExternalEditors = externalEditors,
                    })
                    {
                        OpenFrom = linkOpener.OpenAsync,
                    });
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
                    // The cloud sessions of Claude Code: started in a terminal of the window, which is shown when Claude Code asks.
                    builder.AddCloudSessionsService(new CloudSessionsService(host.Commands, host.ProjectCatalog, host.ModelProviderRegistry,
                        () => host.ModelProviderInitializationService.CurrentStates, terminals, worktrees, epoch));
                    builder.AddMcpHostService(new McpHostService(mcp, McpTools, epoch));
                    builder.AddPromptImagesService(new PromptImagesService(host.WorkspaceReads, epoch));
                    builder.AddToolCallsService(new ToolCallsService(sessionHosts, epoch));
                    builder.AddMcpAppsService(new McpAppsService(host.WorkspaceReads.ReadHistoryRecordAsync,
                        () => host.PluginRuntime?.ActivePlugins.Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugin.Mcp.McpPlugin>().FirstOrDefault(), mcpAppViews, epoch)
                    {
                        SessionFolder = (sessionId, token) => SessionFolderAsync(host, sessionId, token),
                        SessionSpace = async (sessionId, token) => await host.RuntimeService.GetSessionSpaceIdAsync(sessionId, token).ConfigureAwait(false),
                    });
                    builder.AddComposerStatusService(new ComposerStatusService(host.ProjectCatalog, epoch, options.McpHome, host.PluginRuntime)
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
                    builder.AddSessionUserInputService(new SessionUserInputService(sessionHosts, epoch, options.EnableOwnedUserInput));
                    builder.AddSessionDisplayService(new SessionDisplayService(sessionHosts, epoch));
                    builder.AddSessionRuntimeStateService(new SessionRuntimeStateService(sessionHosts, epoch));
                    builder.AddSessionUsageService(new SessionUsageService(host.RuntimeService, host.WorkspaceReads, epoch));
                    // A request exists only for a send that was reviewed: it stays readable and answerable after the
                    // setting is turned off, until that send ends.
                    builder.AddSessionPermissionsService(new SessionPermissionsService(sessionHosts, epoch, static () => true)
                    {
                        // The skill of an MCP server a session asks to load, read for the user on the connection of that session.
                        ReadSkill = async (sessionId, server, uri, token) =>
                        {
                            if (host.PluginRuntime?.ActivePlugins.Select(static plugin => plugin.Instance).OfType<CodeAlta.Plugin.Mcp.McpPlugin>().FirstOrDefault() is not { } plugin)
                                return (null, false, "The MCP plugin does not run.");
                            var content = await plugin.ReadSkillContentAsync(new CodeAlta.Plugin.Mcp.McpSkillsRequest(null, null, sessionId), server, uri, token).ConfigureAwait(false);
                            return (content.Text, content.Verified, content.Error);
                        },
                    });
                    // A Claude Code conversation open in VS Code or a terminal: what the run waits for the user to choose.
                    builder.AddConversationHeldService(new ConversationHeldService(host.RuntimeService.ConversationHolds, epoch));
                    var rpc = builder.Build();
                    rpcLifetime = rpc;
                    // All session-work producers now share the original admission registry. This does
                    // not qualify desktop restoration: its authenticated lifetime/inventory handshake
                    // is still unknown until the application document has completed it.
                    DesktopSessionRetention.CompleteExternalRegistration(host, retentionProtection);
                    if (!closeRequested.Task.IsCompleted)
                    {
                        bindingLifetime = NeoRpcViewBinding.Bind(rpc, view);
                        // What the application document shows below the progress bar until the page has its workspace.
                        startupStatus.Text = "Loading the projects and sessions…";
                        var navigation = view.NavigateAsync(ApplicationDocument);
                        await navigation;
                        Mark("application loading");
                        shown.ApplicationLoading();
                        // From now on windows can be opened: the secondary ones are made as the main one is, and bound to the same host.
                        void ConfigureSecondaryView(NeoWindow secondary, global::NeoAstra.NeoAstra secondaryView)
                        {
                            DesktopWindowFocus.Follow(secondary, secondaryView, application.Dispatcher);
                            secondaryView.NavigationRequested = request => ValueTask.FromResult(DecideNavigation(request));
                            secondaryView.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
                            secondaryView.FileChooserRequested = request => DesktopFileChooser.ChooseAsync(secondary, shell.Dialogs, request,
                                problem => shell.NotifyDialogProblem(secondaryView.ViewLabel ?? Windows.DesktopWindows.MainLabel, "files", problem));
                            secondaryView.PermissionRequested = request => ValueTask.FromResult(DecidePermission(request, speech?.IsMicrophoneAllowed() == true));
                        }
                        windows.Attach(new Windows.DesktopWindowServices(environment, chrome, rpc, () => appearances.Latest, () => shell.Zoom / 100d, ConfigureSecondaryView));
                        // The secondary windows that were open when the application last ended come back where they were.
                        _ = windows.RestoreAsync();
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
            // A web view that cannot start (no WebView2 Runtime, no display) is said where the user started CodeAlta.
            if (!_windowShown) DesktopStartupFailure.Report(options, failure);
            // Shown, the window still has the start-up screen: it says why instead of waiting for nothing.
            else if (_startupStatus is { } status) status.Text = StartFailure(failure, Path.Combine(options.DataRoot, "logs"));
            bodyFailed = true;
            closing.Begin();
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
            GC.KeepAlive(sessionTurnAlerts);
            GC.KeepAlive(window);
            GC.KeepAlive(desktopWindows);
            return; // No native-resource disposal, lease release or ForceShutdown on this path.
        }
        // The host has ended: nothing runs that needs the computer awake.
        keepAwake?.Dispose();
        exitSignals?.Dispose();
        uiWatchdog?.Dispose();
        fileDropsLifetime?.Dispose();
        // The secondary windows are kept for the next start, then closed without asking their pages: their views and
        // bindings end before the RPC host does.
        if (desktopWindows is not null) await desktopWindows.SaveAndCloseForExitAsync();
        // The placement of the window for the next start, written while the window is still there.
        if (windowState is not null) await windowState.DisposeAsync();
        var nativeFailed = false;
        foreach (var resource in new[] { uiLifetime, bindingLifetime, viewLifetime, rpcLifetime, instanceLifetime, sessionTaskbarProgress, sessionTurnAlerts, chromeLifetime, environmentLifetime })
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
            GC.KeepAlive(sessionTurnAlerts);
            GC.KeepAlive(window);
            GC.KeepAlive(desktopWindows);
            return;
        }
        gitIssues?.Dispose(); // Its RPC host is gone: no lookup can still use the HTTP client.
        if (desktopWindows is not null) await desktopWindows.DisposeAsync();
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

    // Where a link was followed: the space of the session, else the space its window shows when the project of the
    // document is in it, else the space of that project, and the folder whose configuration may set where its links open.
    private static Task<DesktopLinkOrigin?> LinkOriginAsync(CodeAltaHost host, DesktopSpaceView spaceView, string? sessionId, string? projectId, string? originLabel, CancellationToken cancellationToken)
        => LinkOriginAsync(host.SpaceCatalog, host.ProjectCatalog,
            async (id, token) => await host.RuntimeService.GetSessionSpaceAsync(id, token).ConfigureAwait(false) is { } record ? (record.SpaceId, record.ProjectId) : null,
            (id, token) => SessionFolderAsync(host, id, token), spaceView, sessionId, projectId, originLabel, cancellationToken);

    internal static async Task<DesktopLinkOrigin?> LinkOriginAsync(
        CodeAlta.Catalog.SpaceCatalog spaceCatalog,
        CodeAlta.Catalog.ProjectCatalog projectCatalog,
        Func<string, CancellationToken, ValueTask<(string? SpaceId, string? ProjectId)?>> sessionSpace,
        Func<string, CancellationToken, ValueTask<string?>> sessionFolder,
        DesktopSpaceView spaceView,
        string? sessionId,
        string? projectId,
        string? originLabel,
        CancellationToken cancellationToken)
    {
        string? spaceId = null, folder = null;
        if (sessionId is not null)
        {
            if (await sessionSpace(sessionId, cancellationToken).ConfigureAwait(false) is not { } record) return null;
            (spaceId, projectId) = record;
            folder = await sessionFolder(sessionId, cancellationToken).ConfigureAwait(false);
        }

        var project = projectId is null || folder is not null ? null
            : (await projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (sessionId is null)
        {
            // The window chooses among the spaces of the project, or alone for a document of no project. A space the
            // project is not in (the default space shows the projects of the others) leaves it to the space of the project.
            var shown = (originLabel is null ? null : spaceView.ShownSpaceOf(originLabel)) ?? spaceView.ShownSpaceId;
            if (projectId is null || project?.Spaces.Contains(shown ?? string.Empty, StringComparer.Ordinal) == true) spaceId = shown;
        }

        if (projectId is null && spaceId is null) return null;
        folder ??= project?.ProjectPath;
        var space = await spaceCatalog.ResolveSessionSpaceAsync(spaceId, projectId, cancellationToken).ConfigureAwait(false);
        return new DesktopLinkOrigin(space.Id, folder);
    }

    // The folder a session works in: its git worktree while that folder is there, the folder of its project otherwise.
    private static async ValueTask<string?> SessionFolderAsync(CodeAltaHost host, string sessionId, CancellationToken cancellationToken)
        => (await host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Sessions
            .FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) is { } session
            ? !string.IsNullOrWhiteSpace(session.WorktreePath) && Directory.Exists(session.WorktreePath) ? session.WorktreePath : session.WorkspacePath
            : null;

    private async Task CloseOwnedHostWhenRequestedAsync(Task closeRequested, Task<CodeAltaHost> creation, DesktopClosing closing, Func<Task> closeImports)
    {
        // The close runs off the thread of the window: a step that waits must not hold the thread through which the
        // page is told of the close and asks to force it.
        await closeRequested.ConfigureAwait(false);
        closing.Begin();
        var host = await creation.ConfigureAwait(false);
        await closeImports();
        closing.Step("host");
        _hostDisposal = host.DisposeAsync().AsTask();
        try
        {
            await LogWhilePendingAsync(_hostDisposal, "the host");
            closing.Step(null);
            LogManager.GetLogger("CodeAlta.Desktop").Info("The host is closed.");
        }
        catch (Exception exception)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Error(exception, "The host could not be closed.");
            throw;
        }
    }

    // A step of the close that lasts is logged every 15 seconds while it waits: the log says what a close that never
    // ends waits for.
    private static async Task LogWhilePendingAsync(Task work, string name)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(15))) != work)
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"Closing the host: still waiting for {name} after {(int)clock.Elapsed.TotalSeconds} s.");
        await work;
    }

    // The page asked to end the process of a host whose close lasts: what it waits for is logged and written to the
    // file, then the process ends at once, as it does when the user ends it from outside. The operating system releases
    // the lease and the native resources; an ordinary close keeps them until its host is confirmed closed.
    private static bool ForceExit(DesktopClosing closing)
    {
        if (!closing.CanForceExit) return false;
        var state = closing.Snapshot();
        try
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn(
                $"Force exit asked by the page after {state?.Seconds} s of close, waiting for {state?.Step ?? "nothing"}: the process ends.");
            LogManager.Shutdown();
        }
        catch
        {
            // Nothing stops a force exit.
        }

        System.Diagnostics.Process.GetCurrentProcess().Kill();
        return true;
    }

    // One step of the close of the host: logged with its time, and its failure logged instead of ending the close.
    private static async Task CloseStepAsync(DesktopClosing closing, string name, Func<ValueTask> step)
    {
        var logger = LogManager.GetLogger("CodeAlta.Desktop");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        closing.Step(name);
        try
        {
            await LogWhilePendingAsync(step().AsTask(), name);
            logger.Info($"Closing the host: {name} closed in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception exception)
        {
            logger.Error(exception, $"Closing the host: {name} failed after {clock.ElapsedMilliseconds} ms; the close goes on.");
        }
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
            var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions
            {
                ContractHash = NeoRpcGeneratedContract.Hash, Release = true,
                DiagnosticSink = new DesktopRpcDiagnostics(), ErrorMappers = [new DesktopRpcExceptionLog()], InvocationTimeout = DesktopRpcDiagnostics.InvocationTimeout,
            }));
            chrome.AddHandlers(builder);
            builder.AddBootService(new BootService());
            // A copy of a catalog is browsed, not written: the schemes it holds are not offered.
            builder.AddColorSchemesService(new ColorSchemesService());
            builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));
            builder.AddConfigurationService(new ConfigurationService(options.CatalogRoot!));
            builder.AddGlobalConfigService(new GlobalConfigService());
            builder.AddStorageService(new StorageService());
            builder.AddNetworkService(new NetworkService());
            builder.AddProviderLoginService(new ProviderLoginService());
            builder.AddProviderUsageService(new ProviderUsageService());
            builder.AddManagedPolicyService(new ManagedPolicyService());
            builder.AddSandboxSettingsService(new SandboxSettingsService());
            builder.AddSpeechService(new SpeechService());
            builder.AddModelServersService(new ModelServersService());
            builder.AddClaudeCliService(new ClaudeCliService());
            builder.AddMcpServersService(new McpServersService());
            builder.AddAgentPromptsService(new AgentPromptsService());
            builder.AddSkillsService(new SkillsService());
            builder.AddPluginsService(new PluginsService());
            builder.AddSessionPluginEventsService(new SessionPluginEventsService());
            builder.AddPluginUiService(new PluginUiService());
            builder.AddCanvasesService(new CanvasesService());
            builder.AddDocumentationService(new DocumentationService());
            builder.AddSettingsService(new SettingsService());
            builder.AddProjectFilesService(new ProjectFilesService());
            builder.AddExternalEditorsService(new ExternalEditorsService());
            builder.AddSettingsFilesService(new SettingsFilesService());
            builder.AddProjectGitService(new ProjectGitService());
            builder.AddWorktreesService(new WorktreesService());
            builder.AddMcpHostService(new McpHostService());
            builder.AddTerminalsService(new TerminalsService());
            builder.AddCloudSessionsService(new CloudSessionsService());
            builder.AddAutomationsService(new AutomationsService());
            builder.AddWorkflowsService(new WorkflowsService());
            builder.AddWorkItemsService(new WorkItemsService());
            builder.AddReviewsService(new ReviewsService());
            builder.AddBookmarksService(new BookmarksService());
            builder.AddLocalSearchService(new LocalSearchService());
            builder.AddSessionRetentionProtectionService(new SessionRetentionProtectionService());
            builder.AddIssuesService(new IssuesService());
            builder.AddPullRequestPromptsService(new PullRequestPromptsService());
            builder.AddStartPromptsService(new StartPromptsService());
            builder.AddSpacesService(new SpacesService());
            builder.AddLabelsService(new LabelsService());
            builder.AddSubProjectsService(new SubProjectsService());
            builder.AddSpaceIdentityService(new SpaceIdentityService());
            builder.AddSpaceLinksService(new SpaceLinksService());
            builder.AddSecretsService(new SecretsService());
            builder.AddPromptImagesService(new PromptImagesService());
            builder.AddToolCallsService(new ToolCallsService());
            builder.AddMcpAppsService(new McpAppsService());
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
