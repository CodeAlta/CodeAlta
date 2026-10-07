using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>What the running host does with a source plugin package.</summary>
public enum PluginPackageState
{
    /// <summary>A plugin of the package is active.</summary>
    Running,
    /// <summary>The package is turned off in the configuration, or plugins are turned off for this run.</summary>
    Disabled,
    /// <summary>The package was not built, loaded or started, and none of its plugins is active.</summary>
    Failed,
    /// <summary>The package has no plugin for this application.</summary>
    Unsupported,
    /// <summary>The host has not started the package: it was created or turned on after the start.</summary>
    Stopped,
}

/// <summary>A source plugin package of the plugin folders of the host, with what the host did with it.</summary>
public sealed record PluginPackageStatus
{
    /// <summary>Gets the package as it is on disk.</summary>
    public required SourcePluginPackage Package { get; init; }

    /// <summary>Gets what the host does with the package.</summary>
    public required PluginPackageState State { get; init; }

    /// <summary>Gets whether the configuration lets the package start.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the active plugins of the package.</summary>
    public IReadOnlyList<PluginDescriptor> Plugins { get; init; } = [];

    /// <summary>Gets the last build of the package in this run, with its errors and warnings; null when it was not built.</summary>
    public PluginBuildResult? Build { get; init; }

    /// <summary>
    /// Gets whether the source on disk is another one than the source of the active plugins, or than the source of
    /// the last build when no plugin of the package is active.
    /// </summary>
    public bool SourceChanged { get; init; }

    /// <summary>Gets the diagnostics of the package: discovery, build, load, start and callbacks.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>How a change of a package of the running host ended.</summary>
public enum PluginPackageChange
{
    /// <summary>Nothing was done: the package already runs the source that is on disk.</summary>
    Unchanged,
    /// <summary>The package was not running and its plugins were started.</summary>
    Started,
    /// <summary>The plugins of the package were replaced by those of a new build.</summary>
    Reloaded,
    /// <summary>The plugins of the package were stopped: the package is turned off or was removed.</summary>
    Stopped,
    /// <summary>The build failed. Plugins of the package that were running still run their previous version.</summary>
    BuildFailed,
    /// <summary>The package was built and none of its plugins started.</summary>
    StartFailed,
    /// <summary>The package is turned off: nothing was built.</summary>
    Disabled,
}

/// <summary>The result of a change of a package of the running host.</summary>
public sealed record PluginPackageChangeResult
{
    /// <summary>Gets how the change ended.</summary>
    public required PluginPackageChange Change { get; init; }

    /// <summary>Gets the package after the change.</summary>
    public required PluginPackageStatus Status { get; init; }
}

/// <summary>Names the packages whose plugins changed in the running host.</summary>
/// <param name="packageIds">The ids of the packages.</param>
public sealed class PluginRuntimeChangedEventArgs(IReadOnlyList<string> packageIds) : EventArgs
{
    /// <summary>Gets the ids of the packages whose plugins were started, replaced or stopped.</summary>
    public IReadOnlyList<string> PackageIds { get; } = packageIds ?? throw new ArgumentNullException(nameof(packageIds));
}

public sealed partial class PluginRuntimeManager
{
    private static readonly TimeSpan PreviousVersionWait = TimeSpan.FromSeconds(10);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private readonly Dictionary<string, PluginBuildResult> _builds = new(PathComparer);
    private readonly Dictionary<string, string?> _builtStamps = new(PathComparer);
    private readonly Dictionary<string, string?> _loadedStamps = new(PathComparer);
    private PluginRuntimeManagerOptions? _options;
    private PluginHostInfo? _hostInfo;

    /// <summary>
    /// Raised after the plugins of a package were started, replaced or stopped in the running host, so that a
    /// host reads again what it keeps of the contributions: commands, shortcuts, pickers.
    /// </summary>
    public event EventHandler<PluginRuntimeChangedEventArgs>? Changed;

    /// <summary>Gets the options the runtime was started with; null before the start.</summary>
    public PluginRuntimeManagerOptions? StartOptions
    {
        get { lock (_lock) return _options; }
    }

    /// <summary>Gets the plugin folders of the host: the global one, then the one of its project.</summary>
    public IReadOnlyList<PluginRoot> Roots => StartOptions is { } options ? BuildRoots(options) : [];

