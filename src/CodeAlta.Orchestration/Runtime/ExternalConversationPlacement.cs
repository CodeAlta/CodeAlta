using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Where a conversation started outside CodeAlta is listed.</summary>
/// <param name="ProjectId">The project that lists it; null when no project does (the "no project" group).</param>
/// <param name="Tag">
/// Where it started, when that is not the folder of the project: a subfolder or a Claude Code worktree of the project,
/// another checkout of its repository, or the start folder itself for a conversation of no project.
/// </param>
internal sealed record ExternalConversationPlace(string? ProjectId, string? Tag);

/// <summary>
/// Chooses the one project that lists a conversation started outside CodeAlta (Claude Code: in VS Code or a terminal),
/// from the folder it started in and the git checkout of that folder.
/// </summary>
/// <remarks>
/// <para>In order, the conversation is listed under:</para>
/// <list type="number">
/// <item>the deepest project whose folder holds the start folder, in the same checkout or repository (or with no
/// checkout): its folder, a subfolder, a worktree Claude Code made inside it, even one removed since;</item>
/// <item>when the start folder is in another checkout of the repository of projects: the project whose sessions work
/// in that checkout, else the project at the main checkout of the repository, else the first of them by path;</item>
/// <item>the deepest project whose folder holds the start folder across checkouts (a project that is a parent folder of
/// other repositories);</item>
/// <item>no project.</item>
/// </list>
/// <para>
/// A conversation is never listed twice. The checkouts are found from the <c>.git</c> files and folders, without
/// running git; what was found is kept for the life of the instance, made for one listing.
/// </para>
/// </remarks>
internal sealed class ExternalConversationPlacement
{
    private static readonly StringComparison FolderComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer FolderComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string ClaudeWorktrees = Path.Combine(".claude", "worktrees") + Path.DirectorySeparatorChar;

    private readonly List<PlacedProject> _projects;
    private readonly Dictionary<string, string> _checkoutProjects = new(FolderComparer);
    private readonly Dictionary<string, ExternalConversationPlace> _places = new(FolderComparer);

    /// <summary>Initializes a placement over the projects of the catalog.</summary>
    /// <param name="projects">The projects: their identity and their folder.</param>
    /// <param name="sessionCheckouts">The worktrees sessions work in, with the folder of the project of each session.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ExternalConversationPlacement(IEnumerable<(string Id, string Path)> projects, IEnumerable<(string Checkout, string ProjectPath)> sessionCheckouts)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessionCheckouts);
        _projects = [.. projects
            .Where(static project => !string.IsNullOrWhiteSpace(project.Id) && IsLocalFolder(project.Path))
            .Select(static project => new PlacedProject(project.Id, Normalize(project.Path)))
            .OrderBy(static project => project.Path, FolderComparer)];
        foreach (var (checkout, projectPath) in sessionCheckouts)
        {
            if (!IsLocalFolder(checkout) || !IsLocalFolder(projectPath)) continue;
            var path = Normalize(projectPath);
            if (_projects.Find(project => string.Equals(project.Path, path, FolderComparison)) is { } project)
                _checkoutProjects.TryAdd(Normalize(checkout), project.Id);
        }
    }

    /// <summary>Chooses where a conversation that started in a folder is listed.</summary>
    /// <param name="startDirectory">The folder it started in; null or a folder of another system lists it under no project.</param>
    /// <returns>The project that lists it, or none, and the tag of its row.</returns>
    public ExternalConversationPlace Place(string? startDirectory)
    {
        if (!IsLocalFolder(startDirectory)) return new(null, string.IsNullOrWhiteSpace(startDirectory) ? null : startDirectory);
        var start = Normalize(startDirectory);
        if (_places.TryGetValue(start, out var known)) return known;

        var place = Choose(start);
        _places[start] = place;
        return place;
    }

    private ExternalConversationPlace Choose(string start)
    {
        var containing = _projects.Where(project => IsWithin(start, project.Path)).OrderByDescending(static project => project.Path.Length).ToList();
        var checkout = GitWorktreeService.FindCheckoutRoot(start) is { } root ? Normalize(root) : null;
        var common = checkout is not null && GitWorktreeService.FindCommonDirectory(start) is { } found ? Normalize(found) : null;

        // 1. A project of that very checkout, or of no checkout, or of the same repository that holds the checkout (a
        // Claude Code worktree inside a project that is itself a linked worktree).
        if ((checkout is null
                ? containing.FirstOrDefault()
                : containing.Find(project => IsWithin(project.Path, checkout) || (common is not null && string.Equals(project.CommonDirectory, common, FolderComparison))))
            is { } inside)
            return new(inside.Id, Tag(inside.Path, start));

        // 2. Another checkout of the repository of projects.
        if (checkout is not null && common is not null)
        {
            var same = _projects.Where(project => string.Equals(project.CommonDirectory, common, FolderComparison)).ToList();
            if (same.Count > 0)
            {
                var main = MainCheckout(common);
                var chosen = (_checkoutProjects.TryGetValue(checkout, out var used) ? same.Find(project => project.Id == used) : null)
                    ?? (main is null ? null : same.Find(project => string.Equals(project.Path, main, FolderComparison)))
                    ?? same[0];
                return new(chosen.Id, Path.GetRelativePath(Path.GetDirectoryName(checkout) ?? checkout, start));
            }
        }

        // 3. A project that holds the folder across checkouts.
        if (containing.FirstOrDefault() is { } parent) return new(parent.Id, Tag(parent.Path, start));
        return new(null, start);
    }

    // The start folder from the folder of the project; a worktree Claude Code made is named by its own name.
    private static string? Tag(string projectPath, string start)
    {
        if (string.Equals(projectPath, start, FolderComparison)) return null;
        var relative = Path.GetRelativePath(projectPath, start);
        return relative.StartsWith(ClaudeWorktrees, FolderComparison) && relative.Length > ClaudeWorktrees.Length ? relative[ClaudeWorktrees.Length..] : relative;
    }

    // The checkout git keeps the repository in: the parent of a `.git` folder.
    private static string? MainCheckout(string common)
        => string.Equals(Path.GetFileName(Normalize(common)), ".git", FolderComparison) && Path.GetDirectoryName(Normalize(common)) is { } parent
            ? Normalize(parent)
            : null;

    private static bool IsWithin(string folder, string parent)
    {
        if (string.Equals(folder, parent, FolderComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return folder.StartsWith(prefix, FolderComparison);
    }

    private static bool IsLocalFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? folder)
        => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder);

    private static string Normalize(string folder)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return folder; }
    }

    private sealed class PlacedProject(string id, string path)
    {
        private string? _commonDirectory;
        private bool _resolved;

        public string Id { get; } = id;

        public string Path { get; } = path;

        // The repository of the project, read once.
        public string? CommonDirectory
        {
            get
            {
                if (!_resolved)
                {
                    _commonDirectory = GitWorktreeService.FindCommonDirectory(Path) is { } common ? Normalize(common) : null;
                    _resolved = true;
                }

                return _commonDirectory;
            }
        }
    }
}
