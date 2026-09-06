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
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
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

    public static async Task<CodeAltaOwnedServices> CreateAsync(
        CancellationToken cancellationToken,
        PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        var homeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta");
        Directory.CreateDirectory(homeRoot);
        var cacheRoot = Path.Combine(homeRoot, "cache");
        var ownsLogging = CodeAltaLogging.Initialize(homeRoot);

        Directory.CreateDirectory(cacheRoot);
        var rawArguments = Environment.GetCommandLineArgs();
        var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(rawArguments);
        var catalogOptions = new CatalogOptions { GlobalRoot = homeRoot };
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var modelsDevCatalogService = new ModelsDevCatalogService(
            new ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(cacheRoot, "model-catalog", "models_dev_db.json"),
            });
        modelsDevCatalogService.StartBackgroundRefresh();

        var providerDescriptors = new List<ModelProviderDescriptor>();
        var pluginAltaServiceBridge = new PluginAltaServiceBridge();
        var sharedHost = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = homeRoot,
                    CurrentProjectPath = Environment.CurrentDirectory,
                    IsHeadless = false,
                    HasInteractiveUi = true,
                    PluginSafeMode = pluginBootstrapOptions.PluginSafeMode,
                    RawArguments = rawArguments,
                    WaitForEnterAfterPluginLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                    PrestartedPluginRuntime = prestartedPluginRuntime,
                    PluginBuiltIns = CodeAltaBuiltInPlugins.All,
                    PluginServices = new CodeAltaPluginServices(pluginAltaServiceBridge),
                    ConfigureModelProviders = RegisterFrontendModelProviders,
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
    public ValueTask DisposeAsync() => new(_disposeTask.Value);

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
        List<Exception>? failures = null;
        try
        {
            await disposeHost().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeModelsDevCatalog().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (ownsLogging)
        {
            try
            {
                shutdownLogging();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
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
