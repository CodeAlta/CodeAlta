using System.Collections.Frozen;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>What a session of a sub-project may write.</summary>
public enum ProjectWriteScope
{
    /// <summary>The whole folder of its repository: a change in a monorepo often crosses folders.</summary>
    Repository,

    /// <summary>Only the folder of the sub-project.</summary>
    Folder,
}

/// <summary>A problem of a project map: an entry or a value that was left out, or a file that could not be read.</summary>
/// <param name="Key">The key of the project or of the label at fault; null for the whole file.</param>
/// <param name="Code">What is wrong, such as <c>path.outside</c>.</param>
/// <param name="Message">What is wrong, for the user.</param>
public sealed record ProjectManifestProblem(string? Key, string Code, string Message);

/// <summary>A label of a project map: a name the repository gives to some of its sub-projects.</summary>
/// <param name="Key">Its key in the map, unique in the map.</param>
/// <param name="Name">The name shown.</param>
/// <param name="Parent">The key of the label it is under; null for a top-level label of the map.</param>
/// <param name="Icon">The name of its icon; null for the usual one.</param>
/// <param name="Color">The color of its badge, <c>#rgb</c> or <c>#rrggbb</c>; null for none.</param>
/// <param name="Description">What it stands for; null when nothing is said.</param>
/// <param name="Order">Its place in the map, from 1.</param>
public sealed record ProjectManifestLabel(string Key, string Name, string? Parent, string? Icon, string? Color, string? Description, int Order);

/// <summary>A sub-project a project map declares: a folder of the repository.</summary>
/// <param name="Key">Its key in the map: its identity, kept when its folder moves.</param>
/// <param name="Path">Its folder, relative to the root of the repository, with <c>/</c> between the names: <c>services/billing</c>.</param>
/// <param name="Name">The name shown; null for the name of its folder.</param>
/// <param name="Description">What it is; null when nothing is said.</param>
/// <param name="Labels">The keys of the labels of the map it has, in their order.</param>
/// <param name="Optional">Whether it is listed only for those who add it.</param>
/// <param name="Write">What its sessions may write.</param>
public sealed record ProjectManifestEntry(string Key, string Path, string? Name, string? Description, IReadOnlyList<string> Labels, bool Optional,
    ProjectWriteScope Write);

/// <summary>Where the folder of an entry of a project map is, as the file system says.</summary>
public enum ProjectManifestFolderState
{
    /// <summary>A folder inside the repository.</summary>
    Ok,

    /// <summary>Nothing is there, or not a folder.</summary>
    Missing,

    /// <summary>A link on the way leads out of the repository.</summary>
    Outside,
}

/// <summary>
/// The project map of a repository: the file <c>.alta/projects.toml</c> at its root, committed with it, that names the
/// folders of the repository that are projects of their own and the labels that organize them.
/// </summary>
/// <remarks>
/// The map is data: what is wrong in it costs the entry or the value at fault, said in <see cref="Problems"/>, never an
/// exception. It only arranges folders of its repository: a path is relative, stays inside the root, and names a
/// folder, any folder, a git submodule included.
/// </remarks>
public sealed class ProjectManifest
{
    /// <summary>The name of the file in the <c>.alta</c> folder of the repository.</summary>
    public const string FileName = "projects.toml";

    /// <summary>The most sub-projects a map declares; the entries after are left out.</summary>
    public const int MaximumProjects = 2000;

    /// <summary>The most labels a map declares; the labels after are left out.</summary>
    public const int MaximumLabels = 256;

    /// <summary>The largest map read, in UTF-8 bytes.</summary>
    public const int MaximumBytes = 512 * 1024;

    /// <summary>The longest key of a project or of a label.</summary>
    public const int MaximumKeyLength = 48;

    /// <summary>The longest path of a sub-project.</summary>
    public const int MaximumPathLength = 1024;

    /// <summary>The longest description of a sub-project or of a label.</summary>
    public const int MaximumDescriptionLength = 2000;

