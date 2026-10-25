using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Catalog.Storage;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// Shared CodeAlta runtime composition for frontend and headless hosts.
/// </summary>
/// <remarks>
/// Exposed services are borrowed views; callers must not separately dispose host-owned services.
/// A supplied prestarted plugin runtime remains caller-owned. Disposal shares one best-effort
/// operation across repeated or concurrent callers, without retries or a termination timeout.
/// Disposal callbacks must not recursively dispose or await disposal of this same host.
/// </remarks>
public sealed class CodeAltaHost : IAsyncDisposable
{
    private readonly Lazy<Task> _disposeTask;
    private readonly HostDisposalStage _earlyReadShutdown;
    private readonly HostDisposalStage _earlyCommandShutdown;

    private CodeAltaHost(
        CatalogOptions catalogOptions,
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionViewCatalog,
        SkillCatalog skillCatalog,
        ModelProviderRegistry modelProviderRegistry,
        ModelProviderInitializationService modelProviderInitializationService,
        IAgentSessionCatalog agentSessionCatalog,
        AgentHub agentHub,
        SessionRuntimeService runtimeService,
        IProjectFileSearchService projectFileSearchService,
        PluginRuntimeManager pluginRuntime,
        bool ownsPluginRuntime,
        bool ownsLogging,
        ApplicationDatabase applicationDatabase,
        ApplicationDatabase? ownedApplicationDatabase,
        ProjectDescriptor currentProject,
        int ownedCommandReceiptCapacity,
        Func<bool> reviewOwnedCommandPermissions,
        bool enableOwnedAsks,
        bool enableOwnedUserInput,
        IStartupScratchpadCleanup? startupScratchpadCleanup,
        SessionRetentionProtection retentionProtection)
    {
        CatalogOptions = catalogOptions;
        StartupScratchpadCleanup = startupScratchpadCleanup;
        RetentionProtection = retentionProtection;
        ProjectCatalog = projectCatalog;
        SpaceCatalog = new SpaceCatalog(projectCatalog);
        SessionViewCatalog = sessionViewCatalog;
        SessionJournals = sessionViewCatalog.JournalStore.CreateJournalCatalog();
        SkillCatalog = skillCatalog;
        ModelProviderRegistry = modelProviderRegistry;
        ModelProviderInitializationService = modelProviderInitializationService;
        AgentSessionCatalog = agentSessionCatalog;
        AgentHub = agentHub;
        RuntimeService = runtimeService;
        ProjectFileSearchService = projectFileSearchService;
        PluginRuntime = pluginRuntime;
        // A plugin assigned to some spaces adds nothing to the sessions of the others.
        pluginRuntime.Adapter.SpaceAssignments ??= new CachedSpaceAssignments(new CodeAltaConfigStore(catalogOptions)).Current;
        ApplicationDatabase = applicationDatabase;
        CurrentProject = currentProject;
        Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity, reviewOwnedCommandPermissions, enableOwnedAsks, enableOwnedUserInput)
        {
            SelectionModels = async (provider, token) =>
            {
                token.ThrowIfCancellationRequested();
                var models = await modelProviderInitializationService.GetModelsAsync(provider, CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return models;
            },
            SelectionProvider = provider => modelProviderRegistry.TryGetProvider(provider, out var descriptor) ? descriptor : null,
            ProjectFileSearch = projectFileSearchService,
            Plugins = new PluginOrchestrationBridge(pluginRuntime.Adapter, () => pluginRuntime.ActivePlugins) {
                LoadedToolsOf = runtimeService.GetLoadedToolsAsync,
                DisabledToolsOf = async (sessionId, token) => await runtimeService.GetDisabledToolsAsync(sessionId, token).ConfigureAwait(false),
                DefersToolsOf = (provider, model, token) => DefersToolsAsync(modelProviderRegistry, provider, model, token),
            },
            ObservedImageModels = provider => modelProviderInitializationService.CurrentStates
                .FirstOrDefault(state => state.ProviderId == provider && state.Descriptor.IsEnabled
                    && state.Availability == ModelProviderAvailability.Ready)?.Models ?? [],
        };
        Commands.Fallback.ProviderStates = () => modelProviderInitializationService.CurrentStates;
        WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore, runtimeService);
        _earlyReadShutdown = new HostDisposalStage(() => WorkspaceReads.DisposeAsync().AsTask());
        _earlyCommandShutdown = new HostDisposalStage(() => Commands.DisposeAsync().AsTask());
        var disposal = CreateHostDisposal(
            DisposeCommandsAndRuntimeAsync,
            AgentHub.DisposeAsync,
            DisposeModelProvidersAsync,
            PluginRuntime.DisposeAsync,
            LogManager.Shutdown,
            ownsPluginRuntime,
            ownsLogging);
        _disposeTask = ownedApplicationDatabase is null
            ? disposal
            : new Lazy<Task>(() => DisposeWithDatabaseAsync(disposal, ownedApplicationDatabase));
    }

    // Whether the provider of a run defers tools for its model: a provider that cannot be created defers none.
    private static async ValueTask<bool> DefersToolsAsync(ModelProviderRegistry registry, string providerId, string? model, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.GetOrCreateRuntimeAsync(new ModelProviderId(providerId), cancellationToken).ConfigureAwait(false) is IAgentModelProviderRuntime runtime
                && AgentToolDeferral.IsSupported(runtime.RuntimeDescriptor, model);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            return false;
        }
    }

    // The database ends after everything that writes to it: the sessions, the hub and the plugins. When a stage
    // keeps a dependency it could not release, work may still write: the database stays open then.
    private static async Task DisposeWithDatabaseAsync(Lazy<Task> disposal, ApplicationDatabase database)
    {
        Exception? failure = null;
        try
        {
            await disposal.Value.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null || !OwnedProviderEventForwarding.HasRetention(failure))
        {
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>
    /// Gets the catalog options used by the host.
    /// </summary>
    public CatalogOptions CatalogOptions { get; }

    /// <summary>Gets replayable startup maintenance, or null when composition supplied no cleanup capability.</summary>
    /// <remarks>When supplied, its worker ended before this host existed.</remarks>
    public IStartupScratchpadCleanup? StartupScratchpadCleanup { get; }

    /// <summary>Gets the original host admission registry shared by providers, external work and desktop restoration.</summary>
    /// <remarks>It remains fail-closed until the composition has qualified every required owner.</remarks>
    public SessionRetentionProtection RetentionProtection { get; }

    /// <summary>
    /// Gets the project catalog.
    /// </summary>
    public ProjectCatalog ProjectCatalog { get; }

    /// <summary>
    /// Gets the catalog of the spaces: the groups of projects the user works on together.
    /// </summary>
    public SpaceCatalog SpaceCatalog { get; }

    /// <summary>
    /// Gets what gives the sessions of the host the identity of their space; it gives the usual settings in a host
    /// that does not apply them (<see cref="CodeAltaHostOptions.AppliesSpaceIdentities"/>).
    /// </summary>
    public SessionIdentityResolver Identities => RuntimeService.Identities;

    /// <summary>
    /// Gets the session-view catalog.
    /// </summary>
    public SessionViewCatalog SessionViewCatalog { get; }

    /// <summary>
    /// Gets the catalog of the journal files of every session of the store, for readers that follow journals from an offset (the
    /// statistics of the history). It lists the sessions of removed projects too, which the session lists of the application hide.
    /// </summary>
    public ISessionJournalCatalog SessionJournals { get; }

    /// <summary>
    /// Gets the skill catalog.
    /// </summary>
    public SkillCatalog SkillCatalog { get; }

    /// <summary>
    /// Gets the model provider registry used by the host.
    /// </summary>
    public ModelProviderRegistry ModelProviderRegistry { get; }

    /// <summary>
    /// Gets the model provider initialization and model-catalog service used by the host.
    /// </summary>
    public ModelProviderInitializationService ModelProviderInitializationService { get; }

    /// <summary>
    /// Gets the provider-independent session catalog used by the host.
    /// </summary>
    public IAgentSessionCatalog AgentSessionCatalog { get; }

    /// <summary>
    /// Gets the agent hub.
    /// </summary>
    public AgentHub AgentHub { get; }

    /// <summary>
    /// Gets the session-view runtime service.
    /// </summary>
    public SessionRuntimeService RuntimeService { get; }

    /// <summary>Gets the host-owned text-send and abort admission service; completion is not transcript completion.</summary>
    public OwnedSessionCommandService Commands { get; }

    /// <summary>Gets host-owned admission and drainage for up to eight actual cached workspace reads.</summary>
    public OwnedSessionWorkspace WorkspaceReads { get; }

    /// <summary>
    /// Gets the project-file search service.
    /// </summary>
    public IProjectFileSearchService ProjectFileSearchService { get; }

    /// <summary>
    /// Gets the plugin runtime used by the host.
    /// </summary>
    public PluginRuntimeManager PluginRuntime { get; }

    /// <summary>
    /// Gets the SQLite database of this instance, shared by the list of sessions and the plugins. It is a borrowed view:
    /// the host disposes it when it opened it.
    /// </summary>
    public ApplicationDatabase ApplicationDatabase { get; }

    /// <summary>
    /// Gets the current project descriptor used for host composition.
    /// </summary>
    public ProjectDescriptor CurrentProject { get; }

    // A first run on a separate state root starts from the per-project provider/model choices and the
    // navigator settings of the instance that owns the global root. Best effort: that instance may be
    // writing its view state right now, and a session can always choose its provider itself.
    private static async Task SeedViewStateAsync(SessionViewCatalog catalog, string globalRoot, CancellationToken cancellationToken)
    {
        try
        {
            await catalog.SeedViewStateFromAsync(new SessionViewCatalog(new CatalogOptions { GlobalRoot = globalRoot }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or System.Text.DecoderFallbackException or SharpYaml.YamlException)
        {
            LogManager.GetLogger("CodeAlta.Host").Warn($"The view state of the separate state root was not seeded: {exception.Message}");
        }
    }

    /// <summary>
    /// Creates a shared CodeAlta host.
    /// </summary>
    /// <param name="options">Host composition options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created host.</returns>
    /// <remarks>
    /// On failure, awaits best-effort cleanup of successfully acquired runtime, hub, registry,
    /// owned plugin and owned logging resources in normal disposal order. Caller cancellation
    /// does not skip rollback. A supplied plugin remains borrowed, and durable bootstrap effects remain.
    /// This cannot recover resources hidden by a throwing constructor or unpublished plugin activation
    /// state, and does not guarantee termination of work left active by failed child cleanup.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or its plugin startup feedback is null.</exception>
    /// <exception cref="Exception">Creation failed and rollback succeeded; the original exception is propagated.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled and rollback succeeded.</exception>
    /// <exception cref="AggregateException">Creation and rollback both failed; their direct exceptions are retained in that order without flattening.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the plugin authoring profile is invalid, before host acquisition.</exception>
    /// <exception cref="ArgumentException">Scoped host roots are missing, not absolute, or the project is outside the instruction boundary.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Owned receipt capacity is not positive, before host acquisition.</exception>
    /// <exception cref="ArgumentException">An explicit builtin skill root is blank or not fully qualified.</exception>
    /// <exception cref="ArgumentException">
    /// The scratchpad root is not an absolute path, a host with read-only global state is given a startup scratchpad cleanup,
    /// the configuration options are null, or the session lease owner is not a short name without control characters.
    /// </exception>
    public static async Task<CodeAltaHost> CreateAsync(
        CodeAltaHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PluginStartupFeedback);
        if (!Enum.IsDefined(options.PluginAuthoringProfile)) throw new ArgumentOutOfRangeException(nameof(options.PluginAuthoringProfile));
        options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OwnedCommandReceiptCapacity);
        if (options.ScratchpadRoot is { } scratchpadRoot && (string.IsNullOrWhiteSpace(scratchpadRoot) || !Path.IsPathFullyQualified(scratchpadRoot)))
            throw new ArgumentException("The scratchpad root is an absolute path.", nameof(options));
        if (options.ReadOnlyGlobalState && options.StartupScratchpadCleanup is not null)
            throw new ArgumentException("A host with read-only global state runs no startup scratchpad cleanup.", nameof(options));
        var builtInSkillRootProvider = options.BuiltInSkillRoot is null
            ? new BuiltInCodeAltaSkillRootProvider()
            : new BuiltInCodeAltaSkillRootProvider(options.BuiltInSkillRoot);

        var retentionProtection = options.RetentionProtection ?? new SessionRetentionProtection();
        // Borrowed plugin workers may already be running without this registry. They cannot be retroactively
        // qualified by an empty snapshot or by assigning a new property after startup.
        if (options.PrestartedPluginRuntime is { } prestarted
            && (!prestarted.IsStarted || !ReferenceEquals(prestarted.SessionRetentionAdmission, retentionProtection)))
            retentionProtection.MarkUnknown("prestarted-plugins", "external_admission_unqualified");
        PluginRuntimeManager? pluginRuntime = null;
        ModelProviderRegistry? modelProviderRegistry = null;
        AgentHub? agentHub = null;
        SessionRuntimeService? runtimeService = null;
        var ownsPluginRuntime = false;
        var ownsLogging = false;
        ApplicationDatabase? applicationDatabase = null;
        ApplicationDatabase? ownedApplicationDatabase = null;

        try
        {
            var globalRoot = string.IsNullOrWhiteSpace(options.GlobalRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta")
                : Path.GetFullPath(options.GlobalRoot);
            var readOnlyGlobalState = options.ReadOnlyGlobalState;
            if (!readOnlyGlobalState) Directory.CreateDirectory(globalRoot);
            var catalogOptions = new CatalogOptions
            {
                GlobalRoot = globalRoot,
                StateRoot = string.IsNullOrWhiteSpace(options.StateRoot) ? globalRoot : Path.GetFullPath(options.StateRoot),
                Config = options.Config ?? throw new ArgumentException("The host has configuration options.", nameof(options)),
            };
            Directory.CreateDirectory(catalogOptions.StateRoot);
            var startupScratchpadCleanup = options.StartupScratchpadCleanup;
            if (startupScratchpadCleanup is not null
                && (!string.Equals(startupScratchpadCleanup.StateRoot, catalogOptions.StateRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !string.Equals(startupScratchpadCleanup.GlobalRoot, globalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                throw new ArgumentException("Startup scratchpad ownership must match the host roots.", nameof(options));
            // No hub, actor, provider or runnable host has been constructed here yet. All execution routes
            // (including resumed/autonomous work) inherit this barrier instead of each adding a waiter.
            // Never use WaitAsync(token): cancellation cannot release admission while deletion runs.
            if (startupScratchpadCleanup is not null)
            {
                var enabled = false;
                var configurationValid = false;
                try
                {
                    // Read only; never materialize defaults or rewrite the normal instance's shared
                    // configuration during developer startup. Only an actually missing file defaults on.
                    enabled = new CodeAltaConfigStore(catalogOptions).LoadGlobalStorageSettings().ClearScratchpadOnStartup;
                    configurationValid = true;
                }
                catch (Exception)
                {
                    // No unreadable, malformed or semantically invalid policy authorizes deletion.
                    // The worker retains a bounded invalid-configuration result for late subscribers.
                }
                await startupScratchpadCleanup.RunAsync(enabled, configurationValid, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            options.StartupStep?.Invoke("Opening the database…");
            // One database for the instance: the list of sessions and the plugins use the same file and the same queue of writers.
            applicationDatabase = options.ApplicationDatabase ?? options.PrestartedPluginRuntime?.ApplicationDatabase;
            if (applicationDatabase is null)
            {
                applicationDatabase = ownedApplicationDatabase = ApplicationDatabase.Create(catalogOptions);
            }

            // A host that leaves the shared state as it is makes no copy of a database it may share or throw away.
            if (!readOnlyGlobalState) applicationDatabase.StartMaintenance();
            // The owner of the global root keeps the coordinator instructions current; a second instance on a
            // separate state root is usually another build, and only supplies them when they are missing.
            if (!readOnlyGlobalState && (!catalogOptions.HasSeparateStateRoot || !File.Exists(Path.Combine(globalRoot, "AGENTS.md"))))
                _ = CoordinatorAgentsBootstrapper.Ensure(globalRoot);
            if (options.OwnsLogging && !LogManager.IsInitialized)
            {
                LogManager.InitializeForAsync(new LogManagerConfig());
                ownsLogging = true;
            }

            var currentProjectPath = string.IsNullOrWhiteSpace(options.CurrentProjectPath)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(options.CurrentProjectPath);
            options.StartupStep?.Invoke("Reading the projects…");
            var projectCatalog = new ProjectCatalog(catalogOptions) { IsReadOnly = readOnlyGlobalState };
            var currentProject = await ResolveCurrentProjectAsync(projectCatalog, currentProjectPath, cancellationToken).ConfigureAwait(false);

            pluginRuntime = options.PrestartedPluginRuntime ?? new PluginRuntimeManager();
            ownsPluginRuntime = options.PrestartedPluginRuntime is null;
            if (options.StartPlugins && options.PrestartedPluginRuntime is null)
            {
                options.StartupStep?.Invoke("Starting the plugins…");
                await pluginRuntime.StartAsync(
                        new PluginRuntimeManagerOptions
                        {
                            GlobalRoot = globalRoot,
                            SessionRetentionAdmission = retentionProtection,
                            ApplicationDatabase = applicationDatabase,
                            ProjectContext = new PluginProjectContext
                            {
                                ProjectId = currentProject.Id,
                                ProjectPath = currentProject.ProjectPath,
                            },
                            SafeMode = options.PluginSafeMode,
                            IsHeadless = options.IsHeadless,
                            Frontend = options.PluginFrontend,
                            StartupFeedback = options.PluginStartupFeedback,
                            AuthoringProfile = options.PluginAuthoringProfile,
                            WaitForEnterAfterBuildLiveOutput = options.WaitForEnterAfterPluginLiveOutput,
                            RawArguments = options.RawArguments,
                            BuiltIns = options.PluginBuiltIns,
                            Services = options.PluginServices,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            options.StartupStep?.Invoke("Preparing the sessions, skills and providers…");
            var sessionJournalFile = new AgentSessionJournalFile();
            var sessionViewCatalog = new SessionViewCatalog(catalogOptions, sessionJournalFile, serializer: null, applicationDatabase);
            if (catalogOptions.HasSeparateStateRoot && !readOnlyGlobalState)
                await SeedViewStateAsync(sessionViewCatalog, globalRoot, cancellationToken).ConfigureAwait(false);
            var pluginOperationOptions = CreatePluginOperationOptions(options, catalogOptions, currentProject);
            var skillCatalog = new SkillCatalog([
                new ProjectCodeAltaSkillRootProvider(),
                new ProjectCommonSkillRootProvider(),
                new ProjectCopilotSkillRootProvider(),
                new UserCodeAltaSkillRootProvider(),
                new UserCommonSkillRootProvider(),
                new UserCopilotSkillRootProvider(),
                builtInSkillRootProvider,
                new PluginSkillRootProvider(() => pluginRuntime.Adapter.GetResources(pluginRuntime.ActivePlugins, pluginOperationOptions)),
            ]);
            var instructionTemplateProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions, contentLocator: null, configStore: null, discoveryScope: options.DiscoveryScope)
            {
                AppliesSpaceIdentities = options.AppliesSpaceIdentities,
                SkillPromptCatalog = options.SkillPromptCatalog,
                PreferredSkills = options.PreferredSkills,
                ScratchpadRoot = options.ScratchpadRoot is { } scratchpad ? Path.GetFullPath(scratchpad) : null,
                ListsSkills = options.ListsSkills,
            };
            // The providers that list the conversations of other programs keep what they read of their files in the
            // database, so that a start reads only the files that changed.
            modelProviderRegistry = new ModelProviderRegistry { ExternalConversationHeaders = new ExternalConversationHeaderStore(applicationDatabase) };
            options.ConfigureModelProviders?.Invoke(modelProviderRegistry);
            options.ConfigurePluginModelProviders?.Invoke(modelProviderRegistry, pluginRuntime);
            var modelProviderInitializationService = new ModelProviderInitializationService(modelProviderRegistry, null, retentionProtection);
            agentHub = new AgentHub(modelProviderRegistry, catalogOptions.StateRoot, sessionViewCatalog.JournalStore.ProjectionCache, retentionProtection);
            var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
            var projectFileSnapshotCache = new ProjectFileSnapshotCache();
            var eventFailurePolicy = options.PluginAgentEventFailurePolicy ??
                ((RuntimePluginAgentEventEnvelope envelope, Exception failure) =>
                {
                    LogManager.GetLogger("CodeAlta.Host").Error(failure, $"Plugin agent event observer failed for session {envelope.SessionId}");
                    return ValueTask.CompletedTask;
                });
            var eventObserver = new RuntimePluginAgentEventObserver(pluginRuntime, async (envelope, failure) =>
            {
                await eventFailurePolicy(envelope, failure).ConfigureAwait(false);
                if (OwnedProviderEventForwarding.HasRetention(failure)) ExceptionDispatchInfo.Throw(failure);
            });
            // The review of commands and file changes, and the automatic approval that is its opposite: one
            // policy when the host has a live one, the two fixed options otherwise.
            // A host that trusts its sessions fully reviews nothing.
            var reviewOwnedPermissions = options.FullTrust ? static () => false
                : options.ReviewOwnedPermissionsPolicy ?? (() => options.ReviewOwnedCommandPermissions);
            Func<bool> autoApproveOwnedPermissions = options.FullTrust ? static () => true
                : options.ReviewOwnedPermissionsPolicy is { } live
                    ? () => !live()
                    : () => options.AutoApproveOwnedPermissions && !options.ReviewOwnedCommandPermissions;
            runtimeService = new SessionRuntimeService(
                agentHub,
                agentSessionCatalog,
                projectCatalog,
                sessionViewCatalog,
                instructionTemplateProvider,
                catalogOptions,
                skillCatalog,
                autoApproveOwnedPermissions)
            {
                FileSearchCache = projectFileSnapshotCache,
                PluginEventObserver = eventObserver,
                PluginEventCurrentProjectId = currentProject.Id,
                PluginEventCurrentProjectPath = currentProject.ProjectPath,
                SessionPermissionModes = options.SessionPermissionModes || options.FullTrust,
                FullTrust = options.FullTrust,
                Headless = options.HeadlessInstructions,
                InheritPermissionMode = options.InheritPermissionModePolicy,
                ShowsPromptSuggestions = options.ShowsPromptSuggestions,
                Identities = options.AppliesSpaceIdentities
                    ? new SessionIdentityResolver(new SpaceCatalog(projectCatalog), projectProfiles: new CodeAltaConfigStore(catalogOptions).LoadProjectCliProfiles)
                    : SessionIdentityResolver.None,
                AskWhenConversationHeld = options.AskWhenConversationHeld,
                ScratchpadRoot = instructionTemplateProvider.ScratchpadRoot,
                Leases = options.SessionLeaseOwner is { } leaseOwner
                    ? new SessionLeaseRegistry(new SessionLeaseStore(catalogOptions), new SessionLeaseOwner(leaseOwner, Guid.NewGuid().ToString("D")))
                    : null,
            };
            var projectFileSearchService = new ProjectFileSearchService(
                projectFileSnapshotCache,
                new InMemoryProjectFileUsageStore());

            return new CodeAltaHost(
                catalogOptions,
                projectCatalog,
                sessionViewCatalog,
                skillCatalog,
                modelProviderRegistry,
                modelProviderInitializationService,
                agentSessionCatalog,
                agentHub,
                runtimeService,
                projectFileSearchService,
                pluginRuntime,
                ownsPluginRuntime,
                ownsLogging,
                applicationDatabase,
                ownedApplicationDatabase,
                currentProject,
                options.OwnedCommandReceiptCapacity,
                reviewOwnedPermissions,
                options.EnableOwnedAsks,
                options.EnableOwnedUserInput,
                startupScratchpadCleanup,
                retentionProtection);
        }
        catch (Exception creationFailure)
        {
            var acquisitions = new { Plugin = pluginRuntime ?? options.PrestartedPluginRuntime, Runtime = runtimeService,
                Hub = agentHub, Providers = modelProviderRegistry, Options = options, OwnsPlugins = ownsPluginRuntime, OwnsLogging = ownsLogging };
            if (OwnedProviderEventForwarding.HasRetention(creationFailure))
            {
                var failures = new List<Exception> { creationFailure };
                var drain = runtimeService is null ? null : new HostDisposalStage(() => runtimeService.DrainRetainedDependenciesAsync(creationFailure).AsTask());
                if (drain is not null)
                {
                    drain.Launch();
                    if (await drain.ReportedOutcome.ConfigureAwait(false) is { } drainFailure) failures.Add(drainFailure);
                }
                throw new AgentDependencyRetentionException("host creation", "retained inner acquisition", failures,
                    new { Acquisitions = acquisitions, Drain = drain });
            }
            try
            {
                await PluginEventDependencyBarrier.BeforeRollbackAsync(
                    pluginRuntime ?? options.PrestartedPluginRuntime, creationFailure, acquisitions).ConfigureAwait(false);
            }
            catch (Exception barrierFailure) when (OwnedProviderEventForwarding.HasRetention(barrierFailure))
            {
                // Retain this exact outer inventory even when an inner plugin marker already has OuterDependencies.
                throw new AgentDependencyRetentionException("host creation", "plugin barrier", [barrierFailure], acquisitions);
            }
            try
            {
                await RollbackHostCreationAsync(
                    creationFailure,
                    () => runtimeService?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => agentHub?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => modelProviderRegistry?.DisposeAsync() ?? ValueTask.CompletedTask,
                    () => pluginRuntime?.DisposeAsync() ?? ValueTask.CompletedTask,
                    LogManager.Shutdown,
                    ownsPluginRuntime,
                    ownsLogging).ConfigureAwait(false);
            }
            finally
            {
                // The database the creation opened goes with the creation that failed.
                if (ownedApplicationDatabase is not null) await ownedApplicationDatabase.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<ProjectDescriptor> ResolveCurrentProjectAsync(
        ProjectCatalog projectCatalog,
        string currentProjectPath,
        CancellationToken cancellationToken)
    {
        var projects = await projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = projects.FirstOrDefault(project =>
            string.Equals(NormalizePath(project.ProjectPath), NormalizePath(currentProjectPath), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Archived = false;
            return existing;
        }

        return CreateTransientProject(currentProjectPath, projects);
    }

    private static ProjectDescriptor CreateTransientProject(string projectPath, IReadOnlyList<ProjectDescriptor> knownProjects)
    {
        var normalizedPath = NormalizePath(projectPath);
        var projectName = ProjectPathNameFormatter.InferName(normalizedPath);
        var displayName = ProjectPathNameFormatter.InferDisplayName(normalizedPath);
        var baseSlug = Slugify(projectName);
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = EnsureUniqueSlug(baseSlug, knownProjects),
            Name = projectName,
            DisplayName = displayName,
            ProjectPath = normalizedPath,
            DefaultBranch = "main",
            MarkdownBody = $"# {displayName}\n\nTransient project context for the current host process.",
        };
        project.Validate();
        return project;
    }

    private static string EnsureUniqueSlug(string baseSlug, IReadOnlyList<ProjectDescriptor> projects)
    {
        var candidate = baseSlug;
        var suffix = 2;
        var usedSlugs = projects
            .Select(static project => project.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (usedSlugs.Contains(candidate))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }

    internal static PluginAdapterOperationOptions CreatePluginOperationOptions(
        CodeAltaHostOptions options,
        CatalogOptions catalogOptions,
        ProjectDescriptor currentProject)
        => new()
        {
            ProjectId = currentProject.Id,
            ProjectPath = currentProject.ProjectPath,
            HasInteractiveUi = options.HasInteractiveUi && !options.IsHeadless,
            IsHeadless = options.IsHeadless,
            ConfigurationPaths = [Path.Combine(catalogOptions.GlobalRoot, "config.toml")],
            Environment = options.PluginEnvironment is not null
                ? new Dictionary<string, string?>(options.PluginEnvironment, StringComparer.OrdinalIgnoreCase)
                : Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(static entry => entry.Key is string)
                .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>
    /// Awaits the single best-effort disposal operation for this host's owned services.
    /// </summary>
    /// <returns>The same underlying operation for repeated or concurrent callers, including its terminal failure.</returns>
    /// <remarks>
    /// Starts read/command shutdown and plugin quiescence before joining their originals, then attempts
    /// runtime, hub, registry, owned plugin and owned logging cleanup in order after ordinary failures.
    /// Explicit retained-dependency failures prohibit later dependent releases; runtime instead drains
    /// independent controls and genuinely active work without disposing retained dependencies.
    /// A lone failure is rethrown unchanged; multiple failures retain their direct references in execution order,
    /// without flattening aggregates. Cancellation is recorded like other failures and does not skip later stages.
    /// There are no retries or hard timeout guarantees. Reentrant disposal of this same host is unsupported.
    /// Completion does not guarantee that failed or noncooperative child services have terminated all work.
    /// </remarks>
    /// <exception cref="Exception">A single cleanup stage failed; the original exception is propagated.</exception>
    /// <exception cref="OperationCanceledException">The only cleanup failure was cancellation.</exception>
    /// <exception cref="AggregateException">Multiple cleanup stages failed.</exception>
    /// <exception cref="AgentDependencyRetentionException">A required prerequisite was not released; dependent acquisitions remain owned.</exception>
    public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask);

    private async ValueTask DisposeModelProvidersAsync()
    {
        await ModelProviderInitializationService.DrainAsync().ConfigureAwait(false);
        await ModelProviderRegistry.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes owned read/command admission and starts their independent shutdown controls without joining them.</summary>
    /// <remarks>
    /// The host retains both original operations and their outcomes and joins them during disposal.
    /// This does not dispose runtime, provider, plugin or logging dependencies. Frontend owners may
    /// call it before joining their own plugin barrier; repeated calls do not retry either control.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An admitted plugin callback would initiate its own shutdown.</exception>
    public void BeginShutdownControls()
    {
        PluginRuntime.ThrowIfAgentEventSelfJoin();
        _earlyReadShutdown.Launch();
        _earlyCommandShutdown.Launch();
    }

    private async ValueTask DisposeCommandsAndRuntimeAsync()
    {
        BeginShutdownControls();
        await DisposeOwnedWorkAsync(
            () => _earlyCommandShutdown.Reported,
            () => _earlyReadShutdown.Reported,
            RuntimeService.DisposeAsync,
            RuntimeService.DrainRetainedDependenciesAsync,
            PluginRuntime.QuiesceAgentEventsAsync).ConfigureAwait(false);
    }

    // Mandatory callback seam for this existing host stage, not a replacement lifetime owner.
    internal static async ValueTask DisposeOwnedWorkAsync(
        Func<Task> disposeCommands, Func<Task> disposeReads, Func<ValueTask> disposeRuntime,
        Func<Exception, ValueTask> drainRetainedRuntime, Func<Task> quiescePlugins)
    {
        ArgumentNullException.ThrowIfNull(disposeCommands);
        ArgumentNullException.ThrowIfNull(disposeReads);
        ArgumentNullException.ThrowIfNull(disposeRuntime);
        ArgumentNullException.ThrowIfNull(drainRetainedRuntime);
        ArgumentNullException.ThrowIfNull(quiescePlugins);
        Exception? retained = null;
        var reads = new HostDisposalStage(disposeReads);
        var commands = new HostDisposalStage(disposeCommands);
        var plugins = new HostDisposalStage(quiescePlugins);
        var runtime = new HostDisposalStage(() => (retained is null ? disposeRuntime() : drainRetainedRuntime(retained)).AsTask());
        // Own every receipt before invocation. Close read admission and signal commands before any join,
        // including plugin quiescence: plugin work may itself await one of these owned operations.
        reads.Launch();
        commands.Launch();
        plugins.Launch();
        var failures = new List<Exception>();
        if (await commands.ReportedOutcome.ConfigureAwait(false) is { } commandFailure) failures.Add(commandFailure);
        if (await reads.ReportedOutcome.ConfigureAwait(false) is { } readFailure) failures.Add(readFailure);
        if (await plugins.ReportedOutcome.ConfigureAwait(false) is { } pluginFailure)
            failures.Add(new AgentDependencyRetentionException("host", "plugin quiescence", [pluginFailure], plugins));
        if (failures.Any(OwnedProviderEventForwarding.HasRetention))
            retained = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        runtime.Launch();
        if (await runtime.ReportedOutcome.ConfigureAwait(false) is { } runtimeFailure) failures.Add(runtimeFailure);
        if (failures.Any(OwnedProviderEventForwarding.HasRetention))
            throw new AgentDependencyRetentionException("host", "owned work drainage", failures,
                new { Commands = commands, Reads = reads, Plugins = plugins, Runtime = runtime });
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    internal sealed class HostDisposalStage
    {
        private readonly Func<Task> _operation;
        private readonly TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<Exception?>? _outcome;
        private int _launched;
        internal HostDisposalStage(Func<Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            _operation = operation;
            Reported = ReportAsync();
            ReportedOutcome = ObserveAsync(Reported);
        }
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? AwaitedFailure { get; private set; }
        internal Exception? InvocationFailure { get; private set; }
        internal Task? Observer => _outcome;
        internal Task Reported { get; }
        internal Task<Exception?> ReportedOutcome { get; }
        internal Task<Exception?> Outcome => _outcome ?? throw new InvalidOperationException("The host cleanup invocation has not launched.");
        internal void Launch()
        {
            if (Interlocked.Exchange(ref _launched, 1) != 0) return;
            try
            {
                Original = _operation() ?? throw new InvalidOperationException("A cleanup callback returned no original.");
                _outcome = ObserveOriginalAsync(Original);
            }
            catch (Exception failure)
            {
                InvocationFailure = failure;
                // A synchronous callback failure is a completed ordinary failure, not evidence that
                // work escaped. Only an explicit retention marker may stop dependent cleanup.
                _outcome = Task.FromResult<Exception?>(failure);
            }
            finally { _published.TrySetResult(); }
        }
        private async Task ReportAsync()
        {
            await _published.Task.ConfigureAwait(false);
            if (await Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);
        }
        private async Task<Exception?> ObserveOriginalAsync(Task original)
        {
            try { await original.ConfigureAwait(false); return null; }
            catch (Exception failure)
            {
                AwaitedFailure = failure;
                OriginalFaults = original.Exception;
                return OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
            }
        }
        private static async Task<Exception?> ObserveAsync(Task original)
        {
            try { await original.ConfigureAwait(false); return null; }
            catch (Exception failure) { return failure; }
        }
    }

    /// <summary>
    /// Creates a lazy, single-execution host cleanup operation from mandatory, caller-supplied operations.
    /// </summary>
    /// <remarks>
    /// Validates every operation in parameter order without invoking it, including borrowed plugin/logging operations.
    /// Uses default execution-and-publication; first access starts inline. Borrowed operations remain uncalled.
    /// Callbacks must not recursively access the resulting lazy value or await its own disposal task.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    internal static Lazy<Task> CreateHostDisposal(
        Func<ValueTask> disposeRuntimeService,
        Func<ValueTask> disposeAgentHub,
        Func<ValueTask> disposeModelProviderRegistry,
        Func<ValueTask> disposePluginRuntime,
        Action shutdownLogging,
        bool ownsPluginRuntime,
        bool ownsLogging)
    {
        ArgumentNullException.ThrowIfNull(disposeRuntimeService);
        ArgumentNullException.ThrowIfNull(disposeAgentHub);
        ArgumentNullException.ThrowIfNull(disposeModelProviderRegistry);
        ArgumentNullException.ThrowIfNull(disposePluginRuntime);
        ArgumentNullException.ThrowIfNull(shutdownLogging);

        return new Lazy<Task>(() => DisposeHostCoreAsync(
            disposeRuntimeService,
            disposeAgentHub,
            disposeModelProviderRegistry,
            disposePluginRuntime,
            shutdownLogging,
            ownsPluginRuntime,
            ownsLogging));
    }

    private static async Task DisposeHostCoreAsync(
        Func<ValueTask> disposeRuntimeService,
        Func<ValueTask> disposeAgentHub,
        Func<ValueTask> disposeModelProviderRegistry,
        Func<ValueTask> disposePluginRuntime,
        Action shutdownLogging,
        bool ownsPluginRuntime,
        bool ownsLogging)
    {
        var stages = new[]
        {
            new HostDisposalStage(() => disposeRuntimeService().AsTask()),
            new HostDisposalStage(() => disposeAgentHub().AsTask()),
            new HostDisposalStage(() => disposeModelProviderRegistry().AsTask()),
            new HostDisposalStage(() => disposePluginRuntime().AsTask()),
            new HostDisposalStage(() => { shutdownLogging(); return Task.CompletedTask; }),
        };
        var failures = new List<Exception>();
        for (var index = 0; index < stages.Length; index++)
        {
            if (index == 3 && !ownsPluginRuntime || index == 4 && !ownsLogging) continue;
            stages[index].Launch();
            if (await stages[index].ReportedOutcome.ConfigureAwait(false) is not { } failure) continue;
            failures.Add(failure);
            if (OwnedProviderEventForwarding.HasRetention(failure))
                throw new AgentDependencyRetentionException("host", "dependent service release", failures, stages);
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    /// <summary>
    /// Awaits best-effort rollback of returned host acquisitions, then reports creation failure.
    /// </summary>
    /// <param name="creationFailure">The original creation exception, retained unchanged.</param>
    /// <param name="disposeRuntimeService">Disposes the acquired runtime, or does nothing if none returned.</param>
    /// <param name="disposeAgentHub">Disposes the acquired hub, or does nothing if none returned.</param>
    /// <param name="disposeModelProviderRegistry">Disposes the acquired registry, or does nothing if none returned.</param>
    /// <param name="disposePluginRuntime">Disposes an acquired plugin runtime, or does nothing if none returned.</param>
    /// <param name="shutdownLogging">Shuts down logging only when owned.</param>
    /// <param name="ownsPluginRuntime">Whether this creation acquired an owned rather than borrowed plugin runtime.</param>
    /// <param name="ownsLogging">Whether this creation successfully acquired logging ownership.</param>
    /// <returns>An operation that always reports creation failure, after attempting rollback.</returns>
    /// <remarks>
    /// Validates the creation exception first, then all callbacks synchronously through the existing
    /// disposal factory before starting its traversal inline. Borrowed callbacks remain uncalled.
    /// Cleanup has no caller cancellation or timeout. Never recursively await this creation/rollback
    /// operation from its own callback. Hidden constructor acquisitions, unpublished plugin activation
    /// state and work left active by failed child disposal remain outside this rollback guarantee.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The creation exception or a mandatory callback is null.</exception>
    /// <exception cref="Exception">Rollback succeeded; the original creation exception is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">Creation was canceled and rollback succeeded.</exception>
    /// <exception cref="AggregateException">Rollback failed; creation and rollback failures are two direct, ordered references without flattening.</exception>
    internal static Task RollbackHostCreationAsync(
        Exception creationFailure,
        Func<ValueTask> disposeRuntimeService,
        Func<ValueTask> disposeAgentHub,
        Func<ValueTask> disposeModelProviderRegistry,
        Func<ValueTask> disposePluginRuntime,
        Action shutdownLogging,
        bool ownsPluginRuntime,
        bool ownsLogging)
    {
        ArgumentNullException.ThrowIfNull(creationFailure);
        var disposal = CreateHostDisposal(
            disposeRuntimeService,
            disposeAgentHub,
            disposeModelProviderRegistry,
            disposePluginRuntime,
            shutdownLogging,
            ownsPluginRuntime,
            ownsLogging);
        return RollbackHostCreationCoreAsync(creationFailure, disposal);
    }

    private static async Task RollbackHostCreationCoreAsync(Exception creationFailure, Lazy<Task> disposal)
    {
        if (OwnedProviderEventForwarding.HasRetention(creationFailure))
            throw new AgentDependencyRetentionException("host creation", "retained inner acquisition", [creationFailure], disposal);
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
}
