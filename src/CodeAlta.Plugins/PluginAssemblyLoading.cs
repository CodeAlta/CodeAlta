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
    private readonly PluginAuthoringProfile _authoringProfile;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAssemblyLoadContext"/> class.
    /// </summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="hostSharedAssemblyNames">Assembly simple names that must resolve from the default load context.</param>
    /// <exception cref="ArgumentException">Thrown when the main path or an additional name is invalid, or a name requires Terminal.</exception>
    /// <remarks>Defaults to Neutral. Additional shared names cannot remove mandatory identities; rich hosts must select Terminal.</remarks>
    public PluginAssemblyLoadContext(string mainAssemblyPath, IEnumerable<string>? hostSharedAssemblyNames = null)
        : this(mainAssemblyPath, PluginAuthoringProfile.Neutral, hostSharedAssemblyNames)
    {
    }

    /// <summary>Creates a collectible context using an explicit authoring profile.</summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="authoringProfile">The host's assembly authoring profile, not its interactivity.</param>
    /// <exception cref="ArgumentException">Thrown when the main path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid, before context creation.</exception>
    public PluginAssemblyLoadContext(string mainAssemblyPath, PluginAuthoringProfile authoringProfile)
        : this(mainAssemblyPath, authoringProfile, null)
    {
    }

    /// <summary>Creates a context with additional shared identities; mandatory profile identities cannot be removed.</summary>
    /// <param name="mainAssemblyPath">The plugin output assembly path.</param>
    /// <param name="authoringProfile">The explicit host authoring profile.</param>
    /// <param name="hostSharedAssemblyNames">Additional shared names, or null for the profile defaults.</param>
    /// <exception cref="ArgumentException">Thrown when a path/name is invalid or an additional name conflicts with Neutral.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid, before context creation.</exception>
    public PluginAssemblyLoadContext(string mainAssemblyPath, PluginAuthoringProfile authoringProfile, IEnumerable<string>? hostSharedAssemblyNames)
        : this(CreateOptions(mainAssemblyPath, authoringProfile, hostSharedAssemblyNames))
    {
    }

    private PluginAssemblyLoadContext((string Path, PluginAuthoringProfile Profile, string[] SharedNames) options)
        : base($"CodeAlta.Plugin:{Path.GetFileNameWithoutExtension(options.Path)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _authoringProfile = options.Profile;
        _hostSharedAssemblyNames = new HashSet<string>(options.SharedNames, StringComparer.OrdinalIgnoreCase);
        _resolver = new AssemblyDependencyResolver(options.Path);
    }

    private static (string Path, PluginAuthoringProfile Profile, string[] SharedNames) CreateOptions(
        string path, PluginAuthoringProfile profile, IEnumerable<string>? additionalNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return (path, profile, PluginAuthoringPolicy.GetSharedAssemblies(profile, additionalNames));
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostShared(assemblyName))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
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
    /// <exception cref="FileLoadException">Thrown when a terminal assembly is requested under Neutral; reserved identities never resolve privately.</exception>
    public string? ResolveManagedAssemblyPath(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        if (IsHostShared(assemblyName))
        {
            return null;
        }

        return _resolver.ResolveAssemblyToPath(assemblyName);
    }

    internal void ValidateMainAssembly(string mainAssemblyPath)
        => PluginAssemblyReferenceAdmission.Inspect(mainAssemblyPath, _authoringProfile, _hostSharedAssemblyNames, _resolver.ResolveAssemblyToPath);

    private bool IsHostShared(AssemblyName assemblyName)
    {
        var binding = PluginAuthoringPolicy.ClassifyAssembly(_authoringProfile, assemblyName.Name ?? string.Empty, _hostSharedAssemblyNames);
        if (binding == PluginAssemblyBinding.Forbidden)
            throw new FileLoadException($"Assembly '{assemblyName.Name}' requires the Terminal authoring profile.");
        return binding == PluginAssemblyBinding.Host;
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
    /// <summary>Gets the Terminal compatibility catalog of host-shared assembly simple names.</summary>
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
    private readonly PluginAuthoringProfile _authoringProfile;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAssemblyLoader"/> class.
    /// </summary>
    /// <param name="hostSharedAssemblyNames">Assembly simple names shared with the host default load context.</param>
    /// <remarks>Defaults to Neutral. Lists now add shared identities instead of replacing the mandatory defaults.</remarks>
    /// <exception cref="ArgumentException">Thrown when an additional name is invalid or requires Terminal.</exception>
    public PluginAssemblyLoader(IEnumerable<string>? hostSharedAssemblyNames = null)
        : this(PluginAuthoringProfile.Neutral, hostSharedAssemblyNames)
    {
    }

    /// <summary>Creates a loader for an explicit host authoring profile.</summary>
    /// <param name="authoringProfile">The assembly profile, independent of presentation capabilities.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid.</exception>
    public PluginAssemblyLoader(PluginAuthoringProfile authoringProfile)
        : this(authoringProfile, null)
    {
    }

    /// <summary>Creates a loader retaining mandatory profile identities alongside additional shared names.</summary>
    /// <param name="authoringProfile">The explicit authoring profile.</param>
    /// <param name="hostSharedAssemblyNames">Additional shared names, or null for profile defaults.</param>
    /// <exception cref="ArgumentException">Thrown when an additional name is invalid or conflicts with Neutral.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the profile is invalid.</exception>
    public PluginAssemblyLoader(PluginAuthoringProfile authoringProfile, IEnumerable<string>? hostSharedAssemblyNames)
    {
        _hostSharedAssemblyNames = PluginAuthoringPolicy.GetSharedAssemblies(authoringProfile, hostSharedAssemblyNames);
        _authoringProfile = authoringProfile;
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
            loadContext = new PluginAssemblyLoadContext(outputAssemblyPath, _authoringProfile, _hostSharedAssemblyNames);
            loadContext.ValidateMainAssembly(outputAssemblyPath);
            var assembly = LoadFromMemory(loadContext, outputAssemblyPath);
            return new PluginAssemblyLoadResult
            {
                Package = buildResult.Package,
                OutputAssemblyPath = outputAssemblyPath,
                LoadContext = loadContext,
                Assembly = assembly,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
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

    // The assembly of a package is built again to the same file while this version runs, and a file that a
    // load context maps stays locked on Windows until the context is collected. So the plugin assembly is
    // read into memory, with its symbols for the line numbers of a stack trace. Its private dependencies keep
    // their files: a build copies them only when they change.
    private static Assembly LoadFromMemory(PluginAssemblyLoadContext loadContext, string assemblyPath)
    {
        using var image = new MemoryStream(File.ReadAllBytes(assemblyPath), writable: false);
        var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(symbolsPath)) return loadContext.LoadFromStream(image);
        using var symbols = new MemoryStream(File.ReadAllBytes(symbolsPath), writable: false);
        return loadContext.LoadFromStream(image, symbols);
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
