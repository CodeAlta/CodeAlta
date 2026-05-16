using CodeAlta.Agent;
using CodeAlta.Agent.Acp;
using CodeAlta.Agent.Codex;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.CodexSdk;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class CodeAltaLiveToolHost : IAsyncDisposable
{
    private readonly CodeAltaHost _host;
    private readonly ModelsDevCatalogService _modelsDevCatalogService;
    private readonly bool _ownsLogging;

    private CodeAltaLiveToolHost(
        CodeAltaHost host,
        ModelsDevCatalogService modelsDevCatalogService,
        AltaServiceCollection services,
        bool ownsLogging)
    {
        _host = host;
        _modelsDevCatalogService = modelsDevCatalogService;
        Services = services;
        _ownsLogging = ownsLogging;
    }

    public IServiceProvider Services { get; }

    public static async Task<CodeAltaLiveToolHost> CreateAsync(
        IReadOnlyList<string> args,
        string? currentDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var homeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta");
        Directory.CreateDirectory(homeRoot);
        var cacheRoot = Path.Combine(homeRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        var ownsLogging = CodeAltaLogging.Initialize(homeRoot);
        var bootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(args);
        var catalogOptions = new CatalogOptions { GlobalRoot = homeRoot };
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var installedBackendStore = new AcpInstalledBackendStore(catalogOptions);
        var modelsDevCatalogService = new ModelsDevCatalogService(
            new ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(cacheRoot, "model-catalog", "models_dev_db.json"),
            });
        modelsDevCatalogService.StartBackgroundRefresh();
        var backendDescriptors = new List<AgentBackendDescriptor>();
        var providerDefinitions = configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static definition => definition.ProviderKey, StringComparer.OrdinalIgnoreCase);
        var altaToolBackendIds = ResolveAltaToolBackendIds(providerDefinitions.Values);
        var pluginAltaService = new PluginAltaServiceBridge();
        CodeAltaHost host;
        try
        {
            host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = homeRoot,
                    CurrentProjectPath = currentDirectory ?? Environment.CurrentDirectory,
                    IsHeadless = true,
                    HasInteractiveUi = false,
                    PluginSafeMode = bootstrapOptions.PluginSafeMode,
                    RawArguments = args,
                    WaitForEnterAfterPluginLiveOutput = false,
                    PluginBuiltIns = CodeAltaBuiltInPlugins.All,
                    PluginServices = new CodeAltaPluginServices(pluginAltaService),
                    ConfigureAgentBackends = RegisterLiveToolBackends,
                },
                cancellationToken);
        }
        catch
        {
            await modelsDevCatalogService.DisposeAsync();
            if (ownsLogging && LogManager.IsInitialized)
            {
                LogManager.Shutdown();
            }

            throw;
        }

        foreach (var backendId in host.AgentHub.ListRegisteredBackends().OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (!backendDescriptors.Any(descriptor => descriptor.BackendId == backendId))
            {
                backendDescriptors.Add(new AgentBackendDescriptor(backendId, backendId.Value));
            }
        }

        var services = new AltaServiceCollection()
            .Add(host.CatalogOptions)
            .Add(host.ProjectCatalog)
            .Add(host.ThreadCatalog)
            .Add(host.SkillCatalog)
            .Add(host.AgentHub)
            .Add(host.RuntimeService)
            .Add(host.ProjectFileSearchService)
            .Add<IReadOnlyList<AgentBackendDescriptor>>(backendDescriptors)
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .Add<IAltaSessionToolBackendPolicy>(new AltaSessionToolBackendPolicy(altaToolBackendIds));
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        pluginAltaService.SetDispatcher(dispatcher);
        services
            .Add(registry)
            .Add(dispatcher);

        return new CodeAltaLiveToolHost(host, modelsDevCatalogService, services, ownsLogging);

        void RegisterLiveToolBackends(AgentBackendFactory backendFactory)
        {
            if (providerDefinitions.TryGetValue("codex_cli", out var codexProvider) && codexProvider.Enabled != false)
            {
                var codexPath = CodeAltaOwnedServices.ResolveCodexExecutablePath(
                    Environment.GetEnvironmentVariable("CODEALTA_CODEX_PATH"));
                backendFactory.RegisterCodex(
                    new CodexAgentBackendOptions
                    {
                        ProcessOptions = new CodexProcessOptions
                        {
                            CodexPath = codexPath,
                            LocalRootPath = cacheRoot,
                            ReleaseTag = codexPath is null ? CodexClient.CompiledAgainstReleaseTag : null,
                        },
                    });
                backendDescriptors.Add(new AgentBackendDescriptor(AgentBackendIds.Codex, codexProvider.DisplayName ?? "Codex CLI"));
            }

            if (providerDefinitions.TryGetValue("copilot_cli", out var copilotProvider) && copilotProvider.Enabled != false)
            {
                backendFactory.RegisterCopilot(CodeAltaOwnedServices.CreateCopilotBackendOptions(copilotProvider, cacheRoot));
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
                if (!CodeAltaOwnedServices.TryCreateAcpBackendOptions(catalogOptions, definition, out var acpOptions))
                {
                    continue;
                }

                backendFactory.RegisterAcp(acpOptions);
                backendDescriptors.Add(new AgentBackendDescriptor(
                    AcpAgentBackendFactoryExtensions.CreateBackendId(acpOptions.AgentId),
                    acpOptions.DisplayName));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.DisposeAsync();
        }
        finally
        {
            await _modelsDevCatalogService.DisposeAsync();
            if (_ownsLogging && LogManager.IsInitialized)
            {
                LogManager.Shutdown();
            }
        }
    }

    private static IReadOnlySet<string> ResolveAltaToolBackendIds(IEnumerable<CodeAltaProviderDocument> providerDefinitions)
    {
        var backendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentBackendIds.OpenAIChat.Value,
            AgentBackendIds.OpenAIResponses.Value,
        };
        foreach (var provider in providerDefinitions)
        {
            if (SupportsHostInjectedTools(provider.ProviderType) && !string.IsNullOrWhiteSpace(provider.ProviderKey))
            {
                backendIds.Add(provider.ProviderKey);
            }
        }

        return backendIds;
    }

    private static bool SupportsHostInjectedTools(string? providerType)
        => string.Equals(providerType, "openai-chat", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "openai-responses", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "copilot", StringComparison.OrdinalIgnoreCase);
}
