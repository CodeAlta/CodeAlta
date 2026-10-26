using System.Collections;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
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

    /// <summary>Gets the mode of Code Mode of the sessions (<c>off</c>, <c>on</c>, <c>only</c>), or null for <c>[code_mode]</c> of the configuration.</summary>
    public string? CodeMode { get; init; }

    /// <summary>
    /// Gets whether the host checks, once started, that the plugins and the MCP servers the configuration marks required
    /// started (<c>run</c> and <c>acp</c>); a failure is a <see cref="CliStartupException"/>.
    /// </summary>
    public bool ChecksRequired { get; init; }
}

/// <summary>A host that cannot run as it was set up: a required plugin or MCP server did not start. Exit code 2.</summary>
/// <param name="message">What did not start, and why.</param>
internal sealed class CliStartupException(string message) : Exception(message);

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
    /// <summary>How long the host is waited for when it stops, by default: its sessions stop, its plugins and providers end.</summary>
    public static readonly TimeSpan DefaultShutdownLimit = TimeSpan.FromSeconds(10);

    private readonly PrivateApplicationDatabase _database;
    private readonly ModelsDevCatalogService _modelsDev;
    private readonly LoopbackHarnessToolEndpoints _toolEndpoints;
    private readonly TimeSpan _shutdownLimit;
    private int _disposed;

    private CliHost(CliStorage storage, CodeAltaHost host, AltaCommandDispatcher alta, PrivateApplicationDatabase database,
        ModelsDevCatalogService modelsDev, LoopbackHarnessToolEndpoints toolEndpoints, TimeSpan shutdownLimit)
    {
        Storage = storage;
        Host = host;
        Alta = alta;
        _database = database;
        _modelsDev = modelsDev;
        _toolEndpoints = toolEndpoints;
        _shutdownLimit = shutdownLimit;
    }

    public CliStorage Storage { get; }

    public CodeAltaHost Host { get; }

    /// <summary>Gets the dispatcher of the <c>alta</c> commands, the one of the <c>alta</c> tool of the sessions.</summary>
    public AltaCommandDispatcher Alta { get; }

    /// <summary>Gets the MCP plugin that runs, which takes the servers of a session (<c>altacli acp</c>); null when MCP is off.</summary>
    public McpPlugin? Mcp { get; private init; }

    /// <summary>Gets the folder the host was started for, whose required plugins and MCP servers it checked as it started.</summary>
    public string ProjectFolder => Request.ProjectFolder;

    // What the host was composed with, for the checks of the folders of later sessions.
    private CliHostRequest Request { get; init; } = null!;

    private CatalogOptions Catalog { get; init; } = null!;

    /// <summary>
    /// Checks that the plugins and the MCP servers the configuration of a folder marks required run, as the start of the host
    /// checked those of its own folder (<see cref="CliHostRequest.ChecksRequired"/>): <c>altacli acp</c> opens sessions in
    /// other folders, whose project configuration may require others.
    /// </summary>
    /// <param name="projectFolder">The folder of a session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What does not run, and why; null when everything required runs, or when the host checks nothing.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<string?> CheckRequiredAsync(string projectFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        return Request.ChecksRequired
            ? CheckRequiredAsync(Host, Mcp, Request with { ProjectFolder = projectFolder }, Catalog, cancellationToken)
            : Task.FromResult<string?>(null);
    }

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
                // A separate profile reads none of the user's home: not the skills and agents other tools keep there.
                ReadsUserHome = storage.Mode != CliStorageMode.Profile,
                IsHeadless = true,
                HasInteractiveUi = false,
                OwnsLogging = false,
                DiscoveryScope = environment.DiscoveryScope,
                BuiltInSkillRoot = environment.BuiltInSkillRoot,
                PluginEnvironment = environment.PluginEnvironment,
                StartPlugins = request.StartPlugins,
                PluginFrontend = PluginFrontends.Cli,
                PluginBuiltIns = BuiltInPlugins(request, catalog, plugin => Volatile.Write(ref mcp, plugin)),
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
            var codeMode = request.CodeMode ?? new CodeAltaConfigStore(catalog).LoadCodeModeMode();
            host.Commands.SessionTools = CreateSessionTools(alta, codeMode is "on" or "only" ? new CodeModeSessionTools(new CodeModeToolOptions
            {
                Runs = host.CodeModeRuns,
                Agents = new CodeAlta.Orchestration.Runtime.Scripts.DeferredCodeModeAgents(() => host.CodeModeAgents),
                Cache = new CodeAlta.CodeMode.CodeModeScriptCache(),
                SourceFolder = sessionId => Path.Combine(storage.GlobalRoot, "scratchpad", "codemode", sessionId),
            }, () => codeMode) : null);
            // The MCP plugin that runs: one that was created but did not start is not used.
            var started = Volatile.Read(ref mcp) is { } created && host.PluginRuntime.ActivePlugins.Any(plugin => ReferenceEquals(plugin.Instance, created)) ? created : null;
            if (request.ChecksRequired && await CheckRequiredAsync(host, started, request, catalog, cancellationToken).ConfigureAwait(false) is { } missing)
            {
                throw new CliStartupException(missing);
            }

            return new CliHost(storage, host, alta, database, modelsDev, toolEndpoints, environment.ShutdownLimit ?? DefaultShutdownLimit)
            {
                Mcp = started,
                Request = request,
                Catalog = catalog,
            };
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
    /// <param name="catalog">The roots and the configuration of the host, which the MCP plugin reads its policy through.</param>
    /// <param name="mcpCreated">Told the MCP plugin once it is created; null for nothing.</param>
    internal static IReadOnlyList<BuiltInPluginDefinition> BuiltInPlugins(CliHostRequest request, CatalogOptions catalog, Action<McpPlugin>? mcpCreated = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
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
            var options = McpOptions(request, catalog);
            plugins.Add(new()
            {
                Id = "mcp", DisplayName = "MCP",
                Description = "Connects CodeAlta to configured Model Context Protocol servers.",
                PluginType = typeof(McpPlugin), Factory = () =>
                {
                    var plugin = new McpPlugin(options);
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

    // The MCP plugin of a run. No window shows the views of MCP Apps; the skills of the servers are loaded like the others,
    // without asking. The policy ([plugins.mcp]) is the one of the configuration the run reads, -c and CODEALTA_CONFIG_CONTENT
    // included, and the alta mcp commands do not write the configuration files. The servers, their OAuth tokens and their
    // caches are those of the global root (mcp.json, auth/mcp, cache/mcp). The visible and isolated modes read the MCP
    // configurations of other tools in the user's home too, as CodeAlta Desktop does; a separate profile (--profile) reads
    // none of the user's home.
    internal static McpPluginOptions McpOptions(CliHostRequest request, CatalogOptions catalog)
    {
        var store = new CodeAltaConfigStore(catalog);
        var overlaid = request.Config.IgnoreUserConfig || request.Config.Overlays.Count > 0;
        var globalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Storage.GlobalRoot));
        return new McpPluginOptions
        {
            ShowsApps = false,
            LoadsSkills = request.ListsSkills,
            // Without overlays and without a file, there is no policy (the store would give the template of a new file).
            GlobalConfigContent = () => overlaid || File.Exists(catalog.ConfigPath) ? store.LoadGlobalConfigContent() : null,
            ReadOnlyConfiguration = true,
            CodeAltaRoot = globalRoot,
            // The global root of the other modes is <home>/.alta.
            UserHomeDirectory = request.Storage.Mode == CliStorageMode.Profile ? null : Path.GetDirectoryName(globalRoot),
        };
    }

    // [plugins.<id>] required = true and [plugins.mcp.servers.<key>] required = true: the run does not go on with fewer of
    // them than it was set up with. --no-plugins and --no-mcp turn the check off with what they turn off.
    private static async Task<string?> CheckRequiredAsync(CodeAltaHost host, McpPlugin? mcp, CliHostRequest request, CatalogOptions catalog,
        CancellationToken cancellationToken)
    {
        if (!request.StartPlugins) return null;
        var problems = new List<string>();
        IReadOnlyList<string> requiredPlugins;
        try
        {
            requiredPlugins = RequiredPlugins(catalog, request.ProjectFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
        {
            return "The required plugins cannot be read from the configuration: " + exception.GetBaseException().Message;
        }

        foreach (var id in requiredPlugins)
        {
            if (id == "mcp" && !request.StartMcp) continue;
            if (host.PluginRuntime.ActivePlugins.Any(plugin => string.Equals(PluginIdOf(plugin), id, StringComparison.OrdinalIgnoreCase))) continue;
            var diagnostic = host.PluginRuntime.Diagnostics.LastOrDefault(candidate => candidate.Severity >= PluginDiagnosticSeverity.Error
                && (string.Equals(candidate.PackageId, id, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.RuntimeKey, "builtin:" + id, StringComparison.OrdinalIgnoreCase)));
            problems.Add($"The required plugin '{id}' did not start: {diagnostic?.Message ?? "it is not installed, it is turned off, or it is not made for the command line."}");
        }

        if (request.StartMcp)
        {
            if (mcp is not null)
            {
                foreach (var failure in await mcp.CheckRequiredServersAsync(request.ProjectFolder, cancellationToken).ConfigureAwait(false))
                {
                    problems.Add($"The required MCP server '{failure.Server}' did not start ({failure.Code}): {failure.Message}");
                }
            }
            else
            {
                // The plugin did not start (turned off, or it failed): its policy is read all the same, by an instance of its own.
                await using var reader = new McpPlugin(McpOptions(request, catalog));
                IReadOnlyList<string> required;
                try
                {
                    required = reader.GetRequiredServers(request.ProjectFolder);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Tomlyn.TomlException)
                {
                    required = [];
                }

                foreach (var key in required) problems.Add($"The required MCP server '{key}' did not start: the MCP plugin is not running.");
            }
        }

        return problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    // The plugins a configuration marks required: [plugins.<id>] required = true in the configuration of the run (the user's
    // file and what is laid over it) or in the project's .alta/config.toml.
    internal static IReadOnlyList<string> RequiredPlugins(CatalogOptions catalog, string projectFolder)
    {
        var store = new CodeAltaConfigStore(catalog);
        return [.. new[] { store.LoadGlobal(), store.LoadProject(projectFolder) }
            .SelectMany(static document => document.Plugins ?? [])
            .Where(static entry => entry.Value?.Required == true)
            .Select(static entry => entry.Key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    // The id of a plugin as the configuration names it: the package of a source plugin, the id of a built-in one.
    private static string PluginIdOf(ActivePluginInstance plugin)
        => plugin.SourcePackage?.PackageId
            ?? (plugin.Descriptor.RuntimeKey.StartsWith("builtin:", StringComparison.Ordinal) ? plugin.Descriptor.RuntimeKey["builtin:".Length..] : plugin.Descriptor.RuntimeKey);

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

    // One alta tool per session, as the desktop gives them, without the tool search of an index the command line has not;
    // with --code-mode, the exec and wait tools of Code Mode beside it.
    private static Func<OwnedSessionToolRequest, IReadOnlyList<AgentToolDefinition>> CreateSessionTools(AltaCommandDispatcher dispatcher, CodeModeSessionTools? codeMode)
        => request =>
        {
            // A session being created has no id yet: its tool learns it from its first call.
            var session = request.SessionId;
            var caller = new AltaSessionToolOptions
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
            };
            var tool = AltaSessionToolFactory.Create(dispatcher, caller);
            AgentToolDefinition[] tools =
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
            return codeMode?.Create(dispatcher, caller, tools) ?? tools;
        };

    /// <summary>
    /// Stops the host, waiting for it no longer than the shutdown limit: a session whose provider does not stop is left
    /// behind, and the process, which exits after the run, ends it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Host.DisposeAsync().AsTask().WaitAsync(_shutdownLimit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogManager.GetLogger("CodeAlta.Cli").Warn($"The host did not stop within {_shutdownLimit}: the run ends without waiting for it.");
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
