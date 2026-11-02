namespace CodeAlta.Catalog.Skills;

/// <summary>What a folder of skills the user added is on the disk now.</summary>
public enum AddedSkillFolderState
{
    /// <summary>The folder is there and can be read.</summary>
    Ready,

    /// <summary>Nothing is at that path, or it is a file: no skill is read from it.</summary>
    Missing,

    /// <summary>The folder is there, and the system refuses to list it: no skill is read from it.</summary>
    Unreadable,

    /// <summary>The path is not a full path, or names a folder that cannot hold skills (a disk, the home folder).</summary>
    Invalid,
}

/// <summary>Why a folder cannot be added as a folder of skills.</summary>
public enum AddedSkillFolderProblem
{
    /// <summary>It can be added.</summary>
    None,

    /// <summary>No path is given.</summary>
    Required,

    /// <summary>The path is longer than <see cref="AddedSkillFolders.MaximumPathLength"/>.</summary>
    TooLong,

    /// <summary>The path is not a full one.</summary>
    NotFullPath,

    /// <summary>The path is the root of a disk.</summary>
    Disk,

    /// <summary>The path is the home folder, or a folder that holds it.</summary>
    Home,

    /// <summary>The path is the folder of CodeAlta, or a folder that holds it.</summary>
    CodeAlta,
}

/// <summary>
/// The folders of skills a user adds, beside the folders CodeAlta reads by itself: for every space (<c>[skills] folders</c>
/// of the global configuration), for one space (<c>skills.folders</c> of the file of the space) or for one project
/// (<c>skill_folders</c> of the user's file of the project, never the repository). Each holds skill folders, as
/// <c>~/.agents/skills</c> does; its skills are read where they are, and removing the folder from a list changes nothing
/// on the disk.
/// </summary>
/// <remarks>
/// Only the user adds a folder, in the window: a repository cannot name one, so it cannot make CodeAlta read skills, or
/// a sandbox read files, outside of it.
/// </remarks>
public static class AddedSkillFolders
{
    /// <summary>The most folders one list holds: the others are not read.</summary>
    public const int MaximumFolders = 32;

    /// <summary>The longest path of a folder.</summary>
    public const int MaximumPathLength = 1024;

