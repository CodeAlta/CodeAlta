using System.Collections.Concurrent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop;

/// <summary>
/// Finds the folder that a <see cref="SkillFolder"/> names: the one of the skill with that name and source, among
/// the skills discovered for the user and for the project of the id.
/// </summary>
internal sealed class SkillFolders
{
    private const int MaximumRemembered = 64;

    private readonly ProjectCatalog _projects;
    private readonly SkillManagementService _management;
    // The file of the skill last found for an id: every request of an editor names the same folder, and the
    // skills are only looked for again once that file is gone.
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Creates the lookup over the skills of a host.</summary>
    /// <param name="projects">The project catalog of the host; it maps the project of an id to its folder.</param>
    /// <param name="management">Lists the skills of the user and of a project.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal SkillFolders(ProjectCatalog projects, SkillManagementService management)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(management);
        _projects = projects;
        _management = management;
    }

    /// <summary>
    /// The id that names the folder of a listed skill; null for a skill whose name cannot be part of an id.
    /// </summary>
    /// <param name="skill">The skill, as it was listed.</param>
    /// <param name="projectId">The project the skills were listed for, or null.</param>
    /// <param name="projectRoot">The folder of that project, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="skill"/> is null.</exception>
    internal static string? IdOf(SkillDescriptor skill, string? projectId, string? projectRoot)
    {
        ArgumentNullException.ThrowIfNull(skill);
        // A skill that the project brings is only found with that project: one of its own, or one of a plugin of it.
        var ofProject = projectId is not null && projectRoot is not null
            && (skill.Scope == SkillScopeKind.Project || GitWorktreeService.IsWithin(skill.SkillFilePath, projectRoot));
        var id = new SkillFolder(ofProject ? projectId : null, skill.SourceKind, skill.Name).Id;
        return SkillFolder.TryParse(id, out _) ? id : null;
    }

    /// <summary>
    /// Returns <c>ok</c> with the folder of the skill, or the refusal: the one of its project, or
    /// <c>project_unavailable</c> when no skill has that name and source.
    /// </summary>
    internal async Task<(string Status, string? Root)> ResolveAsync(SkillFolder folder, CancellationToken cancellationToken)
    {
        string? projectRoot = null;
        if (folder.ProjectId is not null)
        {
            var project = await SettingsProjectScope.ResolveAsync(_projects, folder.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project.Root is null) return (project.Status == "ok" ? "unknown_project" : project.Status, null);
            projectRoot = project.Root;
        }

        var id = folder.Id;
        if (_files.TryGetValue(id, out var known) && File.Exists(known)) return ("ok", Path.GetDirectoryName(known));
        try
        {
            var skills = await _management.LoadAsync(SkillListingScope.Combined, projectRoot, cancellationToken).ConfigureAwait(false);
            var skill = skills.FirstOrDefault(candidate => candidate.SourceKind == folder.Source && string.Equals(candidate.Name, folder.Name, StringComparison.Ordinal));
            if (skill is null) return ("project_unavailable", null);
            var file = Path.GetFullPath(skill.SkillFilePath);
            if (!File.Exists(file) || Path.GetDirectoryName(file) is not { } root) return ("project_unavailable", null);
            if (_files.Count >= MaximumRemembered) _files.Clear();
            _files[id] = file;
            return ("ok", root);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // The skills cannot be listed (a configuration that does not parse, a root that is gone).
            return ("project_unavailable", null);
        }
    }
}