    /// <summary>
    /// How many levels of maps are read: the map of a repository is the first, the map of one of its sub-projects the
    /// second, and so on. A map deeper than that is not read, and says so.
    /// </summary>
    public const int MaximumDepth = 4;

    // The folders of settings and of agents that the sessions of a project never write (as AgentWriteGuard keeps them): a
    // sub-project is never one of them, nor inside one, by its path or through a link.
    private static readonly FrozenSet<string> SettingsFolderNames =
        new[] { ".git", ".alta", ".claude", ".codex", ".copilot", ".gemini", ".vscode", ".cursor" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private ProjectManifest(IReadOnlyList<ProjectManifestEntry> projects, IReadOnlyList<ProjectManifestLabel> labels, IReadOnlyList<ProjectManifestProblem> problems)
        => (Projects, Labels, Problems) = (projects, labels, problems);

    /// <summary>Gets a map that declares nothing.</summary>
    public static ProjectManifest Empty { get; } = new([], [], []);

    // A map whose file is larger than a map can be: no entry, and the reason.
    internal static ProjectManifest TooLarge()
        => new([], [], [new(null, "file.tooLarge", $"The project map is larger than {MaximumBytes / 1024} KB: nothing of it is read.")]);

    // A map whose file cannot be read: no entry, and the reason.
    internal static ProjectManifest Unreadable(string message)
        => new([], [], [new(null, "file.unreadable", $"The project map cannot be read: {FirstLine(message)}")]);

    /// <summary>Gets the sub-projects, in the order of the file.</summary>
    public IReadOnlyList<ProjectManifestEntry> Projects { get; }

    /// <summary>Gets the labels, in the order of the file; a parent always comes before its labels.</summary>
    public IReadOnlyList<ProjectManifestLabel> Labels { get; }

    /// <summary>Gets what was left out, and why.</summary>
    public IReadOnlyList<ProjectManifestProblem> Problems { get; }

    /// <summary>Gets the path of the map of a repository.</summary>
    /// <param name="repositoryRoot">The folder of the repository project.</param>
    /// <returns><c>&lt;root&gt;/.alta/projects.toml</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="repositoryRoot"/> is blank.</exception>
    public static string PathOf(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Path.Combine(repositoryRoot, ".alta", FileName);
    }

    /// <summary>Gets an entry by its key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The entry, or null.</returns>
    public ProjectManifestEntry? Find(string? key) => Projects.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));

    /// <summary>Tells whether a text can be the key of a project or of a label: lower-case letters, digits, <c>-</c> and <c>_</c>, starting with a letter or a digit.</summary>
    /// <param name="key">The text.</param>
    /// <returns><see langword="true"/> for 1 to <see cref="MaximumKeyLength"/> such characters.</returns>
    public static bool IsValidKey(string? key)
    {
        if (key is not { Length: >= 1 and <= MaximumKeyLength } || key[0] is '-' or '_') return false;
        foreach (var ch in key)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')) return false;
        }

        return true;
    }

    /// <summary>Works out a key from a path or a name: its last part, lower case, with <c>-</c> for what is neither a letter nor a digit.</summary>
    /// <param name="value">The path or the name.</param>
    /// <returns>A valid key, which may already be taken; <c>project</c> when nothing usable is left.</returns>
    public static string KeyFrom(string? value)
    {
        var text = (value ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var last = text[(text.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var builder = new StringBuilder(last.Length);
        foreach (var ch in last)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var key = builder.ToString().Trim('-', '_');
        if (key.Length > MaximumKeyLength) key = key[..MaximumKeyLength].TrimEnd('-', '_');
        return IsValidKey(key) ? key : "project";
    }

    /// <summary>
    /// Normalizes the path of a sub-project: <c>/</c> between the names, no <c>/</c> at the end. A path that is empty,
    /// absolute, that goes up (<c>..</c>) or into <c>.git</c>, or that is too long is refused.
    /// </summary>
    /// <param name="path">The path as written.</param>
    /// <param name="problem">What is wrong with it, when it is refused.</param>
    /// <returns>The normalized path, or null.</returns>
    public static string? NormalizePath(string? path, out string? problem)
    {
        problem = null;
        var text = path?.Trim().Replace('\\', '/');
        while (text is { Length: > 0 } && text.EndsWith('/')) text = text[..^1];
        if (text is { Length: > 2 } && text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        if (string.IsNullOrEmpty(text) || text == ".")
        {
            problem = "A sub-project is a folder inside the repository, not its root.";
            return null;
        }

        if (text.Length > MaximumPathLength || text.Any(char.IsControl))
        {
            problem = $"The path is longer than {MaximumPathLength} characters or holds a control character.";
            return null;
        }

        if (text.StartsWith('/') || text.Contains(':') || Path.IsPathRooted(text))
        {
            problem = "The path must be relative to the root of the repository.";
            return null;
        }

        foreach (var segment in text.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                problem = "The path must stay inside the repository: no `..`, `.` or empty name.";
                return null;
            }

            // The folders of settings of a project are kept out of reach of its sessions: none is a project of its own.
            if (SettingsFolderNames.Contains(segment))
            {
                problem = $"A sub-project cannot be a folder of settings or be inside one (`{segment}`).";
                return null;
            }
        }

        return text;
    }

    /// <summary>
    /// Finds the folder of a sub-project, following the links on the way: a folder inside the repository, nothing, or a
    /// link that leads out of it. A git submodule is a folder like any other.
    /// </summary>
    /// <param name="repositoryRoot">The folder of the repository project.</param>
    /// <param name="path">The normalized path of the sub-project (<see cref="NormalizePath"/>).</param>
    /// <returns>The full path of the folder, as the repository names it (links not resolved), and its state.</returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    public static (string FullPath, ProjectManifestFolderState State) ResolveFolder(string repositoryRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            var roots = new List<string> { root };
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
                && Directory.ResolveLinkTarget(root, returnFinalTarget: true) is { } rootTarget)
                roots.Add(Path.TrimEndingDirectorySeparator(rootTarget.FullName));
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current)) return (full, ProjectManifestFolderState.Missing);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: true) is not { } target) continue;
                var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
                var inside = roots.FirstOrDefault(value => IsInside(resolved, value));
                if (inside is null) return (full, ProjectManifestFolderState.Outside);
                // A link that stays inside but leads into a folder of settings is no folder for a project either.
                if (Path.GetRelativePath(inside, resolved).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SettingsFolderNames.Contains))
                    return (full, ProjectManifestFolderState.Outside);
                current = resolved;
            }

            return (full, ProjectManifestFolderState.Ok);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (full, ProjectManifestFolderState.Missing);
        }
    }

    // The folder a path is on the disk, every link on the way followed: two paths that lead to the same folder give the
    // same text. A path that cannot be looked at is given as it is.
    internal static string RealFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var current = Path.GetPathRoot(full) ?? string.Empty;
            foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 && Directory.ResolveLinkTarget(current, returnFinalTarget: true) is { } target)
                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
            }

            return current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return full;
        }
    }

    private static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison)
            || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Reads a project map from its text. Never throws for what the text says: what is wrong is in <see cref="Problems"/>.</summary>
    /// <param name="text">The text of the file.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static ProjectManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var problems = new List<ProjectManifestProblem>();
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            return new([], [], [new(null, "file.tooLarge", $"The project map is larger than {MaximumBytes / 1024} KB: nothing of it is read.")]);
        TomlTable table;
        try
        {
            // The strict parser refuses what the model would merge, such as a table written twice.
            if (!string.IsNullOrWhiteSpace(text)) Tomlyn.Parsing.SyntaxParser.ParseStrict(text);
            table = string.IsNullOrWhiteSpace(text) ? new TomlTable() : TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new([], [], [new(null, "file.invalid", $"The project map is not valid TOML: {FirstLine(exception.Message)}")]);
        }

        foreach (var (key, _) in table)
        {
            if (key is not ("labels" or "projects"))
                problems.Add(new(null, "key.unknown", $"`{key}` is not a setting of a project map: it is left out."));
        }

        var labels = ReadLabels(table, problems);
        var projects = ReadProjects(table, labels, problems);
        return new(projects, labels, problems);
    }

    private static List<ProjectManifestLabel> ReadLabels(TomlTable table, List<ProjectManifestProblem> problems)
    {
        var read = new List<ProjectManifestLabel>();
        if (!table.TryGetValue("labels", out var value)) return read;
        if (value is not TomlTable labels)
        {
            problems.Add(new(null, "labels.invalid", "`labels` must be a table of labels, such as `[labels.backend]`."));
            return read;
        }

        foreach (var (key, item) in labels)
        {
            if (read.Count >= MaximumLabels)
            {
                problems.Add(new(key, "labels.tooMany", $"A project map declares {MaximumLabels} labels at most: the others are left out."));
                break;
            }

            if (!IsValidKey(key))
            {
                problems.Add(new(key, "label.key", $"`{key}` is not a key of a label: lower-case letters, digits, `-` and `_`."));
                continue;
            }

            if (item is not TomlTable entry)
            {
                problems.Add(new(key, "label.invalid", $"The label `{key}` must be a table."));
                continue;
            }

            var name = Text(entry, "name", key, problems, LabelDescriptor.MaximumNameLength) ?? key;
            if (!SpaceDescriptor.IsValidName(name))
            {
                problems.Add(new(key, "label.name", $"The name of the label `{key}` is 1 to {LabelDescriptor.MaximumNameLength} characters on one line: its key is shown."));
                name = key;
            }

            var icon = Text(entry, "icon", key, problems, 64)?.ToLowerInvariant();
            if (icon is not null && !SpaceDescriptor.IsValidIcon(icon))
            {
                problems.Add(new(key, "label.icon", $"`{icon}` is not the name of an icon."));
                icon = null;
            }

            var color = Text(entry, "color", key, problems, 7);
            if (color is not null && !SpaceDescriptor.IsValidColor(color))
            {
                problems.Add(new(key, "label.color", $"`{color}` is not a color written #rgb or #rrggbb."));
                color = null;
            }

            var description = Text(entry, "description", key, problems, MaximumDescriptionLength);
            var parent = Text(entry, "parent", key, problems, MaximumKeyLength);
            foreach (var (setting, _) in entry)
            {
                if (setting is not ("name" or "icon" or "color" or "description" or "parent"))
                    problems.Add(new(key, "key.unknown", $"`{setting}` is not a setting of a label: it is left out."));
            }

            read.Add(new(key, name, parent, icon, color, description, read.Count + 1));
        }

        // A parent that is not a label of the map, that makes a loop or that puts a label too deep is read as none.
        var byKey = read.ToDictionary(static label => label.Key, StringComparer.Ordinal);
        for (var index = 0; index < read.Count; index++)
        {
            var label = read[index];
            if (label.Parent is null) continue;
            var depth = 1;
            var ok = byKey.ContainsKey(label.Parent);
            var seen = new HashSet<string>(StringComparer.Ordinal) { label.Key };
            for (var current = label.Parent; ok && current is not null; current = byKey.TryGetValue(current, out var above) ? above.Parent : null)
            {
                if (!seen.Add(current) || ++depth > LabelDescriptor.MaximumDepth) ok = false;
            }

            if (ok) continue;
            problems.Add(new(label.Key, "label.parent", byKey.ContainsKey(label.Parent)
                ? $"The label `{label.Key}` cannot be under `{label.Parent}`: labels go {LabelDescriptor.MaximumDepth} levels deep at most, without a loop."
                : $"The label `{label.Key}` is under `{label.Parent}`, which is no label of the map."));
            read[index] = byKey[label.Key] = label with { Parent = null };
        }

        // A parent before its labels: the order of a tree.
        var ordered = new List<ProjectManifestLabel>(read.Count);
        void Add(string? parent)
        {
            foreach (var label in read)
            {
                if (label.Parent != parent) continue;
                ordered.Add(label);
                Add(label.Key);
            }
        }

        Add(null);
        return ordered;
    }

    private static List<ProjectManifestEntry> ReadProjects(TomlTable table, IReadOnlyList<ProjectManifestLabel> labels, List<ProjectManifestProblem> problems)
    {
        var read = new List<ProjectManifestEntry>();
        if (!table.TryGetValue("projects", out var value)) return read;
        if (value is not TomlTable projects)
        {
            problems.Add(new(null, "projects.invalid", "`projects` must be a table of projects, such as `[projects.billing]`."));
            return read;
        }

        var known = labels.Select(static label => label.Key).ToHashSet(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, item) in projects)
        {
            if (read.Count >= MaximumProjects)
            {
                problems.Add(new(key, "projects.tooMany", $"A project map declares {MaximumProjects} projects at most: the others are left out."));
                break;
            }

            if (!IsValidKey(key))
            {
                problems.Add(new(key, "project.key", $"`{key}` is not a key of a project: lower-case letters, digits, `-` and `_`."));
                continue;
            }

            if (item is not TomlTable entry)
            {
                problems.Add(new(key, "project.invalid", $"The project `{key}` must be a table with its `path`."));
                continue;
            }

            var path = NormalizePath(Text(entry, "path", key, problems, MaximumPathLength), out var pathProblem);
            if (path is null)
            {
                problems.Add(new(key, "project.path", $"The project `{key}` is left out: {pathProblem}"));
                continue;
            }

            if (paths.TryGetValue(path, out var other))
            {
                problems.Add(new(key, "project.duplicate", $"The project `{key}` is left out: `{other}` already names the folder `{path}`."));
                continue;
            }

            var name = Text(entry, "name", key, problems, 256);
            if (name is not null && (name != name.Trim() || name.Any(char.IsControl)))
            {
                problems.Add(new(key, "project.name", $"The name of the project `{key}` is one line without spaces around it: the name of its folder is shown."));
                name = null;
            }

            var description = Text(entry, "description", key, problems, MaximumDescriptionLength);
            var own = new List<string>();
            if (entry.TryGetValue("labels", out var labelValue))
            {
                if (labelValue is TomlArray array)
                {
                    foreach (var labelKey in array)
                    {
                        if (labelKey is string text && known.Contains(text))
                        {
                            if (!own.Contains(text, StringComparer.Ordinal)) own.Add(text);
                        }
                        else problems.Add(new(key, "project.label", $"The project `{key}` names `{labelKey}`, which is no label of the map."));
                    }
                }
                else problems.Add(new(key, "project.labels", $"`labels` of the project `{key}` must be a list of keys of labels."));
            }

            var optional = false;
            if (entry.TryGetValue("optional", out var optionalValue))
            {
                if (optionalValue is bool flag) optional = flag;
                else problems.Add(new(key, "project.optional", $"`optional` of the project `{key}` must be true or false."));
            }

            var write = ProjectWriteScope.Repository;
            if (Text(entry, "write", key, problems, 32) is { } scope)
            {
                if (scope == "folder") write = ProjectWriteScope.Folder;
                else if (scope != "repository") problems.Add(new(key, "project.write", $"`write` of the project `{key}` is `repository` or `folder`."));
            }

            foreach (var (setting, _) in entry)
            {
                if (setting is not ("path" or "name" or "description" or "labels" or "optional" or "write"))
                    problems.Add(new(key, "key.unknown", $"`{setting}` is not a setting of a project: it is left out."));
            }

            paths[path] = key;
            read.Add(new(key, path, name, description, own, optional, write));
        }

        return read;
    }

    // A text setting; null when absent. A value of another kind, or one that is too long, is a problem and read as absent.
    private static string? Text(TomlTable table, string name, string key, List<ProjectManifestProblem> problems, int maximum)
    {
        if (!table.TryGetValue(name, out var value)) return null;
        if (value is not string text)
        {
            problems.Add(new(key, "value.type", $"`{name}` of `{key}` must be a text."));
            return null;
        }

        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length <= maximum) return text;
        problems.Add(new(key, "value.long", $"`{name}` of `{key}` is longer than {maximum} characters."));
        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
