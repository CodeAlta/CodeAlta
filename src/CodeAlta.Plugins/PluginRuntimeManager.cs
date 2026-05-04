using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Options used to start the CodeAlta plugin runtime.
/// </summary>
public sealed record PluginRuntimeManagerOptions
{
    /// <summary>Gets the global CodeAlta home directory.</summary>
    public required string GlobalRoot { get; init; }

    /// <summary>Gets the current project descriptor, when project plugins are in scope.</summary>
    public PluginProjectContext? ProjectContext { get; init; }

    /// <summary>Gets a value indicating whether dynamic plugins are disabled for this process.</summary>
    public bool SafeMode { get; init; }

    /// <summary>Gets a value indicating whether the host is running without an interactive UI.</summary>
    public bool IsHeadless { get; init; }

    /// <summary>Gets the raw process arguments visible to startup contributions.</summary>
    public IReadOnlyList<string> RawArguments { get; init; } = [];

    /// <summary>Gets additional built-in plugins to activate before dynamic plugins.</summary>
    public IReadOnlyList<BuiltInPluginDefinition> BuiltIns { get; init; } = [];

    /// <summary>Gets the maximum number of source plugin builds that can run in parallel.</summary>
    public int MaxParallelBuilds { get; init; } = Math.Min(Environment.ProcessorCount, 4);
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
public sealed class PluginRuntimeManager : IAsyncDisposable
{
    private readonly PluginContributionRegistry _registry = new();
    private readonly PluginRuntimeDiagnosticStore _diagnostics = new();
    private readonly List<ActivePluginInstance> _activePlugins = [];
    private readonly object _lock = new();
    private int _activationGeneration;
    private bool _disposed;

    /// <summary>Gets the contribution registry owned by the runtime.</summary>
    public PluginContributionRegistry Registry => _registry;

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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public async ValueTask<PluginRuntimeManagerStartResult> StartAsync(
        PluginRuntimeManagerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var activePlugins = new List<ActivePluginInstance>();
        var buildResults = new List<PluginBuildResult>();
        var catalogOptions = new CatalogOptions { GlobalRoot = options.GlobalRoot };
        var configStore = new CodeAltaConfigStore(catalogOptions);
        var globalConfig = configStore.LoadGlobal();
        var projectConfig = configStore.LoadProject(options.ProjectContext?.ProjectPath);
        var hostInfo = CreateHostInfo(options);
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
                Type = builtIn.Factory.Method.ReturnType == typeof(PluginBase) ? builtIn.Factory().GetType() : builtIn.Factory.Method.ReturnType,
                Descriptor = builtIn.CreateDescriptor(),
            };
            var activation = await activator.ActivateAsync(
                    discovered,
                    sourcePackage: null,
                    loadContext: null,
                    new PluginActivationOptions { HostInfo = hostInfo, ActivationGeneration = ++_activationGeneration },
                    cancellationToken)
                .ConfigureAwait(false);
            diagnostics.AddRange(activation.Diagnostics);
            if (activation.ActivePlugin is not null)
            {
                activePlugins.Add(activation.ActivePlugin);
            }
        }

        var roots = BuildRoots(options).Where(static root => Directory.Exists(root.RootPath)).ToArray();
        var packages = roots.SelectMany(static root => new SourcePluginDiscoveryService().Discover(root)).ToArray();
        var plan = new PluginStartupPlanner().PlanSourceBuilds(packages, globalConfig, projectConfig, options.SafeMode);
        diagnostics.AddRange(plan.Diagnostics);

        if (plan.BuildRequests.Count > 0)
        {
            var generationOptions = new PluginRootBuildFileOptions
            {
                CodeAltaExeFolder = AppContext.BaseDirectory,
                GlobalJsonContent = ResolveGlobalJsonContent(),
                PackageVersions = ResolvePackageVersions(),
            };
            foreach (var root in plan.BuildRequests.Select(static request => request.Package.Root).DistinctBy(static root => root.RootPath, StringComparer.OrdinalIgnoreCase))
            {
                var generation = await new PluginRootBuildFileGenerator().GenerateAsync(root, generationOptions, cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(generation.Diagnostics);
            }

            var cacheRoot = Path.Combine(options.GlobalRoot, "cache");
            var manifestStore = new PluginBuildManifestStore(cacheRoot, ResolveCodeAltaBuildIdentity(), ResolveSdkIdentity());
            var scheduler = new PluginBuildScheduler(new PluginBuildService(manifestStore), new PluginBuildSchedulerOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelBuilds) });
            buildResults.AddRange(options.IsHeadless
                ? await scheduler.BuildAsync(plan.BuildRequests, cancellationToken).ConfigureAwait(false)
                : await PluginStartupFeedbackReporter.BuildWithInteractiveLiveAsync(scheduler, plan.BuildRequests, cancellationToken).ConfigureAwait(false));

            var loader = new PluginAssemblyLoader();
            var typeDiscovery = new PluginTypeDiscoveryService();
            foreach (var buildResult in buildResults)
            {
                diagnostics.AddRange(buildResult.RuntimeDiagnostics);
                if (!buildResult.Succeeded)
                {
                    continue;
                }

                var load = loader.Load(buildResult);
                diagnostics.AddRange(load.Diagnostics);
                if (!load.Succeeded)
                {
                    continue;
                }

                var discovery = typeDiscovery.DiscoverWithDiagnostics(load.Assembly!, buildResult.Package.PackageDirectory, buildResult.Package.Sidecars.ReadmePath);
                diagnostics.AddRange(discovery.Diagnostics);
                foreach (var discovered in discovery.Plugins)
                {
                    var activation = await activator.ActivateAsync(
                            discovered,
                            buildResult.Package,
                            load.LoadContext,
                            new PluginActivationOptions { HostInfo = hostInfo, ActivationGeneration = ++_activationGeneration },
                            cancellationToken)
                        .ConfigureAwait(false);
                    diagnostics.AddRange(activation.Diagnostics);
                    if (activation.ActivePlugin is not null)
                    {
                        activePlugins.Add(activation.ActivePlugin);
                    }
                }
            }
        }

        lock (_lock)
        {
            _activePlugins.AddRange(activePlugins);
        }

        var startup = await Adapter.RunStartupAsync(activePlugins, options.RawArguments, CreateAdapterOptions(options), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(startup.Diagnostics);
        _diagnostics.AddRange(diagnostics);
        return new PluginRuntimeManagerStartResult
        {
            ActivePlugins = activePlugins,
            BuildResults = buildResults,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Deactivates all active plugins and releases runtime-owned handles.</summary>
    public async ValueTask DeactivateAllAsync(CancellationToken cancellationToken = default)
    {
        ActivePluginInstance[] active;
        lock (_lock)
        {
            active = _activePlugins.ToArray();
            _activePlugins.Clear();
        }

        foreach (var plugin in active.Reverse())
        {
            var diagnostics = await plugin.DeactivateAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _diagnostics.AddRange(diagnostics);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DeactivateAllAsync().ConfigureAwait(false);
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
        if (options.ProjectContext is not null)
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

    private static PluginHostInfo CreateHostInfo(PluginRuntimeManagerOptions options)
        => new()
        {
            ApplicationName = "CodeAlta",
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0",
            HostApiVersion = "1.0.0",
            UserDataDirectory = options.GlobalRoot,
            IsHeadless = options.IsHeadless,
        };

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

        return [];
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

            directory = directory.Parent;
        }
    }
}
