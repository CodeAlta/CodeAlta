using CodeAlta.Catalog.Skills;

namespace CodeAlta.Desktop;

/// <summary>
/// Names the folder of a skill where a request of the page names a project, so that the code editor opens on a
/// skill: <c>skill:global:&lt;source&gt;:&lt;name&gt;</c> for a skill that is found without a project,
/// <c>skill:project:&lt;project id&gt;:&lt;source&gt;:&lt;name&gt;</c> for one that a project brings.
/// </summary>
/// <remarks>
/// The page names this id and never a path: the host finds the folder among the skills it discovers
/// (<see cref="SkillFolders"/>). An id of a project has no colon, so the id of a project is never read as one of
/// these. The folder of a skill of the user or of a project is written to; the one of a built-in skill, or of a
/// skill that a plugin brings, is only read (<see cref="ReadOnly"/>).
/// </remarks>
/// <param name="ProjectId">The project the skill is found with; null for a skill that is found without one.</param>
/// <param name="Source">Where the skill comes from.</param>
/// <param name="Name">The name of the skill.</param>
internal readonly record struct SkillFolder(string? ProjectId, SkillSourceKind Source, string Name)
{
    /// <summary>What every id of a skill folder starts with.</summary>
    internal const string Prefix = "skill:";

    private const string GlobalPrefix = Prefix + "global:";
    private const string ProjectPrefix = Prefix + "project:";
    private const int MaximumNameLength = 128;

    /// <summary>The id the page names the folder with.</summary>
    internal string Id => ProjectId is null ? $"{GlobalPrefix}{Source}:{Name}" : $"{ProjectPrefix}{ProjectId}:{Source}:{Name}";

    /// <summary>Whether nothing is changed in the folder: it is not one of the user or of a project.</summary>
    internal bool ReadOnly => Source is not (SkillSourceKind.ProjectAlta or SkillSourceKind.ProjectCommon or SkillSourceKind.UserAlta or SkillSourceKind.UserCommon);

    /// <summary>Reads an id of a skill folder; false for anything else, the id of a project included.</summary>
    internal static bool TryParse(string? id, out SkillFolder folder)
    {
        folder = default;
        if (id is null || id.Length > 512) return false;
        string? project = null;
        ReadOnlySpan<char> rest;
        if (id.StartsWith(GlobalPrefix, StringComparison.Ordinal))
        {
            rest = id.AsSpan(GlobalPrefix.Length);
        }
        else if (id.StartsWith(ProjectPrefix, StringComparison.Ordinal))
        {
            var end = id.IndexOf(':', ProjectPrefix.Length);
            if (end <= ProjectPrefix.Length) return false;
            project = id[ProjectPrefix.Length..end];
            rest = id.AsSpan(end + 1);
        }
        else
        {
            return false;
        }

        // The name comes last: it is the only part that may hold a colon.
        var separator = rest.IndexOf(':');
        if (separator <= 0) return false;
        var name = rest[(separator + 1)..];
        if (!TryParseSource(rest[..separator], out var source) || !ValidName(name)) return false;
        folder = new(project, source, name.ToString());
        return true;
    }

    // The name of a source as the listing gives it: a number is not one.
    private static bool TryParseSource(ReadOnlySpan<char> text, out SkillSourceKind source)
    {
        source = default;
        foreach (var character in text)
        {
            if (!char.IsAsciiLetter(character)) return false;
        }

        return Enum.TryParse(text, ignoreCase: false, out source) && Enum.IsDefined(source);
    }

    // A name is compared with the names of the skills that are found, never made into a path.
    private static bool ValidName(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > MaximumNameLength) return false;
        foreach (var character in name)
        {
            if (char.IsControl(character)) return false;
        }

        return true;
    }
}
