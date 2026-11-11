using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes project context used for project-scoped plugin roots.
/// </summary>
public sealed record PluginProjectContext
{
    /// <summary>Gets the project identifier, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the project root path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    /// Gets the folders the project inherits its setup from, the nearest first: for a sub-project of a monorepo, the
    /// declared folders above it, then its repository, whose configuration it reads under its own. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> InheritedRoots { get; init; } = [];
}

/// <summary>
/// Resolves CodeAlta plugin runtime paths.
/// </summary>
public sealed class PluginRuntimePathService
{
    private readonly string _globalCodeAltaRoot;
    private readonly PluginProjectContext? _projectContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRuntimePathService"/> class.
    /// </summary>
    /// <param name="globalCodeAltaRoot">The global CodeAlta root, normally <c>~/.alta</c>.</param>
    /// <param name="projectContext">The selected project context, when known.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="globalCodeAltaRoot"/> is empty.</exception>
    public PluginRuntimePathService(string globalCodeAltaRoot, PluginProjectContext? projectContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalCodeAltaRoot);
        _globalCodeAltaRoot = NormalizeDirectory(globalCodeAltaRoot);
        _projectContext = projectContext is null
            ? null
            : projectContext with { ProjectPath = NormalizeDirectory(projectContext.ProjectPath) };
    }

    /// <summary>Gets the global plugin root path.</summary>
    public string GlobalPluginRoot => Path.Combine(_globalCodeAltaRoot, "plugins");

    /// <summary>Gets the project plugin root path, when a project is known.</summary>
    public string? ProjectPluginRoot => _projectContext is null
        ? null
        : Path.Combine(_projectContext.ProjectPath, ".alta", "plugins");

    /// <summary>
    /// Gets plugin roots that should be considered during startup.
    /// </summary>
    /// <param name="includeMissingGlobalRoot">Whether to include the global root even when it does not exist.</param>
    /// <param name="includeMissingProjectRoot">Whether to include the project root even when it does not exist.</param>
    /// <returns>The candidate plugin roots in deterministic scope order.</returns>
    public IReadOnlyList<PluginRoot> GetCandidateRoots(bool includeMissingGlobalRoot = false, bool includeMissingProjectRoot = false)
    {
        var roots = new List<PluginRoot>(2);
        var globalRoot = GlobalPluginRoot;
        if (includeMissingGlobalRoot || Directory.Exists(globalRoot))
        {
            roots.Add(new PluginRoot
            {
                RootPath = NormalizeDirectory(globalRoot),
                Scope = PluginScope.Global,
            });
        }

        var projectRoot = ProjectPluginRoot;
        if (!string.IsNullOrWhiteSpace(projectRoot) && (includeMissingProjectRoot || Directory.Exists(projectRoot)))
        {
            roots.Add(new PluginRoot
            {
                RootPath = NormalizeDirectory(projectRoot),
                Scope = PluginScope.Project,
                ProjectId = _projectContext?.ProjectId,
                ProjectPath = _projectContext?.ProjectPath,
            });
        }

        return roots;
    }

    /// <summary>
    /// Normalizes a directory path for runtime comparisons and output.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <returns>The normalized full path without a trailing directory separator except for root paths.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is empty.</exception>
    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
