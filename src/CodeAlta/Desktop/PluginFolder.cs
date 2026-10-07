using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// Names the folder of a source plugin package where a request of the page names a project, so that the code
/// editor opens on a plugin: <c>plugin:global:&lt;package&gt;</c> for a package of the user,
/// <c>plugin:project:&lt;project id&gt;:&lt;package&gt;</c> for one of a project.
/// </summary>
/// <remarks>
/// The page names this id and never a path: the host finds the folder, under the plugin folder of the user or
/// of the project of the catalog. An id of a project has no colon, so the id of a project is never read as one
/// of these.
/// </remarks>
/// <param name="ProjectId">The project of a project package; null for a package of the user.</param>
/// <param name="PackageId">The id of the package: the name of its folder.</param>
internal readonly record struct PluginFolder(string? ProjectId, string PackageId)
{
    /// <summary>What every id of a plugin folder starts with.</summary>
    internal const string Prefix = "plugin:";

    private const string GlobalPrefix = Prefix + "global:";
    private const string ProjectPrefix = Prefix + "project:";

    /// <summary>The id the page names the folder with.</summary>
    internal string Id => ProjectId is null ? GlobalPrefix + PackageId : $"{ProjectPrefix}{ProjectId}:{PackageId}";

    /// <summary>The id of the folder of a package: one of the user, or one of the project that has the given id.</summary>
    /// <param name="package">The package.</param>
    /// <param name="projectId">The id of its project in the catalog; unused for a package of the user.</param>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    internal static PluginFolder Of(SourcePluginPackage package, string? projectId)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new(package.Root.Scope == PluginScope.Project ? projectId ?? package.Root.ProjectId : null, package.PackageId);
    }

    /// <summary>Reads an id of a plugin folder; false for anything else, the id of a project included.</summary>
    internal static bool TryParse(string? id, out PluginFolder folder)
    {
        folder = default;
        if (id is null || id.Length > 512) return false;
        string? project = null;
        string package;
        if (id.StartsWith(GlobalPrefix, StringComparison.Ordinal))
        {
            package = id[GlobalPrefix.Length..];
        }
        else if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            var separator = id.IndexOf(':', ProjectPrefix.Length);
            if (separator <= ProjectPrefix.Length) return false;
            project = id[ProjectPrefix.Length..separator];
            package = id[(separator + 1)..];
        }
        else
        {
            return false;
        }

        if (!ValidPackageId(package)) return false;
        folder = new(project, package);
        return true;
    }

    /// <summary>
    /// Returns <c>ok</c> with the folder of the package, or the refusal: the one of its project, or
    /// <c>project_unavailable</c> when the package has no folder.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    internal async Task<(string Status, string? Root)> ResolveAsync(ProjectCatalog projects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        string plugins;
        if (ProjectId is null)
        {
            plugins = Path.Combine(projects.Options.GlobalRoot, "plugins");
        }
        else
        {
            var project = await SettingsProjectScope.ResolveAsync(projects, ProjectId, cancellationToken).ConfigureAwait(false);
            if (project.Root is null) return (project.Status == "ok" ? "unknown_project" : project.Status, null);
            plugins = Path.Combine(project.Root, ".alta", "plugins");
        }

        var directory = Path.Combine(plugins, PackageId);
        return Directory.Exists(directory) ? ("ok", Path.GetFullPath(directory)) : ("project_unavailable", null);
    }

    // The rule of a package id: no separator, nothing that leaves the plugin folder.
    private static bool ValidPackageId(string id)
        => id is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(id[0])
           && id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
