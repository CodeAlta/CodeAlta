using System.Collections;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugin.Acp;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugin.Jira;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Cli;

/// <summary>What a host of <c>altacli</c> is composed with, beside its roots.</summary>
internal sealed record CliHostRequest
{
    public required CliStorage Storage { get; init; }

    public required CodeAltaConfigOptions Config { get; init; }

    public required string ProjectFolder { get; init; }

    public bool StartPlugins { get; init; } = true;

    public bool StartMcp { get; init; } = true;

    public bool ListsSkills { get; init; } = true;

    public bool Offline { get; init; }
}

/// <summary>
/// The host of a run of <c>altacli</c>, the composition every face shares (<c>run</c>, <c>alta</c>, <c>models</c>,
/// <c>auth check</c>, and the coming <c>acp</c>): a headless <see cref="CodeAltaHost"/> with full trust and the headless
/// instructions, on a private application database and private scratchpads, that leaves the shared state of the global
/// root as it is (no <c>alta.lock</c>, no <c>AGENTS.md</c>, no project registered); the configured providers; the built-in
/// plugins that declare the command-line application; and the <c>alta</c> tool without the commands of a user or a window.
/// </summary>
/// <remarks>Disposed in order: the host, the endpoints of the tools of plugin agents, models.dev, the private database, an ephemeral state root.</remarks>
internal sealed class CliHost : IAsyncDisposable
{
    private readonly PrivateApplicationDatabase _database;
    private readonly ModelsDevCatalogService _modelsDev;
    private readonly LoopbackHarnessToolEndpoints _toolEndpoints;
    private int _disposed;

    private CliHost(CliStorage storage, CodeAltaHost host, AltaCommandDispatcher alta, PrivateApplicationDatabase database,
        ModelsDevCatalogService modelsDev, LoopbackHarnessToolEndpoints toolEndpoints)
    {
        Storage = storage;
        Host = host;
        Alta = alta;
        _database = database;
        _modelsDev = modelsDev;
        _toolEndpoints = toolEndpoints;
    }

    public CliStorage Storage { get; }

    public CodeAltaHost Host { get; }

    /// <summary>Gets the dispatcher of the <c>alta</c> commands, the one of the <c>alta</c> tool of the sessions.</summary>
    public AltaCommandDispatcher Alta { get; }

    /// <summary>Gets the MCP plugin that runs, which takes the servers of a session (<c>altacli acp</c>); null when MCP is off.</summary>
    public McpPlugin? Mcp { get; private init; }

