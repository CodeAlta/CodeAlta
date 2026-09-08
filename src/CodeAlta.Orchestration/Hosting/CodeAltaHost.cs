using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
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
        ProjectDescriptor currentProject)
    {
        CatalogOptions = catalogOptions;
        ProjectCatalog = projectCatalog;
        SessionViewCatalog = sessionViewCatalog;
        SkillCatalog = skillCatalog;
        ModelProviderRegistry = modelProviderRegistry;
        ModelProviderInitializationService = modelProviderInitializationService;
        AgentSessionCatalog = agentSessionCatalog;
        AgentHub = agentHub;
        RuntimeService = runtimeService;
        ProjectFileSearchService = projectFileSearchService;
        PluginRuntime = pluginRuntime;
        CurrentProject = currentProject;
        _disposeTask = CreateHostDisposal(
            RuntimeService.DisposeAsync,
            AgentHub.DisposeAsync,
            ModelProviderRegistry.DisposeAsync,
            PluginRuntime.DisposeAsync,
            LogManager.Shutdown,
            ownsPluginRuntime,
            ownsLogging);
    }

    /// <summary>
    /// Gets the catalog options used by the host.
    /// </summary>
    public CatalogOptions CatalogOptions { get; }

    /// <summary>
    /// Gets the project catalog.
    /// </summary>
    public ProjectCatalog ProjectCatalog { get; }

    /// <summary>
    /// Gets the session-view catalog.
    /// </summary>
    public SessionViewCatalog SessionViewCatalog { get; }

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

    /// <summary>
    /// Gets the project-file search service.
    /// </summary>
    public IProjectFileSearchService ProjectFileSearchService { get; }

    /// <summary>
    /// Gets the plugin runtime used by the host.
    /// </summary>
    public PluginRuntimeManager PluginRuntime { get; }

    /// <summary>
    /// Gets the current project descriptor used for host composition.
    /// </summary>
    public ProjectDescriptor CurrentProject { get; }

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
    public static async Task<CodeAltaHost> CreateAsync(
        CodeAltaHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PluginStartupFeedback);
        if (!Enum.IsDefined(options.PluginAuthoringProfile)) throw new ArgumentOutOfRangeException(nameof(options.PluginAuthoringProfile));

        PluginRuntimeManager? pluginRuntime = null;
        ModelProviderRegistry? modelProviderRegistry = null;
        AgentHub? agentHub = null;
        SessionRuntimeService? runtimeService = null;
        var ownsPluginRuntime = false;
        var ownsLogging = false;

        try
        {
            var globalRoot = string.IsNullOrWhiteSpace(options.GlobalRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta")
                : Path.GetFullPath(options.GlobalRoot);
            Directory.CreateDirectory(globalRoot);
            _ = CoordinatorAgentsBootstrapper.Ensure(globalRoot);
            if (options.OwnsLogging && !LogManager.IsInitialized)
            {
                LogManager.InitializeForAsync(new LogManagerConfig());
                ownsLogging = true;
            }

            var currentProjectPath = string.IsNullOrWhiteSpace(options.CurrentProjectPath)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(options.CurrentProjectPath);
            var catalogOptions = new CatalogOptions
            {
                GlobalRoot = globalRoot,
            };
            var projectCatalog = new ProjectCatalog(catalogOptions);
            var currentProject = await ResolveCurrentProjectAsync(projectCatalog, currentProjectPath, cancellationToken).ConfigureAwait(false);

            pluginRuntime = options.PrestartedPluginRuntime ?? new PluginRuntimeManager();
            ownsPluginRuntime = options.PrestartedPluginRuntime is null;
            if (options.StartPlugins && options.PrestartedPluginRuntime is null)
            {
                await pluginRuntime.StartAsync(
                        new PluginRuntimeManagerOptions
                        {
                            GlobalRoot = globalRoot,
                            ProjectContext = new PluginProjectContext
                            {
                                ProjectId = currentProject.Id,
                                ProjectPath = currentProject.ProjectPath,
                            },
                            SafeMode = options.PluginSafeMode,
                            IsHeadless = options.IsHeadless,
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

            var sessionJournalFile = new AgentSessionJournalFile();
            var sessionViewCatalog = new SessionViewCatalog(catalogOptions, sessionJournalFile);
            var pluginOperationOptions = CreatePluginOperationOptions(options, catalogOptions, currentProject);
            var skillCatalog = new SkillCatalog([
                new ProjectCodeAltaSkillRootProvider(),
                new ProjectCommonSkillRootProvider(),
                new UserCodeAltaSkillRootProvider(),
                new UserCommonSkillRootProvider(),
                new BuiltInCodeAltaSkillRootProvider(),
                new PluginSkillRootProvider(() => pluginRuntime.Adapter.GetResources(pluginRuntime.ActivePlugins, pluginOperationOptions)),
            ]);
            var instructionTemplateProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions);
            modelProviderRegistry = new ModelProviderRegistry();
            options.ConfigureModelProviders?.Invoke(modelProviderRegistry);
            var modelProviderInitializationService = new ModelProviderInitializationService(modelProviderRegistry);
            agentHub = new AgentHub(modelProviderRegistry, globalRoot, sessionViewCatalog.JournalStore.ProjectionCache);
            var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
            runtimeService = new SessionRuntimeService(
                agentHub,
                agentSessionCatalog,
                projectCatalog,
                sessionViewCatalog,
                instructionTemplateProvider,
                catalogOptions,
                skillCatalog);
            var projectFileSearchService = new ProjectFileSearchService(
                new ProjectFileSnapshotCache(),
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
                currentProject);
        }
        catch (Exception creationFailure)
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

    private static PluginAdapterOperationOptions CreatePluginOperationOptions(
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
            Environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(static entry => entry.Key is string)
                .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>
    /// Awaits the single best-effort disposal operation for this host's owned services.
    /// </summary>
    /// <returns>The same underlying operation for repeated or concurrent callers, including its terminal failure.</returns>
    /// <remarks>
    /// Attempts runtime, hub, registry, owned plugin and owned logging cleanup in order, even after a stage fails.
    /// A lone failure is rethrown unchanged; multiple failures retain their direct references in execution order,
    /// without flattening aggregates. Cancellation is recorded like other failures and does not skip later stages.
    /// There are no retries or hard timeout guarantees. Reentrant disposal of this same host is unsupported.
    /// Completion does not guarantee that failed or noncooperative child services have terminated all work.
    /// </remarks>
    /// <exception cref="Exception">A single cleanup stage failed; the original exception is propagated.</exception>
    /// <exception cref="OperationCanceledException">The only cleanup failure was cancellation.</exception>
    /// <exception cref="AggregateException">Multiple cleanup stages failed.</exception>
    public ValueTask DisposeAsync() => new(_disposeTask.Value);

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
        List<Exception>? failures = null;
        try
        {
            await disposeRuntimeService().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeAgentHub().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeModelProviderRegistry().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (ownsPluginRuntime)
        {
            try
            {
                await disposePluginRuntime().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
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
