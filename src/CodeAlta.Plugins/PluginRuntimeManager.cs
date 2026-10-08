using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Options used to start the CodeAlta plugin runtime.
/// </summary>
public sealed record PluginRuntimeManagerOptions
{
    /// <summary>Gets the explicit source-plugin authoring profile; defaults to Neutral.</summary>
    /// <remarks>Terminal hosts must opt in even on noninteractive or CLI paths. This does not select presentation capabilities.</remarks>
    public PluginAuthoringProfile AuthoringProfile { get; init; } = PluginAuthoringProfile.Neutral;

    /// <summary>Gets the global CodeAlta home directory.</summary>
    public required string GlobalRoot { get; init; }

    /// <summary>Gets the current project descriptor, when project plugins are in scope.</summary>
    public PluginProjectContext? ProjectContext { get; init; }

    /// <summary>Gets a value indicating whether dynamic plugins are disabled for this process.</summary>
    public bool SafeMode { get; init; }

    /// <summary>Gets a value indicating whether the host is running without an interactive UI.</summary>
    public bool IsHeadless { get; init; }

    /// <summary>
    /// Gets the CodeAlta application of the host; the default is <see cref="PluginFrontends.None"/>, a host
    /// without a user interface.
    /// </summary>
    /// <remarks>A plugin that does not support the application of the host is discovered but not started.</remarks>
    public PluginFrontends Frontend { get; init; }

    /// <summary>Gets the raw process arguments visible to startup contributions.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];

    /// <summary>Gets additional built-in plugins to activate before dynamic plugins.</summary>
    public IReadOnlyList<BuiltInPluginDefinition> BuiltIns { get; init; } = [];

    /// <summary>Gets host services exposed to activated plugins.</summary>
    public IPluginServices? Services { get; init; }

    /// <summary>Gets the maximum number of source plugin builds that can run in parallel.</summary>
    public int MaxParallelBuilds { get; init; } = Math.Min(Environment.ProcessorCount, 4);

    /// <summary>Gets a value indicating whether interactive plugin build live output should wait for Enter after builds complete.</summary>
    public bool WaitForEnterAfterBuildLiveOutput { get; init; }

    /// <summary>Gets borrowed startup presentation; the default is silent even for nonheadless callers.</summary>
    /// <remarks>The runtime never disposes this port. Hosts must explicitly inject frontend feedback.</remarks>
    public IPluginStartupFeedback StartupFeedback { get; init; } = new SilentPluginStartupFeedback();
}

/// <summary>
/// Describes the result of starting the plugin runtime.
/// </summary>
public sealed record PluginRuntimeManagerStartResult
{
    /// <summary>Gets activated plugin instances.</summary>
    public IReadOnlyList<ActivePluginInstance> ActivePlugins { get; init; } = [];

    /// <summary>Gets build results produced for enabled source plugins.</summary>
    public IReadOnlyList<PluginBuildResult> BuildResults { get; init; } = [];

    /// <summary>Gets diagnostics raised during startup.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Reusable runtime manager that discovers, builds, loads, activates, adapts, and unloads plugins.
/// </summary>
public sealed partial class PluginRuntimeManager : IAsyncDisposable
{
    private readonly PluginContributionRegistry _registry = new();
    private readonly PluginRuntimeDiagnosticStore _diagnostics = new();
    private readonly List<ActivePluginInstance> _activePlugins = [];
    private readonly object _lock = new();
    private int _activationGeneration;
    private bool _disposed;

    /// <summary>Gets the contribution registry owned by the runtime.</summary>
    public PluginContributionRegistry Registry => _registry;

    /// <summary>
    /// Gets the services the host gave its plugins when it started the runtime, or null before the start
    /// and for a host that gave none. A host that starts its plugins before its user interface reads them
    /// back here to attach that interface later.
    /// </summary>
    public IPluginServices? HostServices { get; private set; }

    /// <summary>Gets the adapter service used by hosts to materialize contribution points.</summary>
    public PluginContributionAdapterService Adapter { get; }

    /// <summary>Initializes a new instance of the <see cref="PluginRuntimeManager"/> class.</summary>
    public PluginRuntimeManager()
    {
        Adapter = new PluginContributionAdapterService(_registry, _diagnostics);
    }

    /// <summary>Gets a snapshot of active plugins.</summary>
    public IReadOnlyList<ActivePluginInstance> ActivePlugins
    {
        get
        {
            lock (_lock)
            {
                return _activePlugins.ToArray();
            }
        }
    }

