using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Tui.Views;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

/// <summary>
/// Owns the existing shared host and models.dev refresh lifetime for the terminal frontend.
/// </summary>
/// <remarks>
/// Host-exposed services remain borrowed views. A prestarted plugin runtime remains Program-owned
/// and now survives this cleanup until Program's outer finally, after metadata cleanup.
/// Disposal is one cached best-effort operation; repeated/concurrent callers share its outcome.
/// Reentrant same-owner disposal is unsupported, and completion is not proof that all child work terminated.
/// </remarks>
internal sealed class CodeAltaOwnedServices : IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly Lazy<Task> _disposeTask;
    private readonly ModelProviderRegistry _modelProviderRegistry;
    private readonly IModelProviderInitializationService _modelProviderInitializationService;
    private readonly CodeAltaConfigStore _configStore;
    private readonly List<ModelProviderDescriptor> _providerDescriptors;
    private readonly ModelsDevCatalogService _modelsDevCatalogService;

    private CodeAltaOwnedServices(
        bool ownsLogging,
        CodeAltaHost host,
        CodeAltaConfigStore configStore,
        ModelsDevCatalogService modelsDevCatalogService,
        PluginHostBridge pluginHostBridge,
        List<ModelProviderDescriptor> providerDescriptors)
    {
        _host = host;
        _modelProviderRegistry = host.ModelProviderRegistry;
        _modelProviderInitializationService = host.ModelProviderInitializationService;
        _configStore = configStore;
        _modelsDevCatalogService = modelsDevCatalogService;
        PluginRuntime = host.PluginRuntime;
        PluginHostBridge = pluginHostBridge;
        _providerDescriptors = providerDescriptors;
        AgentSessionCatalog = host.AgentSessionCatalog;
        CatalogOptions = host.CatalogOptions;
        ProjectCatalog = host.ProjectCatalog;
        SessionViewCatalog = host.SessionViewCatalog;
        SkillCatalog = host.SkillCatalog;
        AgentHub = host.AgentHub;
        RuntimeService = host.RuntimeService;
        ProjectFileSearchService = host.ProjectFileSearchService;
        CurrentProject = host.CurrentProject;
        _disposeTask = CreateOwnedServicesDisposal(
            _host.DisposeAsync,
            _modelsDevCatalogService.DisposeAsync,
            LogManager.Shutdown,
            ownsLogging);
    }

    public CatalogOptions CatalogOptions { get; }

    public IReadOnlyList<ModelProviderDescriptor> ProviderDescriptors => _providerDescriptors;

    public IModelProviderRegistry ModelProviderRegistry => _modelProviderRegistry;

    public IModelProviderInitializationService ModelProviderInitializationService => _modelProviderInitializationService;

    internal IModelProviderInitializationService ProviderInit => _modelProviderInitializationService;

    internal ModelsDevCatalogService ModelsDevCatalogService => _modelsDevCatalogService;

    public ProjectCatalog ProjectCatalog { get; }

    public SessionViewCatalog SessionViewCatalog { get; }

    public SkillCatalog SkillCatalog { get; }

    public AgentHub AgentHub { get; }

    public SessionRuntimeService RuntimeService { get; }

    public IAgentSessionCatalog AgentSessionCatalog { get; }

    public IProjectFileSearchService ProjectFileSearchService { get; }

    public ProjectDescriptor CurrentProject { get; }

    public PluginRuntimeManager PluginRuntime { get; }

    public PluginHostBridge PluginHostBridge { get; }

    /// <summary>
    /// Creates the terminal frontend's owned services, rolling back returned acquisitions on failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels existing startup operations, but does not skip rollback.</param>
    /// <param name="prestartedPluginRuntime">A borrowed plugin runtime that remains caller-owned.</param>
    /// <returns>The completed owner of the shared host, metadata service and any acquired logging.</returns>
    /// <remarks>
    /// Failure cleanup awaits the entire acquired host operation before metadata and owned logging.
    /// Failed inner host creation returns no host; its own rollback precedes outer cleanup.
    /// Durable effects remain. Hidden constructor acquisitions, lower-owner failures and pending
    /// deferred startup joining are not qualified by this bounded best-effort rollback.
    /// </remarks>
    /// <exception cref="Exception">Creation failed and rollback succeeded; the original exception is propagated.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled and rollback succeeded.</exception>
    /// <exception cref="AggregateException">Creation and rollback both failed; their direct exceptions remain ordered and unflattened.</exception>
    public static async Task<CodeAltaOwnedServices> CreateAsync(
        CancellationToken cancellationToken,
        PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        ModelsDevCatalogService? modelsDevCatalogService = null;
        CodeAltaHost? sharedHost = null;
        var ownsLogging = false;

        try
        {
            var homeRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".alta");
            Directory.CreateDirectory(homeRoot);
            var cacheRoot = Path.Combine(homeRoot, "cache");
            var rawArguments = Environment.GetCommandLineArgs();
            // The developer instance (--dev) shares the profile and keeps its own sessions, view state and logs.
            var instance = CodeAltaInstanceProfile.Create(homeRoot, CodeAltaInstanceProfile.IsDeveloperRequested(rawArguments));
            ownsLogging = CodeAltaLogging.Initialize(instance.StateRoot);

            Directory.CreateDirectory(cacheRoot);
            var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(rawArguments);
            var catalogOptions = new CatalogOptions { GlobalRoot = homeRoot };
            var configStore = new CodeAltaConfigStore(catalogOptions);
            modelsDevCatalogService = new ModelsDevCatalogService(
                new ModelsDevCatalogServiceOptions
                {
                    CacheFilePath = Path.Combine(cacheRoot, "model-catalog", "models_dev_db.json"),
                });
            modelsDevCatalogService.StartBackgroundRefresh();

            var providerDescriptors = new List<ModelProviderDescriptor>();
            var pluginAltaServiceBridge = new PluginAltaServiceBridge();
            sharedHost = await CodeAltaHost.CreateAsync(
                    new CodeAltaHostOptions
                    {
                        GlobalRoot = homeRoot,
                        StateRoot = instance.StateRoot,
                        CurrentProjectPath = Environment.CurrentDirectory,
                        IsHeadless = false,
                        PluginFrontend = CodeAlta.Plugins.Abstractions.PluginFrontends.Terminal,
                        PluginStartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),
                        PluginAuthoringProfile = PluginAuthoringProfile.Terminal,
                        HasInteractiveUi = true,
                        PluginSafeMode = pluginBootstrapOptions.PluginSafeMode,
                        RawArguments = rawArguments,
                        WaitForEnterAfterPluginLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                        PrestartedPluginRuntime = prestartedPluginRuntime,
                        PluginBuiltIns = CodeAltaBuiltInPlugins.All,
                        PluginServices = new CodeAltaPluginServices(pluginAltaServiceBridge),
                        ConfigureModelProviders = RegisterFrontendModelProviders,
                        PluginAgentEventFailurePolicy = (envelope, failure) => RuntimePluginAgentEventFailurePolicy.ReportAsync(
                            envelope.SessionId, failure,
                            (message, error) => CodeAltaApp.UiLogger.Error(error, message),
                            CodeAltaCrashReporter.ReportFatalTaskException),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var pluginRuntime = sharedHost.PluginRuntime;
            var pluginHostBridge = new PluginHostBridge(pluginRuntime, () => sharedHost.CurrentProject, pluginAltaServiceBridge);

            return new CodeAltaOwnedServices(
                ownsLogging,
                sharedHost,
                configStore,
                modelsDevCatalogService,
                pluginHostBridge,
                providerDescriptors);

            void RegisterFrontendModelProviders(ModelProviderRegistry modelProviderRegistry)
            {
                providerDescriptors.AddRange(
                    ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                        modelProviderRegistry,
                        configStore,
                        homeRoot,
                        modelsDevCatalogService));

            }
        }
        catch (Exception creationFailure)
        {
            var acquisitions = new { Host = sharedHost, Metadata = modelsDevCatalogService,
                PrestartedPlugins = prestartedPluginRuntime, OwnsLogging = ownsLogging };
            if (Program.StartupOwner.ContainsRetention(creationFailure))
                throw new AgentDependencyRetentionException("terminal creation", "retained inner acquisition", [creationFailure], acquisitions);
            try
            {
                await PluginEventDependencyBarrier.BeforeRollbackAsync(
                    sharedHost?.PluginRuntime ?? prestartedPluginRuntime, creationFailure, acquisitions).ConfigureAwait(false);
            }
            catch (Exception barrierFailure) when (Program.StartupOwner.ContainsRetention(barrierFailure))
            {
                throw new AgentDependencyRetentionException("terminal creation", "plugin barrier", [barrierFailure], acquisitions);
            }
            await RollbackOwnedServicesCreationAsync(
                creationFailure,
                () => sharedHost?.DisposeAsync() ?? ValueTask.CompletedTask,
                () => modelsDevCatalogService?.DisposeAsync() ?? ValueTask.CompletedTask,
                LogManager.Shutdown,
                ownsLogging).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Awaits the single best-effort host, metadata and owned logging cleanup operation.
    /// </summary>
    /// <returns>The same underlying operation for repeated or concurrent callers, without retries.</returns>
    /// <remarks>
    /// Awaits the entire host operation before metadata cleanup, then attempts owned logging shutdown.
    /// Each later stage is attempted after faults or cancellation. A lone failure is rethrown unchanged;
    /// multiple failures retain direct references in execution order without flattening aggregates.
    /// Callbacks must not recursively dispose or await disposal of this same owner. There is no hard timeout
    /// or guarantee of termination of work left active by failed or noncooperative child services.
    /// </remarks>
    /// <exception cref="Exception">A single cleanup stage failed; the original exception is propagated.</exception>
    /// <exception cref="OperationCanceledException">The only cleanup failure was cancellation.</exception>
    /// <exception cref="AggregateException">Multiple cleanup stages failed.</exception>
    public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask);

    // Deferred startup calls this before entering the unchanged ShellFrontendHost plugin barrier.
    // The Host pre-owns and memoizes the actual originals; no borrowed dependency is disposed here.
    internal void BeginShutdownControls() => _host.BeginShutdownControls();

    /// <summary>
    /// Creates a lazy, single-execution outer cleanup operation from mandatory, caller-supplied operations.
    /// </summary>
    /// <remarks>
    /// Validates every operation in parameter order without invoking it, including borrowed logging.
    /// Uses default execution-and-publication; first access starts inline. Borrowed logging remains uncalled.
    /// Callbacks must not recursively access the resulting lazy value or await its own disposal task.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    internal static Lazy<Task> CreateOwnedServicesDisposal(
        Func<ValueTask> disposeHost,
        Func<ValueTask> disposeModelsDevCatalog,
        Action shutdownLogging,
        bool ownsLogging)
    {
        ArgumentNullException.ThrowIfNull(disposeHost);
        ArgumentNullException.ThrowIfNull(disposeModelsDevCatalog);
        ArgumentNullException.ThrowIfNull(shutdownLogging);

        return new Lazy<Task>(() => DisposeOwnedServicesCoreAsync(
            disposeHost,
            disposeModelsDevCatalog,
            shutdownLogging,
            ownsLogging));
    }

    private static async Task DisposeOwnedServicesCoreAsync(
        Func<ValueTask> disposeHost,
        Func<ValueTask> disposeModelsDevCatalog,
        Action shutdownLogging,
        bool ownsLogging)
    {
        var stages = new[]
        {
            new OwnedServicesRelease(disposeHost),
            new OwnedServicesRelease(disposeModelsDevCatalog),
            new OwnedServicesRelease(() => { shutdownLogging(); return ValueTask.CompletedTask; }),
        };
        var failures = new List<Exception>();
        for (var index = 0; index < stages.Length; index++)
        {
            if (index == 2 && !ownsLogging) continue;
            stages[index].Launch();
            if (await stages[index].Outcome!.ConfigureAwait(false) is not { } failure) continue;
            failures.Add(failure);
            if (Program.StartupOwner.ContainsRetention(failure))
                throw new AgentDependencyRetentionException("terminal owned services", "dependent service release", failures, stages);
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    internal sealed class OwnedServicesRelease(Func<ValueTask> invoke)
    {
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? AwaitedFailure { get; private set; }
        internal Task<Exception?>? Outcome { get; private set; }
        internal void Launch() => Outcome = InvokeAsync();
        private async Task<Exception?> InvokeAsync()
        {
            try
            {
                Original = invoke().AsTask();
                await Original.ConfigureAwait(false);
                return null;
            }
            catch (Exception failure)
            {
                AwaitedFailure = failure;
                OriginalFaults = Original?.Exception;
                var evidence = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                // A synchronous callback failure has no Task receipt by construction, but no work
                // escaped either. Preserve it as an ordinary failure and continue best-effort cleanup.
                return evidence;
            }
        }
    }

    /// <summary>
    /// Awaits best-effort rollback of returned outer acquisitions, then reports creation failure.
    /// </summary>
    /// <param name="creationFailure">The original creation exception, retained unchanged.</param>
    /// <param name="disposeHost">Disposes the complete acquired host, or does nothing if none returned.</param>
    /// <param name="disposeModelsDevCatalog">Disposes acquired metadata, or does nothing if none returned.</param>
    /// <param name="shutdownLogging">Shuts down logging only when owned.</param>
    /// <param name="ownsLogging">Whether this creation successfully acquired logging ownership.</param>
    /// <returns>An operation that always reports creation failure, after attempting rollback.</returns>
    /// <remarks>
    /// Validates the creation exception first, then all callbacks synchronously through the existing
    /// disposal factory before starting its traversal inline. Cleanup has no caller cancellation or
    /// timeout. Never recursively await this creation/rollback operation from its own callback.
    /// A host failure remains intact; metadata and owned logging are still attempted. This does not
    /// recover hidden constructor resources or qualify lower-owner termination or deferred startup joins.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The creation exception or a mandatory callback is null.</exception>
    /// <exception cref="Exception">Rollback succeeded; the original creation exception is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled and rollback succeeded.</exception>
    /// <exception cref="AggregateException">Rollback failed; creation and rollback failures are two direct, ordered references without flattening.</exception>
    internal static Task RollbackOwnedServicesCreationAsync(
        Exception creationFailure,
        Func<ValueTask> disposeHost,
        Func<ValueTask> disposeModelsDevCatalog,
        Action shutdownLogging,
        bool ownsLogging)
    {
        ArgumentNullException.ThrowIfNull(creationFailure);
        var disposal = CreateOwnedServicesDisposal(disposeHost, disposeModelsDevCatalog, shutdownLogging, ownsLogging);
        return RollbackOwnedServicesCreationCoreAsync(creationFailure, disposal);
    }

    private static async Task RollbackOwnedServicesCreationCoreAsync(Exception creationFailure, Lazy<Task> disposal)
    {
        if (Program.StartupOwner.ContainsRetention(creationFailure))
            throw new AgentDependencyRetentionException("terminal creation", "retained inner acquisition", [creationFailure], disposal);
        try
        {
            await disposal.Value.ConfigureAwait(false);
        }
        catch (Exception rollbackFailure)
        {
            throw new AggregateException(creationFailure, rollbackFailure);
        }

        ExceptionDispatchInfo.Throw(creationFailure);
    }

    internal static IReadOnlyList<ModelProviderDescriptor> CreateBuiltInProviderDescriptors()
    {
        return
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(ModelProviderIds.Copilot, "Copilot"),
        ];
    }

    public async Task<IReadOnlyList<ModelProviderDescriptor>> RefreshModelProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var providerDefinitions = _configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var expectedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerDescriptors = new List<ModelProviderDescriptor>();

        providerDescriptors.AddRange(
            ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
                _modelProviderRegistry,
                providerDefinitions.Values.Where(static definition => definition.Enabled != false),
                CatalogOptions.GlobalRoot,
                _modelsDevCatalogService));
        foreach (var descriptor in providerDescriptors)
        {
            expectedProviderIds.Add(descriptor.ProviderId.Value);
        }

        foreach (var descriptor in _providerDescriptors.ToArray())
        {
            if (expectedProviderIds.Contains(descriptor.ProviderId.Value))
            {
                continue;
            }

            _modelProviderRegistry.Unregister(descriptor.ProviderId);
        }

        _providerDescriptors.Clear();
        _providerDescriptors.InsertRange(
            0,
            providerDescriptors.OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase));

        return _providerDescriptors;
    }
}
