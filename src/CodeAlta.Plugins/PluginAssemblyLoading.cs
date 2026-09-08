using System.Reflection;
using System.Runtime.Loader;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Collectible load context for one dynamic plugin load unit.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlySet<string> _hostSharedAssemblyNames;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAssemblyLoadContext"/> class.
    /// </summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="hostSharedAssemblyNames">Assembly simple names that must resolve from the default load context.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mainAssemblyPath"/> is empty.</exception>
    public PluginAssemblyLoadContext(string mainAssemblyPath, IEnumerable<string>? hostSharedAssemblyNames = null)
        : base($"CodeAlta.Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainAssemblyPath);
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _hostSharedAssemblyNames = new HashSet<string>(
            hostSharedAssemblyNames ?? PluginAssemblyLoader.DefaultHostSharedAssemblyNames,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (!string.IsNullOrWhiteSpace(assemblyName.Name) && _hostSharedAssemblyNames.Contains(assemblyName.Name))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }

        var assemblyPath = ResolveManagedAssemblyPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = ResolveUnmanagedDllPath(unmanagedDllName);
        return libraryPath is null ? 0 : LoadUnmanagedDllFromPath(libraryPath);
    }

    /// <summary>
    /// Resolves a managed assembly path using this plugin load unit's dependency resolver.
    /// </summary>
    /// <param name="assemblyName">The assembly name to resolve.</param>
    /// <returns>The resolved assembly path, or <see langword="null" /> when the dependency is host-shared or unresolved.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assemblyName" /> is <see langword="null" />.</exception>
    public string? ResolveManagedAssemblyPath(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        if (!string.IsNullOrWhiteSpace(assemblyName.Name) && _hostSharedAssemblyNames.Contains(assemblyName.Name))
        {
            return null;
        }

        return _resolver.ResolveAssemblyToPath(assemblyName);
    }

    /// <summary>
    /// Resolves an unmanaged library path using this plugin load unit's dependency resolver.
    /// </summary>
    /// <param name="unmanagedDllName">The unmanaged library name.</param>
    /// <returns>The resolved library path, or <see langword="null" /> when unresolved.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="unmanagedDllName" /> is empty.</exception>
    public string? ResolveUnmanagedDllPath(string unmanagedDllName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unmanagedDllName);
        return _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
    }
}

/// <summary>
/// Describes a loaded dynamic source plugin assembly.
/// </summary>
public sealed record PluginAssemblyLoadResult
{
    /// <summary>Gets the source plugin package.</summary>
    public required SourcePluginPackage Package { get; init; }

    /// <summary>Gets the output assembly path.</summary>
    public required string OutputAssemblyPath { get; init; }

    /// <summary>Gets the collectible load context.</summary>
    public PluginAssemblyLoadContext? LoadContext { get; init; }

    /// <summary>Gets the loaded assembly.</summary>
    public Assembly? Assembly { get; init; }

    /// <summary>Gets diagnostics raised while loading.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets a value indicating whether loading succeeded.</summary>
    public bool Succeeded => Assembly is not null && Diagnostics.All(static diagnostic => diagnostic.Severity < PluginDiagnosticSeverity.Error);
}

/// <summary>
/// Loads source plugin assemblies into collectible plugin load contexts.
/// </summary>
public sealed class PluginAssemblyLoader
{
    /// <summary>Gets the default host-shared assembly simple names.</summary>
    public static IReadOnlyList<string> DefaultHostSharedAssemblyNames { get; } =
    [
        "CodeAlta.Plugins.Abstractions",
        "CodeAlta.Plugins.Tui",
        "CodeAlta.Agent",
        "CodeAlta.Catalog",
        "Microsoft.Extensions.AI.Abstractions",
        "XenoAtom.CommandLine",
        "XenoAtom.Logging",
        "XenoAtom.Terminal.UI",
        "XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp",
        "XenoAtom.Terminal.UI.Extensions.Markdown",
        "XenoAtom.Terminal.UI.Extensions.Screenshot",
        "XenoAtom.Terminal.UI.Graphics",
    ];

    private readonly IReadOnlyList<string> _hostSharedAssemblyNames;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAssemblyLoader"/> class.
    /// </summary>
    /// <param name="hostSharedAssemblyNames">Assembly simple names shared with the host default load context.</param>
    public PluginAssemblyLoader(IEnumerable<string>? hostSharedAssemblyNames = null)
    {
        _hostSharedAssemblyNames = (hostSharedAssemblyNames ?? DefaultHostSharedAssemblyNames).ToArray();
    }

