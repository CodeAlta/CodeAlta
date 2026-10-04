using CodeAlta.Desktop.Rpc;
using System.Collections.Frozen;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
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
        => RunWithCapture(options, RunCore);

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

    internal static bool IsApplicationDocument(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "app" && uri.Host == "codealta" && uri.Port == -1 &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/index.html" && string.IsNullOrEmpty(uri.Query);

    private static int RunOwned(DesktopLaunchOptions options, DesktopLogCapture? capture)
    {
        var desktop = new DesktopApplication(options, capture);
        try
        {
            desktop._lease = CodeAltaSingleInstanceGuard.Acquire(Path.Combine(options.CatalogRoot!, "alta.lock"));
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
        GithubIssuesService? githubIssues = null;
        ModelCatalogService? providers = null;
        ProviderLoginService? providerLogin = null;
        WorkspaceService? workspace = null;
        NeoWindow? window = null;
        IAsyncDisposable? environmentLifetime = null, rpcLifetime = null, viewLifetime = null, bindingLifetime = null, chromeLifetime = null;
        var bodyFailed = false;
        try
        {
            window = application.CreateWindow(DesktopWindowChrome.WindowOptions());
            application.MainWindow = window;
            window.Closed += (_, _) => closed.TrySetResult();
            window.CloseRequested += request =>
            {
                if (!allowClose)
                {
                    request.Cancel();
                    operations?.CloseAdmission();
                    asks?.CloseAdmission();
                    reminders?.CloseAdmission();
                    providers?.CloseAdmission();
                    closeRequested.TrySetResult();
                    if (!shutdownUnconfirmed) window.Title = "CodeAlta — shutdown pending; lease retained";
                }
                return ValueTask.CompletedTask; // Never await host cleanup inside the native deadline.
            };
            window.Show();
            var catalog = new CatalogOptions { GlobalRoot = options.CatalogRoot! };
            _hostCreation = CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = options.CatalogRoot, CurrentProjectPath = roots.Project,
                DiscoveryScope = roots.Home is null || roots.Instructions is null
                    ? null
                    : new SessionDiscoveryScope(roots.Home, roots.Instructions),
                BuiltInSkillRoot = roots.Builtin,
                OwnedCommandReceiptCapacity = 256, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                ReviewOwnedCommandPermissions = options.ReviewOwnedCommandPermissions,
                AutoApproveOwnedPermissions = !options.ReviewOwnedCommandPermissions,
                EnableOwnedAsks = true,
                EnableOwnedUserInput = options.EnableOwnedUserInput,
                StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!),
            }, CancellationToken.None);
            // The retained application flow reacts even while a native acquisition is awaiting.
            // No native callback awaits this work; a close during host creation waits its actual result.
            _closeFlow = CloseOwnedHostWhenRequestedAsync(closeRequested.Task, _hostCreation, async () =>
            {
                await workspacePrepared.Task;
                if (workspace is not null) await Task.WhenAll(workspace.CloseImportsAsync(), workspace.CloseSessionsAsync());
                if (reminders is not null) await reminders.DisposeAsync();
                if (providers is not null) await providers.DrainAsync();
                if (providerLogin is not null) await providerLogin.CloseAsync(); // A running sign-in is canceled and joined.
            });
            await AwaitOwnedAsync(_hostCreation, window);
            var host = await _hostCreation;
            if (!closeRequested.Task.IsCompleted)
            {
                var epoch = Guid.NewGuid().ToString("D");
                workspace = new WorkspaceService(host, epoch);
                operations = new SessionOperationsService(host.Commands, epoch);
                asks = new SessionAsksService(host.Commands.Asks, epoch);
                reminders = new ReminderService(host.WorkspaceReads, host.Commands, epoch);
                workspacePrepared.TrySetResult();
                var assets = Path.Combine(AppContext.BaseDirectory, "assets");
                var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
                var creatingEnvironment = application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                {
                    UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                    CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
                });
                var environment = await creatingEnvironment;
                environmentLifetime = environment;
                if (!closeRequested.Task.IsCompleted)
                {
                    // Leave room for ordinary pasted images and their base64/JSON overhead.
                    // Owned-only host-wide inbound UTF-8 framing cap, not a per-image/response limit.
                    var chrome = await DesktopWindowChrome.StartAsync(application, options.DataRoot);
                    chromeLifetime = chrome;
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
                    builder.AddBootService(new BootService(epoch, options.ReviewOwnedCommandPermissions, options.EnableOwnedUserInput));
                    builder.AddWorkspaceService(workspace);
                    builder.AddConfigurationService(new ConfigurationService(host.ModelProviderRegistry, host.PluginRuntime));
                    var configStore = new CodeAltaConfigStore(catalog);
                    var globalConfig = new GlobalConfigService(configStore, host.ModelProviderRegistry, options.CatalogRoot!, epoch);
                    builder.AddGlobalConfigService(globalConfig);
                    providerLogin = new ProviderLoginService(configStore, globalConfig, options.CatalogRoot!, epoch);
                    builder.AddProviderLoginService(providerLogin);
                    builder.AddMcpServersService(new McpServersService(host.ProjectCatalog, epoch, roots.Home));
                    builder.AddAgentPromptsService(new AgentPromptsService(host.ProjectCatalog, epoch));
                    // The standard launch has no explicit discovery home: common skills come from the profile, like the TUI.
                    builder.AddSkillsService(new SkillsService(host.ProjectCatalog, host.SkillCatalog,
                        roots.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), epoch));
                    builder.AddPluginsService(new PluginsService(host.ProjectCatalog, epoch));
                    builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
                    providers = new ModelCatalogService(host.ModelProviderRegistry, host.ModelProviderInitializationService, epoch);
                    _ = providers.StartInitialization(); // Retained and joined by providers.DrainAsync.
                    builder.AddModelCatalogService(providers);
                    builder.AddReminderService(reminders);
                    githubIssues = new GithubIssuesService(host.ProjectCatalog, epoch);
                    builder.AddGithubIssuesService(githubIssues);
                    builder.AddSessionOperationsService(operations);
                    builder.AddSessionAsksService(asks);
                    builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, host.RuntimeService, epoch));
                    builder.AddSessionPluginEventsService(new SessionPluginEventsService(host.WorkspaceReads, host.ProjectCatalog, epoch));
                    builder.AddProjectFilesService(new ProjectFilesService(host.ProjectCatalog, epoch, host.ProjectFileSearchService));
                    builder.AddProjectGitService(new ProjectGitService(host.ProjectCatalog, epoch));
                    builder.AddSessionUserInputService(new SessionUserInputService(host.RuntimeService.Permissions, epoch, options.EnableOwnedUserInput));
                    builder.AddSessionDisplayService(new SessionDisplayService(host.RuntimeService.Display, epoch));
                    builder.AddSessionRuntimeStateService(new SessionRuntimeStateService(host.RuntimeService, epoch));
                    builder.AddSessionUsageService(new SessionUsageService(host.RuntimeService, epoch));
                    builder.AddSessionPermissionsService(new SessionPermissionsService(host.RuntimeService.Permissions, epoch, options.ReviewOwnedCommandPermissions));
                    var rpc = builder.Build();
                    rpcLifetime = rpc;
                    var creatingView = environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
                    var view = await creatingView;
                    viewLifetime = view;
                    if (!closeRequested.Task.IsCompleted)
                    {
                        view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                            IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
                        view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
                        bindingLifetime = NeoRpcViewBinding.Bind(rpc, view);
                        var navigation = view.NavigateAsync(new Uri("app://codealta/index.html"));
                        await navigation;
                    }
                }
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
        foreach (var resource in new[] { bindingLifetime, viewLifetime, rpcLifetime, chromeLifetime, environmentLifetime })
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
        githubIssues?.Dispose(); // Its RPC host is gone: no lookup can still use the HTTP client.
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
            await using var window = application.CreateWindow(DesktopWindowChrome.WindowOptions());
            application.MainWindow = window;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();

            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
            await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
            {
                UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
            });
            await using var chrome = await DesktopWindowChrome.StartAsync(application, options.DataRoot);
            var builder = new NeoRpcBuilder(chrome.Authorize(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true }));
            chrome.AddHandlers(builder);
            builder.AddBootService(new BootService());
            builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));
            builder.AddConfigurationService(new ConfigurationService(options.CatalogRoot!));
            builder.AddGlobalConfigService(new GlobalConfigService());
            builder.AddProviderLoginService(new ProviderLoginService());
            builder.AddMcpServersService(new McpServersService());
            builder.AddAgentPromptsService(new AgentPromptsService());
            builder.AddSkillsService(new SkillsService());
            builder.AddPluginsService(new PluginsService());
            builder.AddSessionPluginEventsService(new SessionPluginEventsService());
            builder.AddProjectFilesService(new ProjectFilesService());
            builder.AddProjectGitService(new ProjectGitService());
            builder.AddApplicationLogsService(new ApplicationLogsService(logCapture));
            builder.AddModelCatalogService(new ModelCatalogService());
            await using var rpc = builder.Build();
            window.Show();
            await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), DesktopWindowChrome.ViewOptions());
            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
            await using var binding = NeoRpcViewBinding.Bind(rpc, view);
            await view.NavigateAsync(new Uri("app://codealta/index.html"));
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