    /// <summary>
    /// Lists the source plugin packages of the plugin folders as they are on disk now, each with what the host
    /// did with it.
    /// </summary>
    /// <returns>The packages, global ones first; empty before the start.</returns>
    public IReadOnlyList<PluginPackageStatus> GetPackages()
    {
        if (StartOptions is not { } options) return [];
        var config = LoadConfig(options);
        var packages = new SourcePluginDiscoveryService().Discover(BuildRoots(options));
        return [.. packages.Select(package => Describe(options, config, package, packages))];
    }

    /// <summary>
    /// Builds a package without loading it: the plugins that run keep running, and the result says whether the
    /// source on disk compiles.
    /// </summary>
    /// <param name="package">The package, as <see cref="GetPackages"/> lists it.</param>
    /// <param name="force">Whether to build when the source did not change since the last build.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    /// <returns>The package after the build.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The runtime has not started, is closing, or the caller is a plugin that is being started.</exception>
    public async ValueTask<PluginPackageStatus> BuildPackageAsync(SourcePluginPackage package, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        PluginPackageStatus? status = null;
        await ChangeAsync(async options =>
        {
            var config = LoadConfig(options);
            var packages = new SourcePluginDiscoveryService().Discover(BuildRoots(options));
            var build = await BuildCoreAsync(options, package, force, cancellationToken).ConfigureAwait(false);
            ReplaceDiagnostics(package, packages, ActiveOf(package).Select(static plugin => plugin.Descriptor.RuntimeKey), buildOnly: true, BuildDiagnostics(package, build));
            status = Describe(options, config, package, packages);
        }, cancellationToken).ConfigureAwait(false);
        return status!;
    }

    /// <summary>
    /// Builds a package and replaces its running plugins by those of the new build, or starts them when the
    /// package was not running. When the build fails, the plugins that run keep running.
    /// </summary>
    /// <param name="package">The package, as <see cref="GetPackages"/> lists it.</param>
    /// <param name="force">Whether to build when the source did not change since the last build.</param>
    /// <param name="cancellationToken">Stops the build. Once the build ended, the change runs to its end.</param>
    /// <returns>How the change ended, and the package after it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The runtime has not started, is closing, or the caller is a plugin that is being started or a callback of a plugin.</exception>
    public async ValueTask<PluginPackageChangeResult> ReloadPackageAsync(SourcePluginPackage package, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        PluginPackageChangeResult? result = null;
        await ChangeAsync(async options =>
        {
            var packages = new SourcePluginDiscoveryService().Discover(BuildRoots(options));
            result = await ChangePackageAsync(options, LoadConfig(options), package, packages, force, onlyWhenChanged: false, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Notify([result!]);
        return result!;
    }

    /// <summary>
    /// Applies what changed on disk and in the configuration since the plugins were started: starts the packages
    /// that are new or turned on, replaces the plugins of the packages whose source changed, and stops those of
    /// the packages that were removed or turned off. A package whose last build failed is built again only when
    /// its source changed.
    /// </summary>
    /// <param name="cancellationToken">Stops the builds.</param>
    /// <returns>One result per package, those that did not change included.</returns>
    /// <exception cref="InvalidOperationException">The runtime has not started, is closing, or the caller is a plugin that is being started or a callback of a plugin.</exception>
    public async ValueTask<IReadOnlyList<PluginPackageChangeResult>> RefreshPackagesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PluginPackageChangeResult>();
        await ChangeAsync(async options =>
        {
            var config = LoadConfig(options);
            var packages = new SourcePluginDiscoveryService().Discover(BuildRoots(options));
            foreach (var removed in RemovedPackages(packages))
            {
                var keys = await RetireAsync(removed, []).ConfigureAwait(false);
                ReplaceDiagnostics(removed, packages, keys, buildOnly: false, []);
                Forget(removed);
                results.Add(new() { Change = PluginPackageChange.Stopped, Status = new() { Package = removed, State = PluginPackageState.Stopped } });
            }

            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await ChangePackageAsync(options, config, package, packages, force: false, onlyWhenChanged: true, cancellationToken).ConfigureAwait(false));
            }
        }, cancellationToken).ConfigureAwait(false);
        Notify(results);
        return results;
    }

