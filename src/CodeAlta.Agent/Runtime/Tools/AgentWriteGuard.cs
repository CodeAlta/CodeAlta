namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Where the file tools of a session that does without <c>shell_command</c> may write: inside the folders it works
/// in, but not in the files that CodeAlta, git or a coding agent read as code or as settings. A write there would run
/// a command later (a plugin, an MCP server, a git hook or the fsmonitor of git, a hook of Claude Code, the command of
/// an automation) or give the session back the tools it does without (the configuration, the spaces, the prompts).
/// </summary>
/// <remarks>
/// A path is checked as the system would follow it: its links are resolved first, so that a link inside a folder
/// that may be written does not lead out of it.
/// </remarks>
public sealed class AgentWriteGuard
{
    // The folders, at any depth, that git and coding agents read their configuration, their hooks and their MCP
    // servers from.
    private static readonly string[] ProtectedFolderNames = [".git", ".claude", ".codex", ".copilot", ".gemini", ".vscode", ".cursor"];

    // The most entries a folder that is deleted or moved is looked through for what it may not take away.
    private const int MaximumTreeEntries = 100_000;

    // The most links one path is followed through.
    private const int MaximumLinks = 40;

    // The files of other tools that CodeAlta reads MCP servers from, at any depth.
    private static readonly string[] ProtectedFileNames = [".mcp.json"];

    // What a session writes in the `.alta` folder of a project: its plans and its tasks. The rest (plugins, prompts,
    // skills, MCP servers, settings) is the user's.
    private static readonly string[] WritableAltaFolderNames = ["plans", "tasks"];

    private readonly string[] _writableRoots;
    private readonly string[] _protectedRoots;
    private readonly string[] _writableExceptions;
    private readonly string? _home;