    /// <summary>Gets a snapshot of runtime diagnostics.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics => _diagnostics.GetSnapshot();

    /// <summary>
    /// Discovers enabled plugins, builds stale source plugins, loads assemblies, activates plugin types, and runs startup hooks.
    /// </summary>
    /// <param name="options">Startup options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The startup result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or its startup feedback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the authoring profile is invalid, before startup acquires resources.</exception>
    /// <exception cref="InvalidOperationException">Startup was already admitted, or manager-wide event admission is closed.</exception>
    public async ValueTask<PluginRuntimeManagerStartResult> StartAsync(
        PluginRuntimeManagerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.StartupFeedback);
        PluginAuthoringPolicy.Validate(options.AuthoringProfile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HostServices = options.Services;
        PluginRuntimeManagerStartResult? result = null;
        await RunOwnedStartAsync(async () => result = await StartOwnedCoreAsync(options, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result!;
    }

    private async Task<PluginRuntimeManagerStartResult> StartOwnedCoreAsync(PluginRuntimeManagerOptions options, CancellationToken cancellationToken)
    {
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var activePlugins = new List<ActivePluginInstance>();
        var buildResults = new List<PluginBuildResult>();
        var hostInfo = CreateHostInfo(options);
        lock (_lock)
        {
            _options = options;
            _hostInfo = hostInfo;
        }

        var configLoad = LoadConfig(options);
        diagnostics.AddRange(configLoad.Diagnostics);
        if (!configLoad.Succeeded)
        {
            _diagnostics.AddRange(diagnostics);
            return new PluginRuntimeManagerStartResult
            {
                ActivePlugins = activePlugins,
                BuildResults = buildResults,
                Diagnostics = diagnostics,
            };
        }

        var globalConfig = configLoad.GlobalConfig!;
        var projectConfig = configLoad.ProjectConfig;
        var activator = new PluginRuntimeActivator(_registry);

        foreach (var builtIn in options.BuiltIns)
        {
            var enablement = new PluginRuntimeConfigResolver().ResolveBuiltInPlugin(builtIn, globalConfig, options.SafeMode);
            if (!enablement.Enabled)
            {
                diagnostics.Add(PluginRuntimeDiagnostic.Info(PluginRuntimeDiagnosticSource.Config, $"Built-in plugin '{builtIn.Id}' skipped: {enablement.Reason}", builtIn.Id));
                continue;
            }

            var discovered = new DiscoveredPluginType
            {
                Type = builtIn.ResolvePluginType(),
                Descriptor = builtIn.CreateDescriptor(),
            };
            if (CreateUnsupportedFrontendDiagnostic(discovered.Descriptor, options.Frontend, builtIn.Id, null) is { } unsupportedBuiltIn)
            {
                diagnostics.Add(unsupportedBuiltIn);
                continue;
            }

            var activation = await activator.ActivateAsync(
                    discovered,
                    sourcePackage: null,
                    loadContext: null,
                    new PluginActivationOptions { HostInfo = hostInfo, Services = options.Services, ActivationGeneration = ++_activationGeneration, BuiltInFactory = builtIn.Factory },
                    cancellationToken)
                .ConfigureAwait(false);
            if (activation.ActivePlugin is not null)
            {
                OwnActivation(activation.ActivePlugin);
                activePlugins.Add(activation.ActivePlugin);
            }
            diagnostics.AddRange(activation.Diagnostics);
        }

        var roots = BuildRoots(options).Where(static root => Directory.Exists(root.RootPath)).ToArray();
        var packages = roots.SelectMany(static root => new SourcePluginDiscoveryService().Discover(root)).ToArray();
        var plan = new PluginStartupPlanner().PlanSourceBuilds(packages, globalConfig, projectConfig, options.SafeMode);
        diagnostics.AddRange(plan.Diagnostics);

        return await PluginStartupFeedbackRouting.RunAsync(
                plan.BuildRequests,
                options.StartupFeedback,
                CompleteStartupAsync,
                static (result, elapsed) => PluginStartupFeedbackReporter.BuildStartupSummary(
                    result.BuildResults,
                    result.ActivePlugins.Count(static plugin => plugin.SourcePackage is not null),
                    elapsed),
                options.IsHeadless,
                options.WaitForEnterAfterBuildLiveOutput,
                cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<PluginRuntimeManagerStartResult> CompleteStartupAsync(IPluginStartupProgress? liveStatus, CancellationToken token)
        {
            await BuildAndActivateSourcePluginsAsync(liveStatus, token).ConfigureAwait(false);

            liveStatus?.MarkActivating();
            var startup = await Adapter.RunStartupAsync(activePlugins, options.RawArguments, CreateAdapterOptions(options), token).ConfigureAwait(false);
            diagnostics.AddRange(startup.Diagnostics);
            _diagnostics.AddRange(diagnostics);
            return new PluginRuntimeManagerStartResult
            {
                ActivePlugins = activePlugins,
                BuildResults = buildResults,
                Diagnostics = diagnostics,
            };
        }

        async ValueTask BuildAndActivateSourcePluginsAsync(IPluginStartupProgress? liveStatus, CancellationToken token)
        {
            if (plan.BuildRequests.Count == 0)
            {
                return;
            }

            liveStatus?.MarkPreparing();
            var generationOptions = CreateBuildFileOptions(options);
            var successfulRoots = new List<string>();
            foreach (var root in plan.BuildRequests.Select(static request => request.Package.Root).DistinctBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase))
            {
                var generation = await new PluginRootBuildFileGenerator().GenerateAsync(root, generationOptions, token).ConfigureAwait(false);
                diagnostics.AddRange(generation.Diagnostics);
                if (generation.Succeeded) successfulRoots.Add(root.RootPath);
            }
            var admittedBuildRequests = PluginAuthoringPolicy.FilterBuildRequests(plan.BuildRequests, successfulRoots);

            liveStatus?.MarkBuilding();
            var cacheRoot = Path.Combine(options.GlobalRoot, "cache");
            var manifestStore = new PluginBuildManifestStore(cacheRoot, ResolveCodeAltaBuildIdentity(), ResolveSdkIdentity());
            var scheduler = new PluginBuildScheduler(new PluginBuildService(manifestStore), new PluginBuildSchedulerOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelBuilds) });
            void OnProgress(object? _, PluginBuildProgress progress)
            {
                liveStatus?.Report(progress);
            }

            if (liveStatus is not null)
            {
                scheduler.ProgressChanged += OnProgress;
            }

            try
            {
                buildResults.AddRange(await scheduler.BuildAsync(admittedBuildRequests, token).ConfigureAwait(false));
            }
            finally
            {
                if (liveStatus is not null)
                {
                    scheduler.ProgressChanged -= OnProgress;
                }
            }

            liveStatus?.MarkBuildsCompleted();
            liveStatus?.MarkActivating();
            foreach (var buildResult in buildResults)
            {
                RememberBuild(buildResult);
                diagnostics.AddRange(buildResult.RuntimeDiagnostics);
                if (!buildResult.Succeeded)
                {
                    continue;
                }

                var stamp = PluginBuildManifestStore.ComputeSourceStamp(buildResult.Package);
                activePlugins.AddRange(await LoadAndActivateAsync(options, hostInfo, buildResult, stamp, diagnostics, token).ConfigureAwait(false));
            }
        }
    }