    // One change at a time, owned like the start: see RunOwnedChangeAsync.
    private async Task ChangeAsync(Func<PluginRuntimeManagerOptions, Task> body, CancellationToken cancellationToken)
    {
        // A plugin that is being started, or a callback of a plugin, would wait for itself here.
        ThrowIfAgentEventSelfJoin();
        if (StartOptions is not { } options) throw new InvalidOperationException("The plugin runtime has not started.");
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunOwnedChangeAsync(() => body(options)).ConfigureAwait(false);
        }
        finally
        {
            _changeGate.Release();
        }
    }

    private async Task<PluginPackageChangeResult> ChangePackageAsync(
        PluginRuntimeManagerOptions options,
        PluginRuntimeConfigLoadResult config,
        SourcePluginPackage package,
        IReadOnlyList<SourcePluginPackage> packages,
        bool force,
        bool onlyWhenChanged,
        CancellationToken cancellationToken)
    {
        PluginPackageChangeResult Result(PluginPackageChange change) => new() { Change = change, Status = Describe(options, config, package, packages) };
        var running = ActiveOf(package);
        if (!IsEnabled(options, config, package))
        {
            if (running.Length == 0) return Result(PluginPackageChange.Disabled);
            var stopped = await RetireAsync(package, []).ConfigureAwait(false);
            ReplaceDiagnostics(package, packages, stopped, buildOnly: false, []);
            Forget(package);
            return Result(PluginPackageChange.Stopped);
        }

        var stamp = PluginBuildManifestStore.ComputeSourceStamp(package);
        if (onlyWhenChanged)
        {
            string? known;
            bool tried;
            lock (_lock) tried = (running.Length > 0 ? _loadedStamps : _builtStamps).TryGetValue(package.PackageDirectory, out known);
            // Running the source that is on disk, or already seen not to build or not to start from it.
            if (tried && stamp is not null && string.Equals(known, stamp, StringComparison.Ordinal)) return Result(PluginPackageChange.Unchanged);
        }

        var diagnostics = new List<PluginRuntimeDiagnostic>(package.Diagnostics);
        if (package.Diagnostics.Any(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error))
        {
            // An id that cannot be a package id, or a build file that would replace the generated ones.
            ReplaceDiagnostics(package, packages, [], buildOnly: running.Length > 0, diagnostics);
            return Result(running.Length > 0 ? PluginPackageChange.BuildFailed : PluginPackageChange.StartFailed);
        }

        var build = await BuildCoreAsync(options, package, force, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(BuildDiagnostics(package, build));
        if (!build.Succeeded)
        {
            ReplaceDiagnostics(package, packages, [], buildOnly: running.Length > 0, diagnostics);
            return Result(PluginPackageChange.BuildFailed);
        }

        // From here the change runs to its end, whatever happens to the caller.
        var previousKeys = await RetireAsync(package, diagnostics).ConfigureAwait(false);
        var started = await LoadAndActivateAsync(options, _hostInfo!, build, stamp, diagnostics, CancellationToken.None).ConfigureAwait(false);
        if (started.Count > 0)
        {
            var startup = await Adapter.RunStartupAsync(started, options.RawArguments, CreateAdapterOptions(options), CancellationToken.None).ConfigureAwait(false);
            diagnostics.AddRange(startup.Diagnostics);
        }

        ReplaceDiagnostics(package, packages, previousKeys, buildOnly: false, diagnostics);
        return Result(started.Count == 0 ? PluginPackageChange.StartFailed : running.Length > 0 ? PluginPackageChange.Reloaded : PluginPackageChange.Started);
    }

    // The files CodeAlta writes in the plugin folder, then the build of the package.
    private async Task<PluginBuildResult> BuildCoreAsync(PluginRuntimeManagerOptions options, SourcePluginPackage package, bool force, CancellationToken cancellationToken)
    {
        PluginBuildResult build;
        try
        {
            var generation = await new PluginRootBuildFileGenerator().GenerateAsync(package.Root, CreateBuildFileOptions(options), cancellationToken).ConfigureAwait(false);
            if (!generation.Succeeded)
            {
                build = new PluginBuildResult { Package = package, RuntimeDiagnostics = generation.Diagnostics };
            }
            else
            {
                var manifests = new PluginBuildManifestStore(Path.Combine(options.GlobalRoot, "cache"), ResolveCodeAltaBuildIdentity(), ResolveSdkIdentity());
                build = await new PluginBuildService(manifests).BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = force }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            build = new PluginBuildResult
            {
                Package = package,
                RuntimeDiagnostics = [PluginRuntimeDiagnostic.Error(PluginRuntimeDiagnosticSource.Build, $"Plugin build failed: {exception.Message}", package.PackageId, package.EntryFilePath, exception)],
            };
        }

        RememberBuild(build);
        return build;
    }

    private static IEnumerable<PluginRuntimeDiagnostic> BuildDiagnostics(SourcePluginPackage package, PluginBuildResult build)
        => build.RuntimeDiagnostics.Select(diagnostic => diagnostic.PackageId is null ? diagnostic with { PackageId = package.PackageId } : diagnostic);

    private void RememberBuild(PluginBuildResult build)
    {
        var stamp = PluginBuildManifestStore.ComputeSourceStamp(build.Package);
        lock (_lock)
        {
            _builds[build.Package.PackageDirectory] = build;
            _builtStamps[build.Package.PackageDirectory] = stamp;
        }
    }

    private void Forget(SourcePluginPackage package)
    {
        lock (_lock)
        {
            _loadedStamps.Remove(package.PackageDirectory);
            _builtStamps.Remove(package.PackageDirectory);
        }
    }

    // Loads a built package and starts the plugins it declares for this application.
    private async ValueTask<IReadOnlyList<ActivePluginInstance>> LoadAndActivateAsync(
        PluginRuntimeManagerOptions options,
        PluginHostInfo hostInfo,
        PluginBuildResult build,
        string? sourceStamp,
        List<PluginRuntimeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var package = build.Package;
        var started = new List<ActivePluginInstance>();
        var load = new PluginAssemblyLoader(options.AuthoringProfile).Load(build);
        diagnostics.AddRange(load.Diagnostics);
        if (!load.Succeeded) return started;

        var discovery = new PluginTypeDiscoveryService().DiscoverWithDiagnostics(load.Assembly!, package.PackageDirectory, package.Sidecars.ReadmePath);
        diagnostics.AddRange(discovery.Diagnostics);
        if (discovery.Plugins.Count == 0)
        {
            diagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Activation,
                "The package declares no plugin: it needs a public class that inherits PluginBase and has a public constructor without parameters.",
                package.PackageId,
                package.EntryFilePath));
        }