    /// <summary>
    /// Creates the guard of a session.
    /// </summary>
    /// <param name="writableRoots">The folders the session works in: its working directory, its worktree, its projects.</param>
    /// <param name="protectedRoots">Folders it never writes in, even inside those: the folders of CodeAlta.</param>
    /// <param name="writableExceptions">Folders it writes in, even inside a protected one: its scratchpad.</param>
    /// <param name="homeDirectory">The home folder of the user, whose entries starting with a dot (the settings of
    /// shells, git and tools) and whose <c>Library</c> and <c>AppData</c> are not written; null for none.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    public AgentWriteGuard(IEnumerable<string> writableRoots, IEnumerable<string> protectedRoots, IEnumerable<string> writableExceptions, string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(writableRoots);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        ArgumentNullException.ThrowIfNull(writableExceptions);
        _home = string.IsNullOrWhiteSpace(homeDirectory) ? null : Normalize([homeDirectory]).FirstOrDefault();
        // A folder of the session that is the root of a disk, or above the home folder, would let it write the
        // programs of the system: it writes in none.
        _writableRoots = [.. Normalize(writableRoots).Where(root => Path.GetPathRoot(root) is not { } top || !PathEquals(top, root))
            .Where(root => _home is null || PathEquals(root, _home) || !IsInside(root, _home))];
        _protectedRoots = Normalize(protectedRoots);
        _writableExceptions = Normalize(writableExceptions);
    }

    /// <summary>
    /// Gets whether the guard is the one of a sandboxed session, which has its shell: its refusals say that the sandbox
    /// refuses, not that <c>shell_command</c> is turned off.
    /// </summary>
    public bool InSandbox { get; init; }

    /// <summary>Gets the folders the session may write in, with their links resolved.</summary>
    public IReadOnlyList<string> WritableRoots => _writableRoots;

    /// <summary>
    /// Says why a path may not be written, created, renamed or deleted, or null when it may.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The reason, for the model; null when the path may be written.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public string? Check(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string real;
        try
        {
            real = RealPath(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return $"'{path}' is not a path this session can write.";
        }

        // A name that Windows reads as another one: a stream of a file or a folder (`.git::$INDEX_ALLOCATION`), or
        // trailing dots and spaces, which it drops.
        if (OperatingSystem.IsWindows() && real[(Path.GetPathRoot(real)?.Length ?? 0)..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(static name => name.Contains(':') || name.EndsWith('.') || name.EndsWith(' ')))
        {
            return $"'{path}' has a name this session does not write.";
        }

        if (_writableExceptions.Any(root => IsInside(root, real)))
        {
            return null;
        }

        // The deepest folder of the session that holds the path: a worktree under `~/.alta/worktrees` or
        // `<project>/.alta/worktrees` is the session's own, whatever is above it.
        if (_writableRoots.Where(candidate => IsInside(candidate, real)).OrderByDescending(static candidate => candidate.Length).FirstOrDefault() is not { } root)
        {
            return $"'{path}' is outside the folders this session works in. {Because("writes only in its own folders")}";
        }

        if (_protectedRoots.Any(@protected => IsInside(root, @protected) && IsInside(@protected, real)) || IsProtectedHomeEntry(root, real))
        {
            return $"'{path}' holds settings of CodeAlta or of the user. {Because("does not change them")}";
        }

        if (ProtectedSegment(root, real) is { } segment)
        {
            return $"'{path}' is in '{segment}', which git, CodeAlta or a coding agent reads as settings or code (hooks, plugins, MCP servers, prompts). " +
                   Because("does not write there");
        }

        return null;
    }

    /// <summary>
    /// Says why a folder or a file may not be deleted or moved, or null when it may: as <see cref="Check"/>, and a
    /// folder that is, or holds, a folder of the session, a folder of CodeAlta or an entry that may not be written
    /// (the <c>.git</c> or the <c>.alta</c> of a project in it) is kept where it is.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The reason, for the model; null when the path may be deleted or moved.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public string? CheckTree(string path)
    {
        if (Check(path) is { } refusal) return refusal;
        string real;
        try
        {
            real = RealPath(Path.GetFullPath(path));
            if (!Directory.Exists(real)) return null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return $"'{path}' is not a path this session can change.";
        }

        if (_writableRoots.Concat(_protectedRoots).Any(root => IsInside(real, root)))
        {
            return $"'{path}' is, or holds, a folder this session works in or a folder of CodeAlta. {Because("does not delete or move it")}";
        }

        var count = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                if (++count > MaximumTreeEntries)
                {
                    return $"'{path}' holds too many files to be looked through. {Because("does not delete or move it")}";
                }

                if (Check(entry.FullName) is { } inner)
                {
                    return $"'{path}' holds what this session may not change: {inner}";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"'{path}' holds files this session cannot look through. {Because("does not delete or move it")}";
        }

        return null;
    }

    // Why the session does not do it: its shell is turned off, or its sandbox keeps it from it.
    private string Because(string what) => InSandbox ? $"The sandbox of this session {what}." : $"`shell_command` is turned off for this session: it {what}.";

    // The first part of the path below the folder of the session that is read as settings or code.
    private static string? ProtectedSegment(string root, string real)
    {
        var all = Path.GetRelativePath(root, real).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < all.Length; index++)
        {
            var name = all[index];
            if (ProtectedFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase) || ProtectedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return name;
            }

            if (name.Equals(".alta", StringComparison.OrdinalIgnoreCase)
                && (index + 1 >= all.Length || !WritableAltaFolderNames.Contains(all[index + 1], StringComparer.OrdinalIgnoreCase)))
            {
                return name;
            }

            // `.github/mcp.json`, read for MCP servers like `.mcp.json`.
            if (name.Equals(".github", StringComparison.OrdinalIgnoreCase) && index + 1 < all.Length && all[index + 1].Equals("mcp.json", StringComparison.OrdinalIgnoreCase))
            {
                return ".github/mcp.json";
            }
        }

        return null;
    }

    // An entry of the home folder that starts with a dot, or its Library or AppData, when the session works in the
    // home folder or above it.
    private bool IsProtectedHomeEntry(string root, string real)
    {
        if (_home is null || !IsInside(root, _home) || !IsInside(_home, real) || PathEquals(_home, real))
        {
            return false;
        }

        var first = Path.GetRelativePath(_home, real).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[0];
        return first.StartsWith('.') || first.Equals("Library", StringComparison.OrdinalIgnoreCase) || first.Equals("AppData", StringComparison.OrdinalIgnoreCase);
    }

    // The folders with their links resolved; one whose links cannot be followed is kept as it is written.
    private static string[] Normalize(IEnumerable<string> paths)
        => [.. paths.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path =>
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                try
                {
                    return RealPath(full);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return full;
                }
            })
            .Distinct(PathComparer)];

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathEquals(string left, string right) => PathComparer.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right));

    private static bool IsInside(string root, string path)
    {
        if (PathEquals(root, path)) return true;
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    // The path with every link along it resolved, as far as it exists: what follows is appended unchanged. The
    // target of a link is resolved again, for the links above it; a path with too many links is refused.
    private static string RealPath(string fullPath) => RealPath(fullPath, 0);

    private static string RealPath(string fullPath, int links)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;
        var current = root;
        var segments = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var next = Path.Combine(current, segments[index]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && info.LinkTarget is null)
            {
                // Nothing further exists: the rest is created as written.
                return Path.Combine([next, .. segments[(index + 1)..]]);
            }

            // The short name Windows may give a folder (`ALTA~1` for `.alta`) is the folder under its own name.
            if (OperatingSystem.IsWindows() && segments[index].Contains('~')
                && new DirectoryInfo(current).EnumerateFileSystemInfos(segments[index]).FirstOrDefault() is { } named)
            {
                next = named.FullName;
                info = named;
            }

            if (info.LinkTarget is not null)
            {
                if (links >= MaximumLinks)
                {
                    throw new IOException("Too many links.");
                }

                // The target as the link names it, from the folder of the link, then resolved as a path of its own.
                var target = Path.GetFullPath(info.LinkTarget, current);
                return RealPath(Path.Combine([target, .. segments[(index + 1)..]]), links + 1);
            }

            current = next;
        }

        return current;
    }
}
