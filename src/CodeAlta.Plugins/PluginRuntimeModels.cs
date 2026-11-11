using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Identifies a CodeAlta plugin runtime diagnostic source.
/// </summary>
public enum PluginRuntimeDiagnosticSource
{
    /// <summary>Configuration loading or enablement.</summary>
    Config,
    /// <summary>Plugin root or build-file generation.</summary>
    RootGeneration,
    /// <summary>Source package discovery.</summary>
    Discovery,
    /// <summary>Plugin build execution.</summary>
    Build,
    /// <summary>Assembly load context resolution.</summary>
    Load,
    /// <summary>Plugin initialization and activation.</summary>
    Activation,
    /// <summary>Contribution collection or registration.</summary>
    Contribution,
    /// <summary>Plugin callback invocation.</summary>
    Callback,
    /// <summary>Source change monitoring.</summary>
    SourceChange,
    /// <summary>Plugin deactivation or unload.</summary>
    Unload,
}

/// <summary>
/// Identifies the runtime state of a plugin instance.
/// </summary>
public enum PluginRuntimeState
{
    /// <summary>The plugin package was discovered.</summary>
    Discovered,
    /// <summary>The plugin assembly was loaded.</summary>
    Loaded,
    /// <summary>The plugin instance was initialized.</summary>
    Initialized,
    /// <summary>The plugin instance is active.</summary>
    Active,
    /// <summary>The plugin instance is being deactivated.</summary>
    Deactivating,
    /// <summary>The plugin instance was deactivated.</summary>
    Deactivated,
    /// <summary>The plugin load unit was unloaded.</summary>
    Unloaded,
    /// <summary>The plugin failed in discovery, build, load, activation, or unload.</summary>
    Failed,
}

/// <summary>
/// Identifies how a plugin load unit is provided.
/// </summary>
public enum PluginLoadUnitKind
{
    /// <summary>A dynamic source plugin built from a plugin package folder.</summary>
    Source,
    /// <summary>A built-in plugin loaded from the host default assembly load context.</summary>
    BuiltIn,
}

/// <summary>
/// Describes a plugin runtime diagnostic.
/// </summary>
public sealed record PluginRuntimeDiagnostic
{
    /// <summary>Gets the diagnostic severity.</summary>
    public required PluginDiagnosticSeverity Severity { get; init; }

    /// <summary>Gets the diagnostic source.</summary>
    public required PluginRuntimeDiagnosticSource Source { get; init; }

    /// <summary>Gets the diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the package id associated with the diagnostic, when known.</summary>
    public string? PackageId { get; init; }

    /// <summary>Gets the runtime key associated with the diagnostic, when known.</summary>
    public string? RuntimeKey { get; init; }

    /// <summary>Gets the path associated with the diagnostic, when known.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the captured exception information, when an exception caused the diagnostic.</summary>
    public PluginExceptionInfo? Exception { get; init; }

    /// <summary>Gets operation metadata associated with the diagnostic.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the diagnostic timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates an informational diagnostic.
    /// </summary>
    /// <param name="source">The diagnostic source.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="packageId">The package id, when known.</param>
    /// <param name="path">The related path, when known.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginRuntimeDiagnostic Info(PluginRuntimeDiagnosticSource source, string message, string? packageId = null, string? path = null)
        => Create(PluginDiagnosticSeverity.Info, source, message, packageId, path, null);

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    /// <param name="source">The diagnostic source.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="packageId">The package id, when known.</param>
    /// <param name="path">The related path, when known.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginRuntimeDiagnostic Warning(PluginRuntimeDiagnosticSource source, string message, string? packageId = null, string? path = null)
        => Create(PluginDiagnosticSeverity.Warning, source, message, packageId, path, null);

    /// <summary>
    /// Creates an error diagnostic.
    /// </summary>
    /// <param name="source">The diagnostic source.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="packageId">The package id, when known.</param>
    /// <param name="path">The related path, when known.</param>
    /// <param name="exception">The exception, when known.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginRuntimeDiagnostic Error(PluginRuntimeDiagnosticSource source, string message, string? packageId = null, string? path = null, Exception? exception = null)
        => Create(PluginDiagnosticSeverity.Error, source, message, packageId, path, exception);

    private static PluginRuntimeDiagnostic Create(
        PluginDiagnosticSeverity severity,
        PluginRuntimeDiagnosticSource source,
        string message,
        string? packageId,
        string? path,
        Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginRuntimeDiagnostic
        {
            Severity = severity,
            Source = source,
            Message = message,
            PackageId = packageId,
            Path = path,
            Exception = exception is null ? null : PluginExceptionInfo.FromException(exception),
        };
    }
}

/// <summary>
/// Describes a dynamic source plugin root.
/// </summary>
public sealed record PluginRoot
{
    /// <summary>Gets the plugin root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Gets the plugin scope assigned from this root.</summary>
    public required PluginScope Scope { get; init; }

    /// <summary>Gets the project identifier for project-scoped roots, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project path for project-scoped roots, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Gets whether the root is the one of a folder the project CodeAlta started in inherits its setup from (the
    /// repository of a sub-project): a package of the same id of the project, or of a nearer folder, takes its place.
    /// </summary>
    public bool Inherited { get; init; }
}

/// <summary>
/// Describes optional source package sidecars discovered without requiring them.
/// </summary>
public sealed record SourcePluginSidecars
{
    /// <summary>Gets the README path, when present.</summary>
    public string? ReadmePath { get; init; }

    /// <summary>Gets the skills directory path, when present.</summary>
    public string? SkillsDirectory { get; init; }

    /// <summary>Gets the prompts directory path, when present.</summary>
    public string? PromptsDirectory { get; init; }

    /// <summary>Gets the templates directory path, when present.</summary>
    public string? TemplatesDirectory { get; init; }

    /// <summary>Gets the themes directory path, when present.</summary>
    public string? ThemesDirectory { get; init; }

    /// <summary>Gets the assets directory path, when present.</summary>
    public string? AssetsDirectory { get; init; }
}

/// <summary>
/// Describes a source plugin package discovered under a plugin root.
/// </summary>
public sealed record SourcePluginPackage
{
    /// <summary>Gets the package id derived from the folder name.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets the owning plugin root.</summary>
    public required PluginRoot Root { get; init; }

    /// <summary>Gets the source package directory.</summary>
    public required string PackageDirectory { get; init; }

    /// <summary>Gets the plugin entry file path.</summary>
    public required string EntryFilePath { get; init; }

    /// <summary>Gets discovered optional sidecars.</summary>
    public SourcePluginSidecars Sidecars { get; init; } = new();

    /// <summary>Gets unsupported build files found in the package directory.</summary>
    public IReadOnlyList<string> UnsupportedBuildFiles { get; init; } = [];

    /// <summary>Gets diagnostics associated with package discovery.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}
