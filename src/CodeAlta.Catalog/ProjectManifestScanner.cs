using System.Collections.Frozen;
using System.IO.Enumeration;

namespace CodeAlta.Catalog;

/// <summary>A folder of a repository that could be a project of its map.</summary>
/// <param name="Path">The folder, relative to the root, with <c>/</c> between the names.</param>
/// <param name="Key">The key it would get.</param>
/// <param name="Markers">The files it holds that mark it as a project (<c>package.json</c>, a <c>.csproj</c>).</param>
/// <param name="Submodule">Whether it is a git submodule.</param>
/// <param name="Declared">The key of the entry of the map that declares it already; null when none does.</param>
public sealed record ProjectManifestCandidate(string Path, string Key, IReadOnlyList<string> Markers, bool Submodule, string? Declared);

/// <summary>What a scan of a repository found.</summary>
/// <param name="Candidates">The folders that could be projects, in the order of the walk.</param>
/// <param name="WorkspaceFiles">The files at the root that tell how the repository is organized, with their kind (<c>pnpm</c>, <c>cargo</c>, <c>git-submodules</c>).</param>
/// <param name="Submodules">The folders <c>.gitmodules</c> names.</param>
/// <param name="Solutions">The .NET solutions at the root.</param>
/// <param name="FoldersVisited">How many folders the walk looked at.</param>
/// <param name="Truncated">Whether the walk stopped at its bounds.</param>
public sealed record ProjectManifestScan(IReadOnlyList<ProjectManifestCandidate> Candidates, IReadOnlyList<(string File, string Kind)> WorkspaceFiles,
    IReadOnlyList<string> Submodules, IReadOnlyList<string> Solutions, int FoldersVisited, bool Truncated)
{
    /// <summary>Gets whether the repository looks like a monorepo: a workspace file, submodules, several solutions or several candidates.</summary>
    public bool LooksLikeMonorepo => WorkspaceFiles.Any(static file => file.Kind is not ("codeowners" or "npm" or "maven" or "cargo"))
        || Submodules.Count > 0 || Solutions.Count > 1 || Candidates.Count(static candidate => candidate.Declared is null) >= 3;
}

/// <summary>
/// Looks at the folders of a repository for the ones that could be projects of its map. It writes nothing: what is a
/// project and what is only a package is the user's choice. The walk is bounded, skips what tools write and never
/// follows a link.
/// </summary>
public static class ProjectManifestScanner
{
    /// <summary>The most folders a scan looks at.</summary>
    public const int MaximumFolders = 20_000;

    /// <summary>The most candidates a scan lists.</summary>
    public const int MaximumCandidates = 2_000;