    /// <summary>
    /// Loads a build result into a collectible plugin assembly load context.
    /// </summary>
    /// <param name="buildResult">The plugin build result.</param>
    /// <returns>The load result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buildResult"/> is <see langword="null"/>.</exception>
    public PluginAssemblyLoadResult Load(PluginBuildResult buildResult)
    {
        ArgumentNullException.ThrowIfNull(buildResult);
        if (string.IsNullOrWhiteSpace(buildResult.OutputAssemblyPath))
        {
            return new PluginAssemblyLoadResult
            {
                Package = buildResult.Package,
                OutputAssemblyPath = string.Empty,
                Diagnostics =
                [
                    PluginRuntimeDiagnostic.Error(
                        PluginRuntimeDiagnosticSource.Load,
                        "Cannot load plugin because the build result does not contain an output assembly path.",
                        buildResult.Package.PackageId,
                        buildResult.Package.EntryFilePath),
                ],
            };
        }

        var outputAssemblyPath = Path.GetFullPath(buildResult.OutputAssemblyPath);
        if (!File.Exists(outputAssemblyPath))
        {
            return new PluginAssemblyLoadResult
            {
                Package = buildResult.Package,
                OutputAssemblyPath = outputAssemblyPath,
                Diagnostics =
                [
                    PluginRuntimeDiagnostic.Error(
                        PluginRuntimeDiagnosticSource.Load,
                        "Cannot load plugin because the output assembly does not exist.",
                        buildResult.Package.PackageId,
                        outputAssemblyPath),
                ],
            };
        }

        PluginAssemblyLoadContext? loadContext = null;
        try
        {
            loadContext = new PluginAssemblyLoadContext(outputAssemblyPath, _hostSharedAssemblyNames);
            var assembly = loadContext.LoadFromAssemblyPath(outputAssemblyPath);
            return new PluginAssemblyLoadResult
            {
                Package = buildResult.Package,
                OutputAssemblyPath = outputAssemblyPath,
                LoadContext = loadContext,
                Assembly = assembly,
            };
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            loadContext?.Unload();
            return new PluginAssemblyLoadResult
            {
                Package = buildResult.Package,
                OutputAssemblyPath = outputAssemblyPath,
                Diagnostics =
                [
                    PluginRuntimeDiagnostic.Error(
                        PluginRuntimeDiagnosticSource.Load,
                        $"Failed to load plugin assembly: {ex.Message}",
                        buildResult.Package.PackageId,
                        outputAssemblyPath,
                        ex),
                ],
            };
        }
    }

    /// <summary>
    /// Unloads a plugin assembly load context and attempts to verify collectibility.
    /// </summary>
    /// <param name="loadContext">The load context.</param>
    /// <param name="maxGcCycles">The maximum number of forced GC cycles.</param>
    /// <returns><see langword="true"/> when the load context was collected.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="loadContext"/> is <see langword="null"/>.</exception>
    public static bool UnloadAndVerify(PluginAssemblyLoadContext loadContext, int maxGcCycles = 10)
    {
        ArgumentNullException.ThrowIfNull(loadContext);
        return VerifyUnload(CreateUnloadWeakReference(loadContext), maxGcCycles);
    }

    /// <summary>
    /// Starts unloading a plugin assembly load context and returns a weak reference suitable for later verification.
    /// </summary>
    /// <param name="loadContext">The load context.</param>
    /// <returns>A weak reference to the unloading load context.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="loadContext"/> is <see langword="null"/>.</exception>
    public static WeakReference CreateUnloadWeakReference(PluginAssemblyLoadContext loadContext)
    {
        ArgumentNullException.ThrowIfNull(loadContext);
        var weakReference = new WeakReference(loadContext, trackResurrection: false);
        loadContext.Unload();
        return weakReference;
    }

    /// <summary>
    /// Forces bounded garbage-collection cycles to verify a previously unloaded context was collected.
    /// </summary>
    /// <param name="weakReference">The weak reference returned by <see cref="CreateUnloadWeakReference"/>.</param>
    /// <param name="maxGcCycles">The maximum number of forced GC cycles.</param>
    /// <returns><see langword="true"/> when the load context was collected.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="weakReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxGcCycles"/> is less than one.</exception>
    public static bool VerifyUnload(WeakReference weakReference, int maxGcCycles = 10)
    {
        ArgumentNullException.ThrowIfNull(weakReference);
        if (maxGcCycles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGcCycles), "At least one GC cycle is required.");
        }

        for (var i = 0; weakReference.IsAlive && i < maxGcCycles; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return !weakReference.IsAlive;
    }
}
