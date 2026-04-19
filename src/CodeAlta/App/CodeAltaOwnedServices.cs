using CodeAlta.Agent;
using CodeAlta.Agent.Acp;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Acp;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.CodexSdk;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using CodeAlta.Search;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaOwnedServices : IAsyncDisposable
{
    private const string CodexPathOverrideEnvironmentVariable = "CODEALTA_CODEX_PATH";

    private readonly bool _ownsLogging;
    private readonly CodeAltaDb _db;
    private readonly AgentBackendFactory _backendFactory;
    private readonly CodeAltaConfigStore _configStore;
    private readonly AcpInstalledBackendStore _installedBackendStore;
    private readonly List<AgentBackendDescriptor> _backendDescriptors;
    private readonly CodexInstallProgressReporter _codexInstallProgress;
    private readonly ModelsDevCatalogService _modelsDevCatalogService;

    private CodeAltaOwnedServices(
        bool ownsLogging,
        CodeAltaDb db,
        AgentBackendFactory backendFactory,
        CodeAltaConfigStore configStore,
        AcpInstalledBackendStore installedBackendStore,
        CodexInstallProgressReporter codexInstallProgress,
        ModelsDevCatalogService modelsDevCatalogService,
        CatalogOptions catalogOptions,
        List<AgentBackendDescriptor> backendDescriptors,
        AcpAgentRegistryService acpAgentRegistryService,
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        AgentHub agentHub,
        WorkThreadRuntimeService runtimeService,
        IProjectFileSearchService projectFileSearchService)
    {
        _ownsLogging = ownsLogging;
        _db = db;
        _backendFactory = backendFactory;
        _configStore = configStore;
        _installedBackendStore = installedBackendStore;
        _codexInstallProgress = codexInstallProgress;
        _modelsDevCatalogService = modelsDevCatalogService;
        _backendDescriptors = backendDescriptors;
        CatalogOptions = catalogOptions;
        AcpAgentRegistryService = acpAgentRegistryService;
        ProjectCatalog = projectCatalog;
        ThreadCatalog = threadCatalog;
        AgentHub = agentHub;
        RuntimeService = runtimeService;
        ProjectFileSearchService = projectFileSearchService;
    }

    public CatalogOptions CatalogOptions { get; }

    public IReadOnlyList<AgentBackendDescriptor> BackendDescriptors => _backendDescriptors;

    public CodexInstallProgressReporter CodexInstallProgress => _codexInstallProgress;

    public AcpAgentRegistryService AcpAgentRegistryService { get; }

    public ProjectCatalog ProjectCatalog { get; }

    public WorkThreadCatalog ThreadCatalog { get; }

    public AgentHub AgentHub { get; }

    public WorkThreadRuntimeService RuntimeService { get; }

    public IProjectFileSearchService ProjectFileSearchService { get; }

    public static async Task<CodeAltaOwnedServices> CreateAsync(CancellationToken cancellationToken)
    {
        var homeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alta");
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codealta");
        var cacheRoot = Path.Combine(legacyRoot, "cache");
        MigrateLegacyStorage(legacyRoot, homeRoot, cacheRoot);
        Directory.CreateDirectory(homeRoot);
        var ownsLogging = CodeAltaLogging.Initialize(homeRoot);

        Directory.CreateDirectory(cacheRoot);
        var codexInstallProgress = new CodexInstallProgressReporter();

        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = Path.Combine(cacheRoot, "codealta.db"),
            });
        await db.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var agentRepository = new AgentRepository(db);
        var catalogOptions = new CatalogOptions
        {
            GlobalRoot = homeRoot,
        };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        await projectCatalog.UpsertFromPathAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleProfileStore = new RoleProfileStore();
        var instructionTemplateProvider = new AgentInstructionTemplateProvider();
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
        var backendFactory = new AgentBackendFactory();
        var backendDescriptors = new List<AgentBackendDescriptor>();
        var codexPath = ResolveCodexExecutablePath(
            Environment.GetEnvironmentVariable(CodexPathOverrideEnvironmentVariable));
        if (providerDefinitions.TryGetValue("codex", out var codexProvider) && codexProvider.Enabled)
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
            backendDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Codex, codexProvider.DisplayName ?? "Codex"));
        }

        if (providerDefinitions.TryGetValue("copilot", out var copilotProvider) && copilotProvider.Enabled)
        {
            backendFactory.RegisterCopilot(new CopilotAgentBackendOptions());
            backendDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Copilot, copilotProvider.DisplayName ?? "GitHub Copilot"));
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

        var agentHub = new AgentHub(backendFactory, agentRepository);
        var runtimeService = new WorkThreadRuntimeService(
            agentHub,
            projectCatalog,
            threadCatalog,
            roleProfileStore,
            instructionTemplateProvider,
            catalogOptions);
        var projectFileSearchService = new ProjectFileSearchService(
            new ProjectFileSnapshotCache(),
            new PersistentProjectFileUsageStore(new ProjectFileUsageRepository(db)));

        return new CodeAltaOwnedServices(
            ownsLogging,
            db,
            backendFactory,
            configStore,
            installedBackendStore,
            codexInstallProgress,
            modelsDevCatalogService,
            catalogOptions,
            backendDescriptors,
            acpAgentRegistryService,
            projectCatalog,
            threadCatalog,
            agentHub,
            runtimeService,
            projectFileSearchService);
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

    private static void MigrateLegacyStorage(string legacyRoot, string homeRoot, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        var legacyLocalRoot = Path.Combine(legacyRoot, "local");
        if (Directory.Exists(legacyLocalRoot) && !Directory.Exists(cacheRoot))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
            Directory.Move(legacyLocalRoot, cacheRoot);
        }

        Directory.CreateDirectory(homeRoot);

        foreach (var directory in Directory.EnumerateDirectories(legacyRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(homeRoot, name);
            if (!Directory.Exists(destination))
            {
                Directory.Move(directory, destination);
            }
        }

        foreach (var file in Directory.EnumerateFiles(legacyRoot))
        {
            var destination = Path.Combine(homeRoot, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RuntimeService.DisposeAsync().ConfigureAwait(false);
        await AgentHub.DisposeAsync().ConfigureAwait(false);
        AcpAgentRegistryService.Dispose();
        await _modelsDevCatalogService.DisposeAsync().ConfigureAwait(false);

        GC.KeepAlive(_db);

        if (_ownsLogging)
        {
            LogManager.Shutdown();
        }
    }

    internal static IReadOnlyList<AgentBackendDescriptor> CreateBuiltInBackendDescriptors()
    {
        return
        [
            new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex"),
            new AgentBackendDescriptor(AgentBackendIds.Copilot, "GitHub Copilot"),
        ];
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

    private static bool TryCreateAcpBackendOptions(
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
            EnableFilesystem = definition.EnableFilesystem,
            EnableTerminal = definition.EnableTerminal,
            EnableElicitation = definition.EnableElicitation,
            UseUnstableFeatures = definition.UseUnstable,
            UnstableFeatures = new AcpUnstableFeatureOptions
            {
                UseSessionResume = definition.UseUnstable,
                UseSessionClose = definition.UseUnstable,
                UseSessionDelete = definition.UseUnstable,
                UseElicitation = definition.UseUnstable && definition.EnableElicitation,
                UseSetModel = definition.UseUnstable,
            },
        };
        return true;
    }
}
