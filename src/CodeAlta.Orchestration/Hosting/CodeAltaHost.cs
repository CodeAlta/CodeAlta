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
public sealed class CodeAltaHost : IAsyncDisposable
{
    private readonly bool _ownsPluginRuntime;
    private readonly bool _ownsLogging;

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
        _ownsPluginRuntime = ownsPluginRuntime;
        _ownsLogging = ownsLogging;
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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public static async Task<CodeAltaHost> CreateAsync(
        CodeAltaHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var globalRoot = string.IsNullOrWhiteSpace(options.GlobalRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta")
            : Path.GetFullPath(options.GlobalRoot);
        Directory.CreateDirectory(globalRoot);
        _ = CoordinatorAgentsBootstrapper.Ensure(globalRoot);
        var ownsLogging = false;
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

        var pluginRuntime = options.PrestartedPluginRuntime ?? new PluginRuntimeManager();
        var ownsPluginRuntime = options.PrestartedPluginRuntime is null;
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
        var modelProviderRegistry = new ModelProviderRegistry();
        options.ConfigureModelProviders?.Invoke(modelProviderRegistry);
        var modelProviderInitializationService = new ModelProviderInitializationService(modelProviderRegistry);
        var agentHub = new AgentHub(modelProviderRegistry, globalRoot, sessionViewCatalog.JournalStore.ProjectionCache);
        var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
        var runtimeService = new SessionRuntimeService(
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await RuntimeService.DisposeAsync().ConfigureAwait(false);
        await AgentHub.DisposeAsync().ConfigureAwait(false);
        await ModelProviderRegistry.DisposeAsync().ConfigureAwait(false);
        if (_ownsPluginRuntime)
        {
            await PluginRuntime.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsLogging)
        {
            LogManager.Shutdown();
        }
    }
}