    /// <summary>Compares two full paths as the file system of this computer does.</summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Gives the full path of a folder as a list writes it: a leading <c>~</c> stands for the home folder of the user.
    /// </summary>
    /// <param name="written">The path as the list writes it.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/> when there is none (a path with <c>~</c> then means nothing).</param>
    /// <returns>The full path without a trailing separator; <see langword="null"/> for a path that is not a full one.</returns>
    public static string? Resolve(string? written, string? userProfileRoot)
    {
        if (string.IsNullOrWhiteSpace(written)) return null;
        var path = written.Trim();
        if (path.Length > MaximumPathLength || path.Any(char.IsControl)) return null;
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userProfileRoot)) return null;
            path = path.Length == 1 ? userProfileRoot : Path.Combine(userProfileRoot, path[2..]);
        }

        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return IsVolumeRoot(full) ? full : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells why a folder cannot be added: it is not a full path, or it is a disk, the home folder, a folder that holds
    /// it, or one that holds the folder of CodeAlta. Walking such a folder for skills would read the whole of it.
    /// </summary>
    /// <param name="written">The path as the user gives it.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <param name="codeAltaRoot">The folder of CodeAlta (<c>~/.alta</c>), or <see langword="null"/>.</param>
    /// <returns>The reason, in a sentence for the user; <see langword="null"/> when the folder can be added.</returns>
    public static string? Problem(string? written, string? userProfileRoot, string? codeAltaRoot)
        => Check(written, userProfileRoot, codeAltaRoot) switch
        {
            AddedSkillFolderProblem.None => null,
            AddedSkillFolderProblem.Required => "A folder is required.",
            AddedSkillFolderProblem.TooLong => "The path of the folder is too long.",
            AddedSkillFolderProblem.NotFullPath => "Give the full path of the folder.",
            AddedSkillFolderProblem.Disk => "A whole disk cannot be a folder of skills: choose the folder that holds the skills.",
            AddedSkillFolderProblem.Home => "The home folder cannot be a folder of skills: choose the folder that holds the skills.",
            _ => "The folder of CodeAlta cannot be a folder of skills: its skills are already read from its skills folder.",
        };

    /// <summary>Tells why a folder cannot be added, as <see cref="Problem"/> does, as a value.</summary>
    /// <param name="written">The path as the user gives it.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <param name="codeAltaRoot">The folder of CodeAlta (<c>~/.alta</c>), or <see langword="null"/>.</param>
    /// <returns>The problem; <see cref="AddedSkillFolderProblem.None"/> when the folder can be added.</returns>
    public static AddedSkillFolderProblem Check(string? written, string? userProfileRoot, string? codeAltaRoot)
    {
        if (string.IsNullOrWhiteSpace(written)) return AddedSkillFolderProblem.Required;
        if (written.Trim().Length > MaximumPathLength) return AddedSkillFolderProblem.TooLong;
        if (Resolve(written, userProfileRoot) is not { } full) return AddedSkillFolderProblem.NotFullPath;
        if (IsVolumeRoot(full)) return AddedSkillFolderProblem.Disk;
        if (Holds(full, userProfileRoot)) return AddedSkillFolderProblem.Home;
        if (Holds(full, codeAltaRoot)) return AddedSkillFolderProblem.CodeAlta;
        return AddedSkillFolderProblem.None;
    }

    /// <summary>Tells what a folder of a list is on the disk now.</summary>
    /// <param name="written">The path as the list writes it.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <param name="codeAltaRoot">The folder of CodeAlta, or <see langword="null"/>.</param>
    /// <returns>Its state, and its full path when it has one.</returns>
    public static (AddedSkillFolderState State, string? FullPath) Inspect(string? written, string? userProfileRoot, string? codeAltaRoot)
    {
        var full = Resolve(written, userProfileRoot);
        if (full is null || Check(written, userProfileRoot, codeAltaRoot) != AddedSkillFolderProblem.None) return (AddedSkillFolderState.Invalid, full);
        try
        {
            if (!Directory.Exists(full)) return (AddedSkillFolderState.Missing, full);
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            _ = entries.MoveNext();
            return (AddedSkillFolderState.Ready, full);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return (AddedSkillFolderState.Unreadable, full);
        }
    }

    /// <summary>
    /// Gives the skill roots of the folders the user added, for the discovery of a session or of a page: each valid
    /// folder once per list, the first <see cref="MaximumFolders"/> of each list, in the order of the list. A folder that
    /// cannot be added (<see cref="Problem"/>) is left out.
    /// </summary>
    /// <param name="user">The folders added for every space.</param>
    /// <param name="spaceId">The space whose folders are given, or <see langword="null"/>.</param>
    /// <param name="space">The folders added for that space.</param>
    /// <param name="project">The folders added for the project.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <param name="codeAltaRoot">The folder of CodeAlta, or <see langword="null"/>.</param>
    /// <returns>The roots, most specific first.</returns>
    public static IReadOnlyList<SkillRootRegistration> Roots(IEnumerable<string>? user, string? spaceId, IEnumerable<string>? space,
        IEnumerable<string>? project, string? userProfileRoot, string? codeAltaRoot)
    {
        var roots = new List<SkillRootRegistration>();
        Add(project, SkillSourceKind.ProjectFolder, SkillScopeKind.Project, SkillRootPrecedence.ProjectFolder);
        Add(space, SkillSourceKind.SpaceFolder, SkillScopeKind.User, SkillRootPrecedence.SpaceFolder);
        Add(user, SkillSourceKind.UserFolder, SkillScopeKind.User, SkillRootPrecedence.UserFolder);
        return roots;

        void Add(IEnumerable<string>? folders, SkillSourceKind kind, SkillScopeKind scope, int precedence)
        {
            var index = 0;
            foreach (var full in Valid(folders, userProfileRoot, codeAltaRoot))
            {
                roots.Add(new SkillRootRegistration
                {
                    RootPath = full,
                    SourceKind = kind,
                    SourceId = SourceIdOf(kind, spaceId, full),
                    Scope = scope,
                    Precedence = precedence + index++,
                });
            }
        }
    }

    /// <summary>Gives the source id of the skills of an added folder: its kind, its space for a folder of a space, and its full path.</summary>
    /// <param name="kind"><see cref="SkillSourceKind.UserFolder"/>, <see cref="SkillSourceKind.SpaceFolder"/> or <see cref="SkillSourceKind.ProjectFolder"/>.</param>
    /// <param name="spaceId">The space of a folder of a space; <see langword="null"/> for the default space.</param>
    /// <param name="fullPath">The full path of the folder (<see cref="Resolve"/>).</param>
    /// <returns>The source id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not the kind of an added folder.</exception>
    public static string SourceIdOf(SkillSourceKind kind, string? spaceId, string fullPath) => kind switch
    {
        SkillSourceKind.ProjectFolder => "project-folder:" + fullPath,
        SkillSourceKind.SpaceFolder => $"space-folder:{(string.IsNullOrWhiteSpace(spaceId) ? SpaceDescriptor.DefaultId : spaceId.Trim())}:{fullPath}",
        SkillSourceKind.UserFolder => "user-folder:" + fullPath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The full paths of the folders of a list that can be read for skills: each once, at most <see cref="MaximumFolders"/>.</summary>
    /// <param name="folders">The folders, as the list writes them.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <param name="codeAltaRoot">The folder of CodeAlta, or <see langword="null"/>.</param>
    /// <returns>The full paths, in the order of the list.</returns>
    public static IReadOnlyList<string> Valid(IEnumerable<string>? folders, string? userProfileRoot, string? codeAltaRoot)
    {
        var kept = new List<string>();
        foreach (var written in (folders ?? []).Take(MaximumFolders))
        {
            if (Check(written, userProfileRoot, codeAltaRoot) != AddedSkillFolderProblem.None || Resolve(written, userProfileRoot) is not { } full) continue;
            if (!kept.Contains(full, PathComparer)) kept.Add(full);
        }

        return kept;
    }

    /// <summary>
    /// Gives a list once a folder is added at its end, or removed: each folder once, compared by its full path. Adding a
    /// folder the list has already leaves it as it is.
    /// </summary>
    /// <param name="folders">The list as it is.</param>
    /// <param name="path">The folder, as the user gives it.</param>
    /// <param name="add">Whether the folder is added, or removed.</param>
    /// <param name="userProfileRoot">The home folder of the user, or <see langword="null"/>.</param>
    /// <returns>The list as it must be written.</returns>
    public static IReadOnlyList<string> Change(IReadOnlyList<string> folders, string path, bool add, string? userProfileRoot)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(path);
        var full = Resolve(path, userProfileRoot);
        bool Same(string written) => string.Equals(written.Trim(), path.Trim(), StringComparison.Ordinal)
            || full is not null && Resolve(written, userProfileRoot) is { } other && PathComparer.Equals(other, full);
        if (!add) return [.. folders.Where(written => !Same(written))];
        return folders.Any(Same) ? folders : [.. folders, full ?? path.Trim()];
    }

    // Whether a folder is a folder or holds it.
    private static bool Holds(string folder, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        var relative = Path.GetRelativePath(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)));
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static bool IsVolumeRoot(string full)
        => Path.GetPathRoot(full) is { Length: > 0 } root && PathComparer.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(full));
}
