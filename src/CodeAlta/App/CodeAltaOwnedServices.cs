using CodeAlta.Agent;
using CodeAlta.Agent.Acp;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Acp;
using CodeAlta.Agent.CopilotCli;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.CodexSdk;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaOwnedServices : IAsyncDisposable
{
    private const string CodexPathOverrideEnvironmentVariable = "CODEALTA_CODEX_PATH";

    private readonly bool _ownsLogging;
    private readonly AgentBackendFactory _backendFactory;
    private readonly CodeAltaConfigStore _configStore;
    private readonly AcpInstalledBackendStore _installedBackendStore;
    private readonly List<AgentBackendDescriptor> _backendDescriptors;
    private readonly CodexInstallProgressReporter _codexInstallProgress;
    private readonly ModelsDevCatalogService _modelsDevCatalogService;

    private CodeAltaOwnedServices(
        bool ownsLogging,
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        AcpInstalledBackendStore installedBackendStore,
        CodexInstallProgressReporter codexInstallProgress,
        ModelsDevCatalogService modelsDevCatalogService,
        PluginRuntimeManager pluginRuntime,
        PluginHostBridge pluginHostBridge,
        CatalogOptions catalogOptions,
        List<AgentBackendDescriptor> backendDescriptors,
        AcpAgentRegistryService acpAgentRegistryService,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        SkillCatalog skillCatalog,
        AgentHub agentHub,
        WorkThreadRuntimeService runtimeService,
        IProjectFileSearchService projectFileSearchService)
    {
        _ownsLogging = ownsLogging;
        _backendFactory = backendFactory;
        _configStore = configStore;
        _installedBackendStore = installedBackendStore;
        _codexInstallProgress = codexInstallProgress;
        _modelsDevCatalogService = modelsDevCatalogService;
        PluginRuntime = pluginRuntime;
        PluginHostBridge = pluginHostBridge;
        _backendDescriptors = backendDescriptors;
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

    public IReadOnlyList<AgentBackendDescriptor> BackendDescriptors => _backendDescriptors;

    public CodexInstallProgressReporter CodexInstallProgress => _codexInstallProgress;

    internal ModelsDevCatalogService ModelsDevCatalogService => _modelsDevCatalogService;

    public AcpAgentRegistryService AcpAgentRegistryService { get; }

    public ProjectCatalog ProjectCatalog { get; }

    public WorkThreadCatalog ThreadCatalog { get; }

    public SkillCatalog SkillCatalog { get; }

    public AgentHub AgentHub { get; }

    public WorkThreadRuntimeService RuntimeService { get; }

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
        var codexInstallProgress = new CodexInstallProgressReporter();
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
        var backendDescriptors = new List<AgentBackendDescriptor>();
        var codexPath = ResolveCodexExecutablePath(
            Environment.GetEnvironmentVariable(CodexPathOverrideEnvironmentVariable));
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
                    ConfigureAgentBackends = RegisterFrontendBackends,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var backendFactory = sharedHost.BackendFactory;
        var pluginRuntime = sharedHost.PluginRuntime;
        var pluginHostBridge = new PluginHostBridge(pluginRuntime, () => sharedHost.CurrentProject, pluginAltaServiceBridge);
        backendDescriptors.AddRange(
            pluginRuntime.Adapter.GetAgentBackends(new PluginAdapterOperationOptions { HasInteractiveUi = true })
                .Select(static pluginBackend => new AgentBackendDescriptor(
                    new AgentBackendId(pluginBackend.Name),
                    pluginBackend.DisplayName ?? pluginBackend.Name)));

        return new CodeAltaOwnedServices(
            ownsLogging,
            backendFactory,
            configStore,
            installedBackendStore,
            codexInstallProgress,
            modelsDevCatalogService,
            pluginRuntime,
            pluginHostBridge,
            sharedHost.CatalogOptions,
            backendDescriptors,
            acpAgentRegistryService,
            sharedHost.ProjectCatalog,
            sharedHost.ThreadCatalog,
            sharedHost.SkillCatalog,
            sharedHost.AgentHub,
            sharedHost.RuntimeService,
            sharedHost.ProjectFileSearchService);

        void RegisterFrontendBackends(AgentBackendFactory backendFactory)
        {
            if (providerDefinitions.TryGetValue("codex_cli", out var codexProvider) && codexProvider.Enabled != false)
            {
                backendFactory.RegisterCodex(
                    new CodexAgentBackendOptions
                    {
                        ProcessOptions = new CodexProcessOptions
                        {
                            CodexPath = codexPath,
                            LocalRootPath = cacheRoot,
                            ReleaseTag = codexPath is null ? CodexClient.CompiledAgainstReleaseTag : null,
                            Progress = codexPath is null ? codexInstallProgress : null,
                        },
                    });
                backendDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Codex, codexProvider.DisplayName ?? "Codex CLI"));
            }

            if (providerDefinitions.TryGetValue("copilot_cli", out var copilotProvider) && copilotProvider.Enabled != false)
            {
                backendFactory.RegisterCopilot(CreateCopilotBackendOptions(copilotProvider, cacheRoot));
                backendDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Copilot, copilotProvider.DisplayName ?? "Copilot CLI"));
            }

            backendDescriptors.AddRange(
                RawApiBackendRegistrar.RegisterConfiguredBackends(
                    backendFactory,
                    configStore,
                    homeRoot,
                    modelsDevCatalogService));

            foreach (var definition in configStore.LoadEffectiveAcpBackendDefinitions(installedBackendStore.Load()))
            {
                if (TryCreateAcpBackendOptions(catalogOptions, definition, out var acpOptions))
                {
                    backendFactory.RegisterAcp(acpOptions);
                    backendDescriptors.Add(new AgentBackendDescriptor(
                        AcpAgentBackendFactoryExtensions.CreateBackendId(acpOptions.AgentId),
                        acpOptions.DisplayName));
                }
            }
        }
    }

    internal static CopilotAgentBackendOptions CreateCopilotBackendOptions(
        CodeAltaProviderDocument definition,
        string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        var clientOptions = new GitHub.Copilot.SDK.CopilotClientOptions();
        if (!string.IsNullOrWhiteSpace(definition.CliPath))
        {
            clientOptions.CliPath = definition.CliPath.Trim();
        }

        return new CopilotAgentBackendOptions
        {
            ClientOptions = clientOptions,
            CliInstallOptions = new CopilotCliInstallOptions
            {
                LocalRootPath = cacheRoot,
                NpmRegistryUrl = string.IsNullOrWhiteSpace(definition.NpmRegistry) ? null : definition.NpmRegistry.Trim(),
            },
        };
    }

    internal static string? ResolveCodexExecutablePath(string? configuredOverridePath)
    {
        foreach (var candidate in EnumerateCodexExecutableCandidates(configuredOverridePath))
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCodexExecutableCandidates(
        string? configuredOverridePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredOverridePath))
        {
            yield return configuredOverridePath;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RuntimeService.DisposeAsync().ConfigureAwait(false);
        await AgentHub.DisposeAsync().ConfigureAwait(false);
        await PluginRuntime.DisposeAsync().ConfigureAwait(false);
        AcpAgentRegistryService.Dispose();
        await _modelsDevCatalogService.DisposeAsync().ConfigureAwait(false);

        if (_ownsLogging)
        {
            LogManager.Shutdown();
        }
    }

    internal static IReadOnlyList<AgentBackendDescriptor> CreateBuiltInBackendDescriptors()
    {
        return
        [
            new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex CLI"),
            new AgentBackendDescriptor(AgentBackendIds.Copilot, "Copilot CLI"),
        ];
    }

    public async Task<IReadOnlyList<AgentBackendDescriptor>> RefreshProviderBackendsAsync(
        CancellationToken cancellationToken = default)
    {
        var providerDefinitions = _configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var expectedBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerDescriptors = new List<AgentBackendDescriptor>();

        if (providerDefinitions.TryGetValue("codex_cli", out var codexProvider) && codexProvider.Enabled != false)
        {
            var codexPath = ResolveCodexExecutablePath(Environment.GetEnvironmentVariable(CodexPathOverrideEnvironmentVariable));
            // Codex is a process-backed runtime. Replacing the factory is enough for the
            // next cold start; keep an already loaded backend alive across provider saves.
            _backendFactory.RegisterOrReplaceCodex(
                new CodexAgentBackendOptions
                {
                    ProcessOptions = new CodexProcessOptions
                    {
                        CodexPath = codexPath,
                        LocalRootPath = Path.Combine(CatalogOptions.GlobalRoot, "cache"),
                        ReleaseTag = codexPath is null ? CodexClient.CompiledAgainstReleaseTag : null,
                        Progress = codexPath is null ? _codexInstallProgress : null,
                    },
                });
            providerDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Codex, codexProvider.DisplayName ?? "Codex CLI"));
            expectedBackendIds.Add(AgentBackendIds.Codex.Value);
        }

        if (providerDefinitions.TryGetValue("copilot_cli", out var copilotProvider) && copilotProvider.Enabled != false)
        {
            // Copilot is a process-backed runtime. Replacing the factory is enough for the
            // next cold start; keep an already loaded backend alive across provider saves.
            _backendFactory.RegisterOrReplaceCopilot(
                CreateCopilotBackendOptions(copilotProvider, Path.Combine(CatalogOptions.GlobalRoot, "cache")));
            providerDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Copilot, copilotProvider.DisplayName ?? "Copilot CLI"));
            expectedBackendIds.Add(AgentBackendIds.Copilot.Value);
        }

        providerDescriptors.AddRange(
            RawApiBackendRegistrar.RegisterOrReplaceConfiguredBackends(
                _backendFactory,
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

            await AgentHub.UnloadBackendAsync(descriptor.BackendId, cancellationToken).ConfigureAwait(false);
            _backendFactory.Unregister(descriptor.BackendId);
        }

        _backendDescriptors.RemoveAll(static descriptor => !descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase));
        _backendDescriptors.InsertRange(
            0,
            providerDescriptors.OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase));

        return _backendDescriptors;
    }

    public async Task<IReadOnlyList<AgentBackendDescriptor>> RefreshAcpBackendsAsync(
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

            await AgentHub.UnloadBackendAsync(backendId, cancellationToken).ConfigureAwait(false);
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

            await AgentHub.UnloadBackendAsync(descriptor.BackendId, cancellationToken).ConfigureAwait(false);
            _backendFactory.Unregister(descriptor.BackendId);
        }

        _backendDescriptors.RemoveAll(static descriptor => descriptor.BackendId.Value.StartsWith("acp:", StringComparison.OrdinalIgnoreCase));
        _backendDescriptors.AddRange(
            effectiveDefinitions
                .Where(definition => TryCreateAcpBackendOptions(CatalogOptions, definition, out _))
                .Select(definition => new AgentBackendDescriptor(
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
