using System.Collections;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool;

/// <summary>What <see cref="HeadlessAltaTools.Attach"/> composes for a host without a window.</summary>
public sealed record HeadlessAltaToolsOptions
{
    /// <summary>
    /// Gets which built-in <c>alta</c> commands the sessions are offered, such as <see cref="AltaCommandRegistryOptions.Cli"/>
    /// or <see cref="AltaCommandRegistryOptions.Node"/>.
    /// </summary>
    public required AltaCommandRegistryOptions Registry { get; init; }

    /// <summary>
    /// Gets the mode of Code Mode of every session (<see cref="CodeModeScopes"/>), or null, the default, for the mode the
    /// configuration gives the provider and the model of each send. <see cref="CodeModeScopes.Off"/> gives no Code Mode tool.
    /// </summary>
    public string? CodeMode { get; init; }

    /// <summary>
    /// Gets the scratchpad folder where the runs of Code Mode keep their source (<c>codemode/&lt;session-id&gt;</c> below it);
    /// null keeps none.
    /// </summary>
    public string? CodeModeScratchpadRoot { get; init; }

    /// <summary>
    /// Gets whether the scripts of Code Mode keep values across runs (<c>store</c> and <c>load</c>) in the state root of the
    /// host. False, the default: a host that leaves its state root as it is keeps none.
    /// </summary>
    public bool KeepsScriptValues { get; init; }

    /// <summary>
    /// Gets whether the runs of Code Mode read the space of a session from the runtime when its tools do not give it.
    /// False, the default.
    /// </summary>
    public bool ReadsSessionSpaces { get; init; }

    /// <summary>
    /// Gets whether the host owns delayed prompt reminders (<c>alta reminder</c>): a host that outlives the turns of its
    /// sessions. False, the default; a registry that offers <c>reminder</c> needs them.
    /// </summary>
    public bool OwnsReminders { get; init; }

    /// <summary>Gets the model servers the <c>alta llama</c> commands manage; null, the default, for none (the commands do not exist).</summary>
    public IAltaModelServers? ModelServers { get; init; }
}

/// <summary>
/// The <c>alta</c> tool and the Code Mode tools of the sessions of a host without a window (<c>altacli</c>, <c>altanode</c>):
/// the commands over the services of the host, minus those its registry options leave out, and nobody to ask. Scripts that
/// call <c>ask()</c> are told that nobody can be asked; the <c>ask</c> command is offered only where a registry allows it
/// and the host gives a service that routes questions to people.
/// </summary>
/// <remarks>Disposing it stops the reminders it owns; the host is left to its owner.</remarks>
public sealed class HeadlessAltaTools : IAsyncDisposable
{
    /// <summary>The number of output records an <c>alta</c> call returns when it does not say.</summary>
    public const int DefaultMaxOutputRecords = 200;

    /// <summary>The number of output bytes an <c>alta</c> call returns when it does not say.</summary>
    public const int DefaultMaxOutputBytes = 64 * 1024;

    private HeadlessAltaTools(AltaCommandRegistry registry, AltaCommandDispatcher dispatcher, AltaReminderService? reminders, CodeModeSessionTools? codeMode)
    {
        Registry = registry;
        Dispatcher = dispatcher;
        Reminders = reminders;
        CodeMode = codeMode;
    }

    /// <summary>How long an <c>alta</c> call runs when it does not say.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(120);

    /// <summary>Gets the registry of the <c>alta</c> commands.</summary>
    public AltaCommandRegistry Registry { get; }

    /// <summary>Gets the dispatcher of the <c>alta</c> commands, the one of the <c>alta</c> tool of the sessions.</summary>
    public AltaCommandDispatcher Dispatcher { get; }

    /// <summary>Gets the reminders the host owns; null when it owns none (<see cref="HeadlessAltaToolsOptions.OwnsReminders"/>).</summary>
    public AltaReminderService? Reminders { get; }

    /// <summary>Gets the Code Mode tools of the sessions; null when Code Mode is off for every session.</summary>
    public CodeModeSessionTools? CodeMode { get; }

    /// <summary>
    /// Composes the <c>alta</c> commands over the services of <paramref name="host"/> and gives every session the
    /// <c>alta</c> tool, with <c>exec</c> and <c>wait</c> beside it while Code Mode is on for it
    /// (<see cref="OwnedSessionCommandService.SessionTools"/>).
    /// </summary>
    /// <param name="host">The host whose sessions get the tools.</param>
    /// <param name="options">What is composed.</param>
    /// <returns>The tools, which own the reminders of the host when it has some.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static HeadlessAltaTools Attach(CodeAltaHost host, HeadlessAltaToolsOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        var services = CreateServices(host);
        if (options.ModelServers is not null) services.Add(options.ModelServers);
        var registry = new AltaCommandRegistry(options.Registry);
        var dispatcher = new AltaCommandDispatcher(registry, services);
        // The registry is a service too: alta tool list and alta tool capability list read it.
        services.Add(registry).Add(dispatcher);
        AltaReminderService? reminders = null;
        if (options.OwnsReminders)
        {
            reminders = new AltaReminderService(services);
            services.Add(reminders);
        }