    /// <summary>Deactivates all active plugins and releases runtime-owned handles.</summary>
    /// <remarks>Permanently closes event/start admission. Cancellation bounds the caller's wait only.</remarks>
    /// <exception cref="InvalidOperationException">The caller would await its own startup/event attempt.</exception>
    public async ValueTask DeactivateAllAsync(CancellationToken cancellationToken = default)
    {
        await DeactivateManagerOriginalAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ThrowIfAgentEventSelfJoin();
        lock (_lock) _disposed = true;
        return new(DeactivateManagerOriginalAsync());
    }

    private static PluginAdapterOperationOptions CreateAdapterOptions(PluginRuntimeManagerOptions options)
        => new()
        {
            ProjectId = options.ProjectContext?.ProjectId,
            ProjectPath = options.ProjectContext?.ProjectPath,
            HasInteractiveUi = !options.IsHeadless,
            ConfigurationPaths = [Path.Combine(options.GlobalRoot, "config.toml")],
            Environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(static entry => entry.Key is string)
                .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase),
        };

    private static IReadOnlyList<PluginRoot> BuildRoots(PluginRuntimeManagerOptions options)
    {
        var roots = new List<PluginRoot>
        {
            new() { RootPath = Path.Combine(options.GlobalRoot, "plugins"), Scope = PluginScope.Global },
        };
        // A host started in the folder that holds the global root (the home folder) has one plugin folder: its
        // packages are the global ones, and are not found a second time as those of the project.
        if (options.ProjectContext is not null && !SameDirectory(Path.Combine(options.ProjectContext.ProjectPath, ".alta", "plugins"), roots[0].RootPath))
        {
            roots.Add(new PluginRoot
            {
                RootPath = Path.Combine(options.ProjectContext.ProjectPath, ".alta", "plugins"),
                Scope = PluginScope.Project,
                ProjectId = options.ProjectContext.ProjectId,
                ProjectPath = options.ProjectContext.ProjectPath,
            });
        }

        return roots;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return PathComparer.Equals(PluginRuntimePathService.NormalizeDirectory(left), PluginRuntimePathService.NormalizeDirectory(right));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false; // A path that names no folder is the folder of nothing.
        }
    }

    private static PluginHostInfo CreateHostInfo(PluginRuntimeManagerOptions options)
        => new()
        {
            ApplicationName = "CodeAlta",
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0",
            HostApiVersion = PluginAuthoringPolicy.HostApiVersion,
            UserDataDirectory = options.GlobalRoot,
            IsHeadless = options.IsHeadless,
            HasInteractiveUi = !options.IsHeadless,
            Frontend = options.Frontend,
        };

    /// <summary>The metadata key of the diagnostic for a plugin that does not support the application of the host.</summary>
    public const string UnsupportedFrontendMetadataKey = "UnsupportedFrontend";

    // A plugin that does not support this application is not an error: it is simply not started here.
    internal static PluginRuntimeDiagnostic? CreateUnsupportedFrontendDiagnostic(PluginDescriptor descriptor, PluginFrontends host, string? packageId, string? path)
    {
        if (descriptor.Frontends.Supports(host)) return null;
        return PluginRuntimeDiagnostic.Info(
            PluginRuntimeDiagnosticSource.Activation,
            $"Plugin '{descriptor.DisplayName ?? descriptor.RuntimeKey}' was not started: it does not support the {host.ToDisplayName()}.",
            packageId,
            path) with
        {
            RuntimeKey = descriptor.RuntimeKey,
            Metadata = new Dictionary<string, string> { [UnsupportedFrontendMetadataKey] = host.ToString() },
        };
    }

    private static string ResolveCodeAltaBuildIdentity()
        => typeof(PluginRuntimeManager).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string ResolveSdkIdentity()
        => Environment.Version.ToString();

    private static string ResolveGlobalJsonContent()
    {
        foreach (var path in EnumerateAncestorFiles(AppContext.BaseDirectory, "global.json"))
        {
            return File.ReadAllText(path);
        }

        return """
{
    "sdk": {
        "version": "10.0.100",
        "rollForward": "latestMinor",
        "allowPrerelease": false
    }
}
""";
    }

    private static IReadOnlyList<PluginPackageVersion> ResolvePackageVersions()
    {
        foreach (var path in EnumerateAncestorFiles(AppContext.BaseDirectory, "Directory.Packages.props"))
        {
            return PluginPackageVersionProvider.ExtractPluginPackageVersionsFromFile(path);
        }

        return ResolveInstalledPackageVersions(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Reads the versions of the shared authoring packages from the assemblies installed beside the host, so
    /// that a source plugin of an installed application compiles against the versions it will run with.
    /// </summary>
    /// <param name="hostFolder">The folder that contains the host assemblies.</param>
    /// <returns>One version per shared package whose assembly is present and carries a product version.</returns>
    internal static IReadOnlyList<PluginPackageVersion> ResolveInstalledPackageVersions(string hostFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFolder);
        var versions = new List<PluginPackageVersion>();
        foreach (var package in PluginRootBuildFileGenerator.DefaultSharedPackageNames)
        {
            var path = Path.Combine(hostFolder, package + ".dll");
            if (!File.Exists(path)) continue;
            // "3.10.0+0123abc": the package version, then the build metadata.
            var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
            var version = product?.Split('+', 2)[0].Trim();
            if (!string.IsNullOrEmpty(version) && char.IsAsciiDigit(version[0])) versions.Add(new PluginPackageVersion { Include = package, Version = version });
        }

        return versions;
    }

    private static IEnumerable<string> EnumerateAncestorFiles(string startDirectory, string fileName)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }

            path = Path.Combine(directory.FullName, "src", fileName);
            if (File.Exists(path))
            {
                yield return path;
            }

            directory = directory.Parent;
        }
    }
}
