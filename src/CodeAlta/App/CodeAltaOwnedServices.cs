using CodeAlta.Agent;
using CodeAlta.Agent.Acp;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Acp;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaOwnedServices : IAsyncDisposable
{
    private readonly bool _ownsLogging;
    private readonly AgentBackendFactory _backendFactory;
    private readonly ModelProviderRegistry _modelProviderRegistry;
    private readonly IModelProviderInitializationService _modelProviderInitializationService;
    private readonly CodeAltaConfigStore _configStore;
    private readonly AcpInstalledBackendStore _installedBackendStore;
    private readonly List<ModelProviderDescriptor> _backendDescriptors;
    private readonly ModelsDevCatalogService _modelsDevCatalogService;

    private CodeAltaOwnedServices(
        bool ownsLogging,
        AgentBackendFactory backendFactory,
        ModelProviderRegistry modelProviderRegistry,
        IModelProviderInitializationService modelProviderInitializationService,
        CodeAltaConfigStore configStore,
        AcpInstalledBackendStore installedBackendStore,
        ModelsDevCatalogService modelsDevCatalogService,
        PluginRuntimeManager pluginRuntime,
        PluginHostBridge pluginHostBridge,
        CatalogOptions catalogOptions,
        List<ModelProviderDescriptor> backendDescriptors,
        IAgentSessionCatalog sessionCatalog,
        AcpAgentRegistryService acpAgentRegistryService,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        SkillCatalog skillCatalog,
        AgentHub agentHub,
        SessionRuntimeService runtimeService,
        IProjectFileSearchService projectFileSearchService)
    {
        _ownsLogging = ownsLogging;
        _backendFactory = backendFactory;
        _modelProviderRegistry = modelProviderRegistry;
        _modelProviderInitializationService = modelProviderInitializationService;
        _configStore = configStore;
        _installedBackendStore = installedBackendStore;
        _modelsDevCatalogService = modelsDevCatalogService;
        PluginRuntime = pluginRuntime;
        PluginHostBridge = pluginHostBridge;
        _backendDescriptors = backendDescriptors;
        SessionCatalog = sessionCatalog;
        CatalogOptions = catalogOptions;
        AcpAgentRegistryService = acpAgentRegistryService;
        ProjectCatalog = projectCatalog;
        ThreadCatalog = threadCatalog;
        SkillCatalog = skillCatalog;
        AgentHub = agentHub;
        RuntimeService = runtimeService;
        ProjectFileSearchService = projectFileSearchService;
    }

    public CatalogOptions CatalogOptions { get; }

    public IReadOnlyList<ModelProviderDescriptor> BackendDescriptors => _backendDescriptors;

    public IModelProviderRegistry ModelProviderRegistry => _modelProviderRegistry;

    public IModelProviderInitializationService ModelProviderInitializationService => _modelProviderInitializationService;

    internal IModelProviderInitializationService ProviderInit => _modelProviderInitializationService;

    internal ModelsDevCatalogService ModelsDevCatalogService => _modelsDevCatalogService;

    public AcpAgentRegistryService AcpAgentRegistryService { get; }

    public ProjectCatalog ProjectCatalog { get; }

    public WorkThreadCatalog ThreadCatalog { get; }

    public SkillCatalog SkillCatalog { get; }

    public AgentHub AgentHub { get; }

    public SessionRuntimeService RuntimeService { get; }

    public IAgentSessionCatalog SessionCatalog { get; }

    public IProjectFileSearchService ProjectFileSearchService { get; }

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
        var installedBackendStore = new AcpInstalledBackendStore(catalogOptions);
        var acpAgentRegistryService = new AcpAgentRegistryService(catalogOptions, installedBackendStore);
        var modelsDevCatalogService = new ModelsDevCatalogService(
            new ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(cacheRoot, "model-catalog", "models_dev_db.json"),
            });
        modelsDevCatalogService.StartBackgroundRefresh();

        var providerDefinitions = configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var backendDescriptors = new List<ModelProviderDescriptor>();
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
        var backendFactory = sharedHost.BackendFactory;
        var pluginRuntime = sharedHost.PluginRuntime;
        var pluginHostBridge = new PluginHostBridge(pluginRuntime, () => sharedHost.CurrentProject, pluginAltaServiceBridge);
        backendDescriptors.AddRange(
            pluginRuntime.Adapter.GetAgentBackends(new PluginAdapterOperationOptions { HasInteractiveUi = true })
                .Select(static pluginBackend => new ModelProviderDescriptor(
                    new ModelProviderId(pluginBackend.Name),
                    pluginBackend.DisplayName ?? pluginBackend.Name)));

        return new CodeAltaOwnedServices(
            ownsLogging,
            backendFactory,
            sharedHost.ModelProviderRegistry,
            sharedHost.ModelProviderInitializationService,
            configStore,
            installedBackendStore,
            modelsDevCatalogService,
            pluginRuntime,
            pluginHostBridge,
            sharedHost.CatalogOptions,
            backendDescriptors,
            sharedHost.SessionCatalog,
            acpAgentRegistryService,
            sharedHost.ProjectCatalog,
            sharedHost.ThreadCatalog,
            sharedHost.SkillCatalog,
            sharedHost.AgentHub,
            sharedHost.RuntimeService,
            sharedHost.ProjectFileSearchService);

        void RegisterFrontendModelProviders(ModelProviderRegistry modelProviderRegistry, AgentBackendFactory backendFactory)
        {
            backendDescriptors.AddRange(
                ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    backendFactory,
                    modelProviderRegistry,
                    configStore,
                    homeRoot,
                    modelsDevCatalogService));

            // ACP support remains implemented at the protocol/backend layer, but the
            // interactive frontend does not register ACP backends while the UI path
            // is hidden and unvalidated.
            // foreach (var definition in configStore.LoadEffectiveAcpBackendDefinitions(installedBackendStore.Load()))
            // {
            //     if (TryCreateAcpBackendOptions(catalogOptions, definition, out var acpOptions))
            //     {
            //         backendFactory.RegisterAcp(acpOptions);
            //         backendDescriptors.Add(new ModelProviderDescriptor(
            //             AcpAgentBackendFactoryExtensions.CreateBackendId(acpOptions.AgentId),
            //             acpOptions.DisplayName));
            //     }
            // }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RuntimeService.DisposeAsync().ConfigureAwait(false);
        await AgentHub.DisposeAsync().ConfigureAwait(false);
        await _modelProviderRegistry.DisposeAsync().ConfigureAwait(false);
        await PluginRuntime.DisposeAsync().ConfigureAwait(false);
        AcpAgentRegistryService.Dispose();
        await _modelsDevCatalogService.DisposeAsync().ConfigureAwait(false);

        if (_ownsLogging)
        {
            LogManager.Shutdown();
        }
    }

    internal static IReadOnlyList<ModelProviderDescriptor> CreateBuiltInBackendDescriptors()
    {
        return
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(ModelProviderIds.Copilot, "Copilot"),
        ];
    }

    public async Task<IReadOnlyList<ModelProviderDescriptor>> RefreshProviderBackendsAsync(
        CancellationToken cancellationToken = default)
    {
        var providerDefinitions = _configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var expectedBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerDescriptors = new List<ModelProviderDescriptor>();

        providerDescriptors.AddRange(
            ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
                _backendFactory,
                _modelProviderRegistry,
                providerDefinitions.Values.Where(static definition => definition.Enabled != false),
                CatalogOptions.GlobalRoot,
                _modelsDevCatalogService));
        foreach (var descriptor in providerDescriptors)
        {
            expectedBackendIds.Add(descriptor.BackendId.Value);
        }

        foreach (var descriptor in _backendDescriptors
                     .Where(static descriptor => !descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            if (expectedBackendIds.Contains(descriptor.BackendId.Value))
            {
                continue;
            }

            _backendFactory.Unregister(descriptor.BackendId);
            _modelProviderRegistry.Unregister(descriptor.ProviderId);
        }

        _backendDescriptors.RemoveAll(static descriptor => !descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase));
        _backendDescriptors.InsertRange(
            0,
            providerDescriptors.OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase));

        return _backendDescriptors;
    }

    public async Task<IReadOnlyList<ModelProviderDescriptor>> RefreshAcpBackendsAsync(
        CancellationToken cancellationToken = default)
    {
        var effectiveDefinitions = _configStore.LoadEffectiveAcpBackendDefinitions(_installedBackendStore.Load());
        var expectedBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in effectiveDefinitions)
        {
            if (!TryCreateAcpBackendOptions(CatalogOptions, definition, out var acpOptions))
            {
                continue;
            }

            var backendId = AcpAgentBackendFactoryExtensions.CreateBackendId(acpOptions.AgentId);
            expectedBackendIds.Add(backendId.Value);

            _backendFactory.RegisterOrReplaceAcp(acpOptions);
        }

        var currentAcpDescriptors = _backendDescriptors
            .Where(static descriptor => descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var descriptor in currentAcpDescriptors)
        {
            if (expectedBackendIds.Contains(descriptor.BackendId.Value))
            {
                continue;
            }

            _backendFactory.Unregister(descriptor.BackendId);
        }

        _backendDescriptors.RemoveAll(static descriptor => descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase));
        _backendDescriptors.AddRange(
            effectiveDefinitions
                .Where(definition => TryCreateAcpBackendOptions(CatalogOptions, definition, out _))
                .Select(definition => new ModelProviderDescriptor(
                    AcpAgentBackendFactoryExtensions.CreateBackendId(definition.AgentId),
                    definition.DisplayName ?? definition.AgentId))
                .OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase));

        return _backendDescriptors;
    }

    internal static bool TryCreateAcpBackendOptions(
        CatalogOptions catalogOptions,
        AcpBackendDefinition definition,
        out AcpAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.AgentId) ||
            string.IsNullOrWhiteSpace(definition.Command))
        {
            options = null!;
            return false;
        }

        var normalizedAgentId = definition.AgentId.Trim().ToLowerInvariant();
        options = new AcpAgentBackendOptions
        {
            AgentId = normalizedAgentId,
            DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName)
                ? normalizedAgentId
                : definition.DisplayName.Trim(),
            RegistryId = string.IsNullOrWhiteSpace(definition.RegistryId)
                ? null
                : definition.RegistryId.Trim(),
            ProcessOptions = new AcpProcessOptions
            {
                FileName = definition.Command.Trim(),
                Arguments = definition.Arguments,
                WorkingDirectory = definition.WorkingDirectory,
                EnvironmentVariables = definition.EnvironmentVariables,
            },
            StateRootPath = Path.Combine(catalogOptions.AcpStateRoot, normalizedAgentId),
            EnableFilesystem = definition.EnableFilesystem ?? AcpBackendDefinition.DefaultEnableFilesystem,
            EnableTerminal = definition.EnableTerminal ?? AcpBackendDefinition.DefaultEnableTerminal,
            EnableElicitation = definition.EnableElicitation ?? AcpBackendDefinition.DefaultEnableElicitation,
            UseUnstableFeatures = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable,
            UnstableFeatures = new AcpUnstableFeatureOptions
            {
                UseSessionResume = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable,
                UseSessionClose = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable,
                UseSessionDelete = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable,
                UseElicitation = (definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable) &&
                    (definition.EnableElicitation ?? AcpBackendDefinition.DefaultEnableElicitation),
                UseSetModel = definition.UseUnstable ?? AcpBackendDefinition.DefaultUseUnstable,
            },
        };
        return true;
    }
}