    // Folders a scan never goes into: what a build, a package manager or a tool writes, and the folders of git and CodeAlta.
    private static readonly FrozenSet<string> Skipped = new[]
    {
        ".git", ".alta", ".claude", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt",
        "coverage", "__pycache__", ".venv", "venv", ".gradle", ".terraform", "artifacts", "TestResults",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>What marks a folder as a project of its own when a scan is given no marker.</summary>
    public static IReadOnlyList<string> DefaultMarkers { get; } = Array.AsReadOnly<string>(
    [
        "package.json", "*.csproj", "*.fsproj", "*.sln", "*.slnx", "Cargo.toml", "go.mod", "pyproject.toml", "setup.py", "pom.xml", "build.gradle",
        "build.gradle.kts", "Gemfile", "composer.json", "mix.exs", "Package.swift", "CMakeLists.txt", "BUILD.bazel", "project.json", "deno.json",
        "AGENTS.md",
    ]);

    // The files at the root of a repository that tell how it is organized, for an agent to read before it proposes projects.
    private static readonly ReadOnlyMemory<(string File, string Kind)> Workspaces = new (string File, string Kind)[]
    {
        ("pnpm-workspace.yaml", "pnpm"), ("package.json", "npm"), ("lerna.json", "lerna"), ("nx.json", "nx"), ("turbo.json", "turborepo"),
        ("rush.json", "rush"), ("Cargo.toml", "cargo"), ("go.work", "go"), ("WORKSPACE", "bazel"), ("WORKSPACE.bazel", "bazel"), ("MODULE.bazel", "bazel"),
        ("pants.toml", "pants"), ("settings.gradle", "gradle"), ("settings.gradle.kts", "gradle"), ("pom.xml", "maven"), (".gitmodules", "git-submodules"),
        ("CODEOWNERS", "codeowners"), (".github/CODEOWNERS", "codeowners"), ("docs/CODEOWNERS", "codeowners"),
    };

    /// <summary>Scans a repository.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="map">Its map, which says the folders declared already.</param>
    /// <param name="globs">Patterns of folders relative to the root (<c>services/*</c>, <c>packages/**</c>); none for every folder down to <paramref name="depth"/>.</param>
    /// <param name="markers">Files a folder must hold to be listed; none for <see cref="DefaultMarkers"/>, or, with patterns, for every folder they match.</param>
    /// <param name="depth">How deep the walk goes without patterns, 1 to 8.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="ArgumentException">A pattern goes out of the repository, or the depth is out of range.</exception>
    /// <exception cref="OperationCanceledException">The walk was canceled.</exception>
    public static ProjectManifestScan Scan(string repositoryPath, ProjectManifest map, IReadOnlyList<string> globs, IReadOnlyList<string> markers, int depth = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(globs);
        ArgumentNullException.ThrowIfNull(markers);
        if (depth is < 1 or > 8) throw new ArgumentException("The depth is from 1 to 8.", nameof(depth));
        var patterns = new List<string[]>();
        foreach (var glob in globs)
        {
            var normalized = glob.Trim().Replace('\\', '/').Trim('/');
            if (normalized.Length == 0 || normalized.Split('/').Any(static part => part is "" or "." or ".."))
                throw new ArgumentException($"`{glob}` is not a pattern of folders inside the repository.", nameof(globs));
            patterns.Add(normalized.Split('/'));
        }

        var root = Path.GetFullPath(repositoryPath);
        var declared = map.Projects.ToDictionary(static entry => entry.Path, static entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var submodules = ReadSubmodulePaths(root);
        var wanted = markers.Count > 0 ? markers : DefaultMarkers;
        var maximumDepth = patterns.Count == 0 ? depth : patterns.Max(static pattern => pattern.Contains("**") ? 8 : pattern.Length);
        var candidates = new List<ProjectManifestCandidate>();
        var visited = 0;
        var truncated = false;
        var queue = new Queue<(string Full, string Relative, int Depth)>();
        queue.Enqueue((root, string.Empty, 0));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (full, relative, level) = queue.Dequeue();
            if (level > 0)
            {
                var matches = patterns.Count == 0 || patterns.Any(pattern => Matches(pattern, 0, relative.Split('/'), 0));
                var found = matches ? FindMarkers(full, wanted) : [];
                var submodule = File.Exists(Path.Combine(full, ".git")) || submodules.Contains(relative);
                if (matches && (found.Count > 0 || submodule || patterns.Count > 0 && markers.Count == 0))
                {
                    if (candidates.Count >= MaximumCandidates) { truncated = true; break; }
                    candidates.Add(new(relative, ProjectManifest.KeyFrom(relative), found, submodule, declared.GetValueOrDefault(relative)));
                }
            }

            if (level >= maximumDepth) continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(full).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (Skipped.Contains(name) || name.StartsWith('.') && level == 0 && name is not ".github") continue;
                try
                {
                    // A link is not followed: it may lead out of the repository, or back into it.
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (++visited > MaximumFolders) { truncated = true; queue.Clear(); break; }
                queue.Enqueue((child, relative.Length == 0 ? name : relative + "/" + name, level + 1));
            }
        }

        var workspaces = Workspaces.ToArray().Where(file => File.Exists(Path.Combine(root, file.File))).ToArray();
        string[] solutions;
        try
        {
            solutions = [.. Directory.EnumerateFiles(root, "*.sln*").Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal).Take(50)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            solutions = [];
        }

        return new(candidates, workspaces, [.. submodules.Order(StringComparer.Ordinal)], solutions, Math.Min(visited, MaximumFolders), truncated);
    }

    // Whether the parts of a path match the parts of a pattern: `*` and `?` within a name, `**` for any folders.
    private static bool Matches(string[] pattern, int at, string[] parts, int part)
    {
        if (at == pattern.Length) return part == parts.Length;
        if (pattern[at] == "**")
        {
            for (var skip = part; skip <= parts.Length; skip++)
                if (Matches(pattern, at + 1, parts, skip)) return true;
            return false;
        }

        return part < parts.Length && FileSystemName.MatchesSimpleExpression(pattern[at], parts[part], ignoreCase: true)
            && Matches(pattern, at + 1, parts, part + 1);
    }

    // The files of a folder that a marker names, at most a few.
    private static List<string> FindMarkers(string folder, IReadOnlyList<string> markers)
    {
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (found.Count < 8 && markers.Any(marker => FileSystemName.MatchesSimpleExpression(marker, name, ignoreCase: true))) found.Add(name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    // The folders `.gitmodules` names, relative to the root.
    private static HashSet<string> ReadSubmodulePaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(root, ".gitmodules");
            if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) return paths;
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                var equals = trimmed.IndexOf('=');
                if (equals <= 0 || trimmed[..equals].Trim() != "path") continue;
                if (ProjectManifest.NormalizePath(trimmed[(equals + 1)..].Trim(), out _) is { } path) paths.Add(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return paths;
    }
}