        var activator = new PluginRuntimeActivator(_registry);
        foreach (var discovered in discovery.Plugins)
        {
            if (CreateUnsupportedFrontendDiagnostic(discovered.Descriptor, options.Frontend, package.PackageId, package.PackageDirectory) is { } unsupported)
            {
                diagnostics.Add(unsupported);
                continue;
            }

            // Contributions are owned by the key of their plugin: two plugins cannot have the same one.
            if (KeyOwner(discovered.Descriptor.RuntimeKey) is { } owner)
            {
                var other = owner.SourcePackage is { } ownerPackage ? $"the plugin package '{ownerPackage.PackageId}'" : "a built-in plugin";
                diagnostics.Add(PluginRuntimeDiagnostic.Error(
                    PluginRuntimeDiagnosticSource.Activation,
                    $"Plugin '{discovered.Descriptor.DisplayName ?? discovered.Descriptor.TypeName}' was not started: its key '{discovered.Descriptor.RuntimeKey}' is the key of {other}. Give it another key in [Plugin(\"...\")].",
                    package.PackageId,
                    package.EntryFilePath));
                continue;
            }

            var activation = await activator.ActivateAsync(
                    discovered,
                    package,
                    load.LoadContext,
                    new PluginActivationOptions { HostInfo = hostInfo, Services = options.Services, ActivationGeneration = Interlocked.Increment(ref _activationGeneration) },
                    cancellationToken)
                .ConfigureAwait(false);
            if (activation.ActivePlugin is not null)
            {
                OwnActivation(activation.ActivePlugin);
                started.Add(activation.ActivePlugin);
            }

            diagnostics.AddRange(activation.Diagnostics);
        }

        if (started.Count > 0)
        {
            lock (_lock) _loadedStamps[package.PackageDirectory] = sourceStamp;
        }
        else
        {
            // Nothing of it runs: the next refresh leaves it alone until its source changes.
            lock (_lock) _loadedStamps.Remove(package.PackageDirectory);
        }