    /// <summary>Creates the host of a run.</summary>
    /// <exception cref="Exception">The host could not be created; what was acquired is released first.</exception>
    public static async Task<CliHost> CreateAsync(CliHostRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        var storage = request.Storage;
        var database = PrivateApplicationDatabase.Create(Path.Combine(environment.TempRoot, PrivateApplicationDatabase.DefaultFolderName));
        var toolEndpoints = new LoopbackHarnessToolEndpoints(LogManager.GetLogger("CodeAlta.Cli.ToolEndpoints"));
        var modelsDev = new ModelsDevCatalogService(new ModelsDevCatalogServiceOptions
        {
            CacheFilePath = Path.Combine(storage.GlobalRoot, "cache", "model-catalog", "models_dev_db.json"),
        });
        CodeAltaHost? host = null;
        McpPlugin? mcp = null;
        try
        {
            // A test gives its own providers and stays off the network.
            if (!request.Offline && environment.ConfigureModelProviders is null) modelsDev.StartBackgroundRefresh();
            var pluginProviderTypes = new PluginModelProviderTypes(toolEndpoints) { ModelCatalog = modelsDev };
            var catalog = new CatalogOptions { GlobalRoot = storage.GlobalRoot, StateRoot = storage.StateRoot, Config = request.Config };
            host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = storage.GlobalRoot,
                StateRoot = storage.StateRoot,
                Config = request.Config,
                CurrentProjectPath = request.ProjectFolder,
                ReadOnlyGlobalState = true,
                ApplicationDatabase = database.Database,
                // Desktop clears the scratchpad of ~/.alta when it starts: the run keeps its own, deleted with its database.
                ScratchpadRoot = Path.Combine(database.Directory, "scratchpad"),
                FullTrust = true,
                HeadlessInstructions = true,
                // A session another process runs (another altacli, CodeAlta Desktop or TUI) is refused as in use, exit code 5.
                SessionLeaseOwner = SessionLeaseOwner.CliKind,
                ListsSkills = request.ListsSkills,
                IsHeadless = true,
                HasInteractiveUi = false,
                OwnsLogging = false,
                DiscoveryScope = environment.DiscoveryScope,
                BuiltInSkillRoot = environment.BuiltInSkillRoot,
                PluginEnvironment = environment.PluginEnvironment,
                StartPlugins = request.StartPlugins,
                PluginFrontend = PluginFrontends.Cli,
                PluginBuiltIns = BuiltInPlugins(request, plugin => Volatile.Write(ref mcp, plugin)),
                RawArguments = [],
                ConfigureModelProviders = environment.ConfigureModelProviders ?? (registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                    registry, new CodeAltaConfigStore(catalog), storage.GlobalRoot, modelsDev, pluginProviderTypes)),
                ConfigurePluginModelProviders = environment.ConfigureModelProviders is not null ? null : (registry, runtime) =>
                {
                    pluginProviderTypes.Attach(runtime.GetModelProviderTypes);
                    pluginProviderTypes.RegisterOrReplace(registry, new CodeAltaConfigStore(catalog), storage.GlobalRoot, includeDisabled: false);
                },
            }, cancellationToken).ConfigureAwait(false);
            var alta = ComposeAlta(host, request.ListsSkills);
            host.Commands.SessionTools = CreateSessionTools(alta);
            // The MCP plugin that runs: one that was created but did not start is not used.
            var started = Volatile.Read(ref mcp) is { } created && host.PluginRuntime.ActivePlugins.Any(plugin => ReferenceEquals(plugin.Instance, created)) ? created : null;
            return new CliHost(storage, host, alta, database, modelsDev, toolEndpoints) { Mcp = started };
        }
        catch
        {
            if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
            await toolEndpoints.DisposeAsync().ConfigureAwait(false);
            await modelsDev.DisposeAsync().ConfigureAwait(false);
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The built-in plugins of the command-line application: Git, Jira, MCP (unless <c>--no-mcp</c>) and ACP.</summary>
    /// <param name="request">What the host is composed with.</param>
    /// <param name="mcpCreated">Told the MCP plugin once it is created; null for nothing.</param>
    internal static IReadOnlyList<BuiltInPluginDefinition> BuiltInPlugins(CliHostRequest request, Action<McpPlugin>? mcpCreated = null)
    {
        var plugins = new List<BuiltInPluginDefinition>
        {
            new()
            {
                Id = "git", DisplayName = "Git",
                Description = "Exposes the GitHub, GitLab and Azure DevOps CLIs (gh, glab, az) to sessions when they are available.",
                PluginType = typeof(GitPlugin), Factory = static () => new GitPlugin(),
                Frontends = PluginFrontends.All | PluginFrontends.Cli,
            },
            new()
            {
                Id = "jira", DisplayName = "Jira",
                Description = "The issues of a Jira project, for the projects whose configuration names one.",
                PluginType = typeof(JiraPlugin), Factory = static () => new JiraPlugin(),
                Frontends = PluginFrontends.All | PluginFrontends.Cli,
            },
        };
        if (request.StartMcp)
        {
            var loadsSkills = request.ListsSkills;
            plugins.Add(new()
            {
                Id = "mcp", DisplayName = "MCP",
                Description = "Connects CodeAlta to configured Model Context Protocol servers.",
                // No window shows the views of MCP Apps; the skills of the servers are loaded like the others, without asking.
                PluginType = typeof(McpPlugin), Factory = () =>
                {
                    var plugin = new McpPlugin(new McpPluginOptions { ShowsApps = false, LoadsSkills = loadsSkills });
                    mcpCreated?.Invoke(plugin);
                    return plugin;
                },
                Frontends = PluginFrontends.All | PluginFrontends.Cli,
            });
        }

        plugins.Add(new()
        {
            Id = AcpPlugin.PluginId, DisplayName = "ACP",
            Description = "Runs the agents of the Agent Client Protocol (ACP) as model providers.",
            PluginType = typeof(AcpPlugin), Factory = static () => new AcpPlugin(),
            Frontends = PluginFrontends.All | PluginFrontends.Cli,
        });
        return plugins;
    }

    // The alta commands over the services of the host, as the desktop composes them, minus those of a user (ask, review), of a
    // window (views, windows, terminals, canvases), of a host that outlives the run (reminders, automations) and of search.
    private static AltaCommandDispatcher ComposeAlta(CodeAltaHost host, bool listsSkills)
    {
        var permissions = host.RuntimeService.Permissions;
        var configStore = new CodeAltaConfigStore(host.CatalogOptions);
        var services = new AltaServiceCollection()
            .Add<ISessionRetentionAdmission>(host.RetentionProtection)
            .Add(host.CatalogOptions)
            .Add(host.SessionViewCatalog.TextFiles)
            .Add(host.ProjectCatalog)
            .Add(host.SpaceCatalog)
            .Add(host.SessionViewCatalog)
            .Add(host.RuntimeService)
            .Add(host.RuntimeService.SkillCatalog)
            .Add(host.AgentHub)
            .Add(host.ModelProviderInitializationService)
            .Add(host.ModelProviderRegistry)
            .Add(host.ProjectFileSearchService)
            .Add<ISessionArchive>(host.Commands)
            .Add(host.Commands)
            .Add<IAltaNotesService>(new RuntimeAltaNotesService(host.RuntimeService))
            .Add<IReadOnlyList<ModelProviderDescriptor>>(new RegisteredProviders(host.ModelProviderRegistry))
            .Add<IAltaSessionToolProviderPolicy>(new AltaSessionToolProviderPolicy())
            .Add<IAltaSessionInteractionDefaults>(new AltaSessionInteractionDefaults(
                permissions.OwnedDefaultPermissionHandler, permissions.OwnedDefaultUserInputHandler))
            // The tasks and the plans of the projects.
            .Add(new CodeAlta.Catalog.WorkItems.WorkItemService(configStore, host.CatalogOptions.StateRoot))
            // The bookmarks of the sessions, in the private database and the journals.
            .Add(new CodeAlta.Catalog.Bookmarks.BookmarkService(host.SessionViewCatalog.JournalStore.ApplicationDatabase,
                new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore())))
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .Add(host.PluginRuntime)
            .AddPluginRuntimeHooks(host.Commands.Plugins ?? new PluginOrchestrationBridge(host.PluginRuntime.Adapter, () => host.PluginRuntime.ActivePlugins));
        var registry = new AltaCommandRegistry(listsSkills ? AltaCommandRegistryOptions.Cli : WithoutSkills(AltaCommandRegistryOptions.Cli));
        var dispatcher = new AltaCommandDispatcher(registry, services);
        // The registry is a service too: alta tool list and alta tool capability list read it.
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    // --no-skills: the agents are offered no command that lists or loads a skill.
    private static AltaCommandRegistryOptions WithoutSkills(AltaCommandRegistryOptions options)
        => options with
        {
            AllowedBuiltInRoots = options.AllowedBuiltInRoots!.Where(static root => root is not ("skill" or "skills" or "skills_activate"))
                .ToHashSet(StringComparer.Ordinal),
        };

    // One alta tool per session, as the desktop gives them, without the tool search of an index the command line has not.
    private static Func<OwnedSessionToolRequest, IReadOnlyList<AgentToolDefinition>> CreateSessionTools(AltaCommandDispatcher dispatcher)
        => request =>
        {
            // A session being created has no id yet: its tool learns it from its first call.
            var session = request.SessionId;
            var tool = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions
            {
                SourceSessionIdProvider = () => Volatile.Read(ref session),
                SourceProjectId = request.ProjectId,
                WorkingDirectory = request.WorkingDirectory,
                WorkingDirectoryProvider = request.ProjectDirectory is { } projectDirectory
                    ? () => Directory.Exists(request.WorkingDirectory) ? request.WorkingDirectory : projectDirectory
                    : null,
                DefaultMaxOutputRecords = 200,
                DefaultMaxOutputBytes = 64 * 1024,
                DefaultTimeout = TimeSpan.FromSeconds(120),
            });
            return
            [
                tool with
                {
                    Handler = (invocation, cancellationToken) =>
                    {
                        Interlocked.CompareExchange(ref session, invocation.SessionId, null);
                        return tool.Handler(invocation, cancellationToken);
                    },
                },
            ];
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Host.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _toolEndpoints.DisposeAsync().ConfigureAwait(false);
            await _modelsDev.DisposeAsync().ConfigureAwait(false);
            await _database.DisposeAsync().ConfigureAwait(false);
            Storage.DeleteEphemeralState();
        }
    }

    // The providers as the registry lists them now.
    private sealed class RegisteredProviders(ModelProviderRegistry registry) : IReadOnlyList<ModelProviderDescriptor>
    {
        public int Count => registry.ListProviders().Count;

        public ModelProviderDescriptor this[int index] => registry.ListProviders()[index];

        public IEnumerator<ModelProviderDescriptor> GetEnumerator() => registry.ListProviders().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