        var codeMode = CreateCodeMode(host, options);
        host.Commands.SessionTools = CreateSessionTools(dispatcher, codeMode);
        return new HeadlessAltaTools(registry, dispatcher, reminders, codeMode);
    }

    /// <summary>Stops the reminders the host owns: none fires afterwards.</summary>
    /// <returns>A task that completes once they stopped.</returns>
    public ValueTask DisposeAsync() => Reminders?.DisposeAsync() ?? ValueTask.CompletedTask;

    // The alta commands over the services of the host, as the desktop composes them, minus those of a user (ask, review)
    // and of a window (views, windows, terminals, canvases), and of search.
    private static AltaServiceCollection CreateServices(CodeAltaHost host)
    {
        var permissions = host.RuntimeService.Permissions;
        var configStore = new CodeAltaConfigStore(host.CatalogOptions);
        return new AltaServiceCollection()
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
            // What a session started by alta answers when nobody is there: what the permissions of the host decide.
            .Add<IAltaSessionInteractionDefaults>(new AltaSessionInteractionDefaults(
                permissions.OwnedDefaultPermissionHandler, permissions.OwnedDefaultUserInputHandler))
            // The tasks and the plans of the projects.
            .Add(new CodeAlta.Catalog.WorkItems.WorkItemService(configStore, host.CatalogOptions.StateRoot))
            // The bookmarks of the sessions, in the database of the host and the journals.
            .Add(new CodeAlta.Catalog.Bookmarks.BookmarkService(host.SessionViewCatalog.JournalStore.ApplicationDatabase,
                new CodeAlta.Catalog.Bookmarks.SessionJournalBookmarks(host.SessionViewCatalog.JournalStore.CreateSessionStore())))
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .Add(host.PluginRuntime)
            // The runs of the scripts of Code Mode, for `alta workflow`.
            .Add(host.CodeModeRuns)
            .Add(host.CodeModeScripts)
            .AddPluginRuntimeHooks(host.Commands.Plugins ?? new PluginOrchestrationBridge(host.PluginRuntime.Adapter, () => host.PluginRuntime.ActivePlugins));
    }

    // A fixed mode wins over the settings, for every session; without one, each session has the mode of its provider and model.
    // No question of a script reaches anybody: ask() tells the script that nobody can be asked.
    private static CodeModeSessionTools? CreateCodeMode(CodeAltaHost host, HeadlessAltaToolsOptions options)
    {
        if (options.CodeMode == CodeModeScopes.Off) return null;
        var scratchpad = options.CodeModeScratchpadRoot;
        var codeModeOptions = new CodeModeToolOptions
        {
            Runs = host.CodeModeRuns,
            Agents = new DeferredCodeModeAgents(() => host.CodeModeAgents),
            Library = host.CodeModeScripts,
            Cache = new CodeAlta.CodeMode.CodeModeScriptCache(),
            Values = options.KeepsScriptValues ? new CodeModeValueStore(CodeModeValueStore.RootOf(host.CatalogOptions)) : null,
            SourceFolder = scratchpad is { Length: > 0 } ? sessionId => Path.Combine(scratchpad, "codemode", sessionId) : null,
        };
        var store = new CodeAltaConfigStore(host.CatalogOptions);
        var tools = options.CodeMode is { } fixedMode
            ? new CodeModeSessionTools(codeModeOptions, () => fixedMode)
            : new CodeModeSessionTools(codeModeOptions, (providerKey, modelId) => CodeModeSessionTools.ResolveMode(store, providerKey, modelId));
        var runtime = host.RuntimeService;
        return options.ReadsSessionSpaces
            ? tools.WithSpaces(async (sessionId, token) => await runtime.GetSessionSpaceFilterAsync(sessionId, token).ConfigureAwait(false))
            : tools;
    }

    // One alta tool per session, as the desktop gives them, without the tool search of an index a headless host has not;
    // the exec and wait tools of Code Mode beside it.
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
                DefaultMaxOutputRecords = DefaultMaxOutputRecords,
                DefaultMaxOutputBytes = DefaultMaxOutputBytes,
                DefaultTimeout = DefaultTimeout,
                Spaces = request.Spaces,
                SessionOptions = request.Options,
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
            return codeMode?.Create(dispatcher, caller, tools, request.ProviderKey, request.ModelId) ?? tools;
        };

    // The providers as the registry lists them now.
    private sealed class RegisteredProviders(ModelProviderRegistry registry) : IReadOnlyList<ModelProviderDescriptor>
    {
        public int Count => registry.ListProviders().Count;

        public ModelProviderDescriptor this[int index] => registry.ListProviders()[index];

        public IEnumerator<ModelProviderDescriptor> GetEnumerator() => registry.ListProviders().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