        return started;
    }

    private ActivePluginInstance? KeyOwner(string runtimeKey)
    {
        lock (_lock) return _activePlugins.FirstOrDefault(plugin => string.Equals(plugin.Descriptor.RuntimeKey, runtimeKey, StringComparison.Ordinal));
    }

    private ActivePluginInstance[] ActiveOf(SourcePluginPackage package)
    {
        lock (_lock) return [.. _activePlugins.Where(plugin => SamePackage(plugin.SourcePackage, package))];
    }

    private static bool SamePackage(SourcePluginPackage? left, SourcePluginPackage right)
        => left is not null && PathComparer.Equals(left.PackageDirectory, right.PackageDirectory);

    // The packages of active plugins that are no longer on disk.
    private SourcePluginPackage[] RemovedPackages(IReadOnlyList<SourcePluginPackage> packages)
    {
        lock (_lock)
        {
            return [.. _activePlugins.Select(static plugin => plugin.SourcePackage).OfType<SourcePluginPackage>()
                .Where(active => !packages.Any(package => SamePackage(active, package)))
                .DistinctBy(static package => package.PackageDirectory, PathComparer)];
        }
    }

    // Stops the active plugins of a package and returns their keys. One that does not stop in time, or fails
    // to, is left to end by itself: the close of the runtime still joins it.
    private async Task<IReadOnlyList<string>> RetireAsync(SourcePluginPackage package, List<PluginRuntimeDiagnostic> diagnostics)
    {
        ActivePluginInstance[] previous;
        lock (_lock)
        {
            previous = [.. _activePlugins.Where(plugin => SamePackage(plugin.SourcePackage, package))];
            foreach (var plugin in previous)
            {
                _activePlugins.Remove(plugin);
                _retired.Add(plugin);
            }
        }

        var keys = new List<string>();
        foreach (var plugin in previous.Reverse())
        {
            keys.Add(plugin.Descriptor.RuntimeKey);
            // The session that asked for the change may hold a tool of this version: no wait for its collection.
            plugin.VerifiesUnload = false;
            try
            {
                await plugin.DeactivateAsync(PreviousVersionWait, CancellationToken.None).ConfigureAwait(false);
                if (plugin.State == PluginRuntimeState.Deactivating)
                {
                    diagnostics.Add(PluginRuntimeDiagnostic.Warning(
                        PluginRuntimeDiagnosticSource.Unload,
                        $"The previous version of '{plugin.Descriptor.DisplayName ?? plugin.Descriptor.TypeName}' did not stop within {PreviousVersionWait.TotalSeconds:0} seconds: it is left to end by itself.",
                        package.PackageId,
                        package.PackageDirectory));
                    continue;
                }

                lock (_lock) _retired.Remove(plugin);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(PluginRuntimeDiagnostic.Warning(
                    PluginRuntimeDiagnosticSource.Unload,
                    $"The previous version of '{plugin.Descriptor.DisplayName ?? plugin.Descriptor.TypeName}' failed to stop: {exception.Message}",
                    package.PackageId,
                    package.PackageDirectory) with
                {
                    Exception = PluginExceptionInfo.FromException(exception),
                });
            }
        }

        return keys;
    }

    private static bool IsEnabled(PluginRuntimeManagerOptions options, PluginRuntimeConfigLoadResult config, SourcePluginPackage package)
        => config.Succeeded && new PluginRuntimeConfigResolver().ResolveSourcePlugin(package, config.GlobalConfig!, config.ProjectConfig, options.SafeMode).Enabled;

    private PluginPackageStatus Describe(
        PluginRuntimeManagerOptions options,
        PluginRuntimeConfigLoadResult config,
        SourcePluginPackage package,
        IReadOnlyList<SourcePluginPackage> packages)
    {
        var enabled = IsEnabled(options, config, package);
        PluginDescriptor[] plugins;
        PluginBuildResult? build;
        string? known;
        bool tried;
        lock (_lock)
        {
            plugins = [.. _activePlugins.Where(plugin => SamePackage(plugin.SourcePackage, package)).Select(static plugin => plugin.Descriptor)];
            _builds.TryGetValue(package.PackageDirectory, out build);
            tried = (plugins.Length > 0 ? _loadedStamps : _builtStamps).TryGetValue(package.PackageDirectory, out known);
        }

        var stored = _diagnostics.GetSnapshot().Where(diagnostic => Owns(package, packages, plugins.Select(static plugin => plugin.RuntimeKey), diagnostic));
        // What discovery says is read again each time; a change keeps it with the rest.
        PluginRuntimeDiagnostic[] diagnostics = [.. config.Diagnostics, .. package.Diagnostics, .. stored.Where(diagnostic => !package.Diagnostics.Any(found => found.Message == diagnostic.Message))];
        var state = plugins.Length > 0 ? PluginPackageState.Running
            : !enabled ? PluginPackageState.Disabled
            : diagnostics.Any(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error) ? PluginPackageState.Failed
            : diagnostics.Any(static diagnostic => diagnostic.Metadata.ContainsKey(UnsupportedFrontendMetadataKey)) ? PluginPackageState.Unsupported
            : PluginPackageState.Stopped;
        var stamp = PluginBuildManifestStore.ComputeSourceStamp(package);
        return new PluginPackageStatus
        {
            Package = package,
            State = state,
            Enabled = enabled,
            Plugins = plugins,
            Build = build,
            SourceChanged = tried && stamp is not null && !string.Equals(known, stamp, StringComparison.Ordinal),
            Diagnostics = diagnostics,
        };
    }

    // A diagnostic names its package by id, and a global and a project package can have the same id: the path of
    // the diagnostic then says which one it is about.
    private static bool Owns(SourcePluginPackage package, IReadOnlyList<SourcePluginPackage> packages, IEnumerable<string> runtimeKeys, PluginRuntimeDiagnostic diagnostic)
    {
        if (diagnostic.PackageId is null) return diagnostic.RuntimeKey is { } key && runtimeKeys.Contains(key, StringComparer.Ordinal);
        if (!string.Equals(diagnostic.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)) return false;
        var others = packages.Where(other => !SamePackage(other, package) && string.Equals(other.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return others.Length == 0 || diagnostic.Path is not { } path || !others.Any(other => IsInside(path, other.PackageDirectory));
    }

    private static bool IsInside(string path, string directory)
    {
        try
        {
            var relative = Path.GetRelativePath(directory, path);
            return relative == "." || (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // What the host says about a package is what its last change said: the diagnostics of the previous one
    // go. While the previous version keeps running after a failed build, only what the build said is replaced.
    private void ReplaceDiagnostics(
        SourcePluginPackage package,
        IReadOnlyList<SourcePluginPackage> packages,
        IEnumerable<string> previousKeys,
        bool buildOnly,
        IEnumerable<PluginRuntimeDiagnostic> diagnostics)
    {
        string[] keys = [.. previousKeys];
        _diagnostics.RemoveWhere(diagnostic =>
            Owns(package, packages, keys, diagnostic) &&
            (!buildOnly || diagnostic.Source is PluginRuntimeDiagnosticSource.Build or PluginRuntimeDiagnosticSource.RootGeneration or PluginRuntimeDiagnosticSource.Discovery));
        _diagnostics.AddRange(diagnostics);
    }

    private void Notify(IReadOnlyList<PluginPackageChangeResult> results)
    {
        string[] changed = [.. results
            .Where(static result => result.Change is PluginPackageChange.Started or PluginPackageChange.Reloaded or PluginPackageChange.Stopped or PluginPackageChange.StartFailed)
            .Select(static result => result.Status.Package.PackageId)];
        if (changed.Length == 0) return;
        try
        {
            Changed?.Invoke(this, new PluginRuntimeChangedEventArgs(changed));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Add(PluginRuntimeDiagnostic.Warning(PluginRuntimeDiagnosticSource.Callback, $"A listener of the plugin changes failed: {exception.Message}") with
            {
                Exception = PluginExceptionInfo.FromException(exception),
            });
        }
    }

    private static PluginRuntimeConfigLoadResult LoadConfig(PluginRuntimeManagerOptions options)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = options.GlobalRoot };
        var projectRoot = options.ProjectContext?.ProjectPath;
        return PluginRuntimeConfigResolver.LoadValidatedConfig(
            new CodeAltaConfigStore(catalogOptions),
            catalogOptions.ConfigPath,
            string.IsNullOrWhiteSpace(projectRoot) ? null : Path.Combine(projectRoot, ".alta", "config.toml"),
            projectRoot);
    }

    private static PluginRootBuildFileOptions CreateBuildFileOptions(PluginRuntimeManagerOptions options)
        => new()
        {
            AuthoringProfile = options.AuthoringProfile,
            CodeAltaExeFolder = AppContext.BaseDirectory,
            GlobalJsonContent = ResolveGlobalJsonContent(),
            PackageVersions = ResolvePackageVersions(),
        };
}
