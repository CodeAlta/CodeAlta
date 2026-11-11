using System.Text;

namespace CodeAlta.Catalog;

/// <summary>What to change of an entry of a project map: a value left <see langword="null"/> stays as it is.</summary>
/// <param name="Path">The folder, relative to the root of the repository.</param>
/// <param name="Name">The name shown; empty removes it, for the name of its folder.</param>
/// <param name="Description">What the project is; empty removes it.</param>
/// <param name="Labels">The keys of its labels, which replace the ones it has; a key no label has creates the label.</param>
/// <param name="Optional">Whether it is listed only for those who add it.</param>
/// <param name="Write">What its sessions may write.</param>
public sealed record ProjectManifestEdit(string? Path = null, string? Name = null, string? Description = null, IReadOnlyList<string>? Labels = null,
    bool? Optional = null, ProjectWriteScope? Write = null);

/// <summary>What to change of a label of a project map: a value left <see langword="null"/> stays as it is.</summary>
/// <param name="Name">The name; a new label without one is named from its key.</param>
/// <param name="Parent">The key of the label it goes under; empty for the top level.</param>
/// <param name="Icon">The name of its icon; empty removes it.</param>
/// <param name="Color">The color of its badge, <c>#rgb</c> or <c>#rrggbb</c>; empty removes it.</param>
/// <param name="Description">What it stands for; empty removes it.</param>
public sealed record ProjectManifestLabelEdit(string? Name = null, string? Parent = null, string? Icon = null, string? Color = null, string? Description = null);

/// <summary>How a change of a project map ended.</summary>
public enum ProjectManifestChangeStatus
{
    /// <summary>The map was written.</summary>
    Updated,
    /// <summary>The map already said so; nothing was written.</summary>
    Unchanged,
    /// <summary>No entry or label has the key given.</summary>
    NotFound,
    /// <summary>A value given is not valid, or the change would add a problem to the map; nothing was written.</summary>
    Invalid,
    /// <summary>The map is written in a form that is not edited here, or is not valid TOML; nothing was written.</summary>
    Unsupported,
}

/// <summary>The result of a change of a project map.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
/// <param name="Key">The key of the entry or the label changed.</param>
/// <param name="CreatedLabels">The keys of the labels the change created.</param>
public sealed record ProjectManifestChange(ProjectManifestChangeStatus Status, string? Message = null, string? Key = null, IReadOnlyList<string>? CreatedLabels = null)
{
    /// <summary>Gets whether the map says what was asked.</summary>
    public bool Succeeded => Status is ProjectManifestChangeStatus.Updated or ProjectManifestChangeStatus.Unchanged;
}

/// <summary>
/// Edits the project map of a repository (<c>.alta/projects.toml</c>), one table at a time, keeping every other byte of
/// the file: its comments, its order, its blank lines. A change that would add a problem to the map is refused.
/// </summary>
/// <remarks>
/// Each change reads, edits and writes the file under the lock the configuration files share (<c>ConfigFileLock</c>), and
/// publishes it whole, so that a reader finds the old map or the new one. The map is a file of the repository: the user
/// reviews the change and commits it.
/// </remarks>
public static class ProjectManifestEditor
{
    private const string Header = "# The projects of this repository for CodeAlta: each [projects.<key>] is a folder that is a project of its own.\n# Edit it with `alta project manifest`, or in CodeAlta Desktop with Sub-projects… in the menu of the repository.\n";

    /// <summary>Declares a folder of the repository as a sub-project.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="key">Its key; null to work it out from its folder.</param>
    /// <param name="edit">Its path, required, and its other values.</param>
    /// <returns>How it ended, with the key of the entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static ProjectManifestChange AddProject(string repositoryPath, string? key, ProjectManifestEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(edit);
        return Edit(repositoryPath, (text, map) =>
        {
            if (ProjectManifest.NormalizePath(edit.Path, out var problem) is not { } path) return Fail(ProjectManifestChangeStatus.Invalid, problem!);
            var (_, state) = ProjectManifest.ResolveFolder(repositoryPath, path);
            if (state != ProjectManifestFolderState.Ok)
                return Fail(ProjectManifestChangeStatus.Invalid, state == ProjectManifestFolderState.Outside
                    ? $"`{path}` is a link that leads out of the repository."
                    : $"`{path}` is not a folder of the repository.");
            if (map.Projects.FirstOrDefault(entry => SamePath(entry.Path, path)) is { } declared)
                return Fail(ProjectManifestChangeStatus.Invalid, $"`{path}` is already declared, as `{declared.Key}`.");
            if (key is null)
            {
                var baseKey = ProjectManifest.KeyFrom(path);
                key = baseKey;
                for (var suffix = 2; map.Find(key) is not null; suffix++) key = $"{baseKey}-{suffix}";
            }
            else if (!ProjectManifest.IsValidKey(key)) return Fail(ProjectManifestChangeStatus.Invalid, $"`{key}` is not a key: lower-case letters, digits, `-` and `_`.");
            else if (map.Find(key) is not null) return Fail(ProjectManifestChangeStatus.Invalid, $"The key `{key}` is already a project of the map.");
            if (map.Projects.Count >= ProjectManifest.MaximumProjects)
                return Fail(ProjectManifestChangeStatus.Invalid, $"A project map declares {ProjectManifest.MaximumProjects} projects at most.");
            var table = new[] { "projects", key };
            text = ConfigTableSyntax.SetText(text, table, "path", path);
            return ApplyProject(text, map, key, edit with { Path = null });
        });
    }

    /// <summary>Changes an entry of the map.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="key">The key of the entry.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static ProjectManifestChange SetProject(string repositoryPath, string key, ProjectManifestEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(edit);
        return Edit(repositoryPath, (text, map) =>
        {
            if (map.Find(key) is null) return Fail(ProjectManifestChangeStatus.NotFound, $"No project of the map has the key `{key}`.");
            if (edit.Path is not null)
            {
                if (ProjectManifest.NormalizePath(edit.Path, out var problem) is not { } path) return Fail(ProjectManifestChangeStatus.Invalid, problem!);
                if (ProjectManifest.ResolveFolder(repositoryPath, path).State != ProjectManifestFolderState.Ok)
                    return Fail(ProjectManifestChangeStatus.Invalid, $"`{path}` is not a folder of the repository.");
                if (map.Projects.FirstOrDefault(entry => entry.Key != key && SamePath(entry.Path, path)) is { } other)
                    return Fail(ProjectManifestChangeStatus.Invalid, $"`{path}` is already declared, as `{other.Key}`.");
                text = ConfigTableSyntax.SetText(text, ["projects", key], "path", path);
            }

            return ApplyProject(text, map, key, edit with { Path = null });
        });
    }

    /// <summary>Removes an entry of the map. Its folder and its sessions stay.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="key">The key of the entry.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static ProjectManifestChange RemoveProject(string repositoryPath, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return Edit(repositoryPath, (text, map) => map.Find(key) is null
            ? Fail(ProjectManifestChangeStatus.NotFound, $"No project of the map has the key `{key}`.")
            : (RemoveTable(text, ["projects", key]), new ProjectManifestChange(ProjectManifestChangeStatus.Updated, Key: key)));
    }

    /// <summary>Creates or changes a label of the map.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="key">The key of the label.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is null.</exception>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static ProjectManifestChange SetLabel(string repositoryPath, string key, ProjectManifestLabelEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(edit);
        return Edit(repositoryPath, (text, map) =>
        {
            if (!ProjectManifest.IsValidKey(key)) return Fail(ProjectManifestChangeStatus.Invalid, $"`{key}` is not a key: lower-case letters, digits, `-` and `_`.");
            var existing = map.Labels.FirstOrDefault(label => label.Key == key);
            var created = new List<string>();
            if (existing is null)
            {
                if (map.Labels.Count >= ProjectManifest.MaximumLabels)
                    return Fail(ProjectManifestChangeStatus.Invalid, $"A project map declares {ProjectManifest.MaximumLabels} labels at most.");
                text = AddLabelTable(text, key, string.IsNullOrWhiteSpace(edit.Name) ? NameOf(key) : edit.Name.Trim());
                created.Add(key);
            }
            else if (edit.Name is not null) text = ConfigTableSyntax.SetText(text, ["labels", key], "name", Required(edit.Name, key));

            var parent = edit.Parent?.Trim();
            if (parent is { Length: > 0 })
            {
                if (map.Labels.All(label => label.Key != parent)) return Fail(ProjectManifestChangeStatus.NotFound, $"No label of the map has the key `{parent}`.");
                if (parent == key || Below(map, key).Contains(parent))
                    return Fail(ProjectManifestChangeStatus.Invalid, "A label cannot go under itself or under one of its labels.");
            }

            if (parent is not null) text = ConfigTableSyntax.SetText(text, ["labels", key], "parent", parent.Length == 0 ? null : parent);
            if (edit.Icon is not null) text = ConfigTableSyntax.SetText(text, ["labels", key], "icon", Optional(edit.Icon)?.ToLowerInvariant());
            if (edit.Color is not null) text = ConfigTableSyntax.SetText(text, ["labels", key], "color", Optional(edit.Color));
            if (edit.Description is not null) text = ConfigTableSyntax.SetText(text, ["labels", key], "description", Optional(edit.Description));
            return (text, new ProjectManifestChange(ProjectManifestChangeStatus.Updated, Key: key, CreatedLabels: created));
        });
    }

    /// <summary>Removes a label of the map: its projects lose it, and the labels under it take its place.</summary>
    /// <param name="repositoryPath">The folder of the repository project.</param>
    /// <param name="key">The key of the label.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static ProjectManifestChange RemoveLabel(string repositoryPath, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return Edit(repositoryPath, (text, map) =>
        {
            if (map.Labels.FirstOrDefault(label => label.Key == key) is not { } removed)
                return Fail(ProjectManifestChangeStatus.NotFound, $"No label of the map has the key `{key}`.");
            foreach (var child in map.Labels.Where(label => label.Parent == key))
                text = ConfigTableSyntax.SetText(text, ["labels", child.Key], "parent", removed.Parent);
            foreach (var entry in map.Projects.Where(entry => entry.Labels.Contains(key, StringComparer.Ordinal)))
                text = ConfigTableSyntax.SetList(text, ["projects", entry.Key], "labels", entry.Labels.Count == 1 ? null : [.. entry.Labels.Where(label => label != key)]);
            return (RemoveTable(text, ["labels", key]), new ProjectManifestChange(ProjectManifestChangeStatus.Updated, Key: key));
        });
    }

    // The values of an entry, after its path: the labels a key names and the map does not declare are created.
    private static (string? Text, ProjectManifestChange Change) ApplyProject(string text, ProjectManifest map, string key, ProjectManifestEdit edit)
    {
        var table = new[] { "projects", key };
        var created = new List<string>();
        if (edit.Name is not null) text = ConfigTableSyntax.SetText(text, table, "name", Optional(edit.Name));
        if (edit.Description is not null) text = ConfigTableSyntax.SetText(text, table, "description", Optional(edit.Description));
        if (edit.Labels is not null)
        {
            var labels = new List<string>();
            foreach (var value in edit.Labels.Select(static label => label.Trim()).Where(static label => label.Length > 0))
            {
                if (!ProjectManifest.IsValidKey(value)) return Fail(ProjectManifestChangeStatus.Invalid, $"`{value}` is not a key of a label: lower-case letters, digits, `-` and `_`.");
                if (labels.Contains(value, StringComparer.Ordinal)) continue;
                labels.Add(value);
                if (map.Labels.Any(label => label.Key == value) || created.Contains(value, StringComparer.Ordinal)) continue;
                text = AddLabelTable(text, value, NameOf(value));
                created.Add(value);
            }

            text = ConfigTableSyntax.SetList(text, table, "labels", labels.Count == 0 ? null : labels);
        }

        if (edit.Optional is { } optional) text = optional ? ConfigTableSyntax.SetValue(text, table, "optional", true) : ConfigTableSyntax.SetText(text, table, "optional", null);
        if (edit.Write is { } write) text = ConfigTableSyntax.SetText(text, table, "write", write == ProjectWriteScope.Folder ? "folder" : null);
        return (text, new ProjectManifestChange(ProjectManifestChangeStatus.Updated, Key: key, CreatedLabels: created));
    }

    // Reads the map, edits its text and writes it, under the lock of the file. The edit gives null text for a refusal.
    private static ProjectManifestChange Edit(string repositoryPath, Func<string, ProjectManifest, (string? Text, ProjectManifestChange Change)> edit)
    {
        var path = Path.GetFullPath(ProjectManifest.PathOf(repositoryPath));
        using var writing = ConfigFileLock.Acquire(path);
        var content = SharedTextFile.TryReadAllText(path) ?? string.Empty;
        var before = ProjectManifest.Parse(content);
        if (before.Problems.FirstOrDefault(static problem => problem.Key is null && problem.Code.StartsWith("file.", StringComparison.Ordinal)) is { } unreadable)
            return new(ProjectManifestChangeStatus.Unsupported, unreadable.Message);
        string? updated;
        ProjectManifestChange change;
        try
        {
            (updated, change) = edit(content.Length == 0 ? Header : content, before);
        }
        catch (InvalidDataException exception)
        {
            return new(ProjectManifestChangeStatus.Unsupported, exception.Message);
        }

        if (updated is null) return change;
        if (string.Equals(updated, content, StringComparison.Ordinal) || content.Length == 0 && updated == Header)
            return change with { Status = ProjectManifestChangeStatus.Unchanged };
        // The edited map must read as asked: a change never adds a problem.
        var after = ProjectManifest.Parse(updated);
        var known = before.Problems.Select(static problem => (problem.Key, problem.Code)).ToHashSet();
        if (after.Problems.FirstOrDefault(problem => !known.Contains((problem.Key, problem.Code))) is { } added)
            return new(ProjectManifestChangeStatus.Invalid, added.Message, change.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SharedTextFile.WriteAllText(path, updated);
        return change;
    }

    // A new label goes before the projects, after the other labels: the map reads from its labels to its projects.
    private static string AddLabelTable(string text, string key, string name)
    {
        var start = ConfigTableSyntax.TableStart(text, ["projects"]);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var table = new StringBuilder().Append("[labels.").Append(ConfigTableSyntax.KeyText(key)).Append(']').Append(newline)
            .Append("name = ").Append(new Tomlyn.Syntax.StringValueSyntax(name)).Append(newline).Append(newline).ToString();
        if (start is not { } offset) return ConfigTableSyntax.SetText(text, ["labels", key], "name", name);
        // The comment lines right above the first project are about it: the label goes above them.
        while (offset > 0 && LineStart(text, offset - 1) is var previous && text.AsSpan(previous, offset - previous).TrimStart().StartsWith("#"))
            offset = previous;
        return text.Insert(offset, table);
    }

    private static int LineStart(string text, int offset)
    {
        var newline = offset == 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        return newline < 0 ? 0 : newline + 1;
    }

    private static string RemoveTable(string text, string[] table)
    {
        var removed = ConfigTableSyntax.RemoveTable(text, table);
        if (removed == text) throw new InvalidDataException($"[{string.Join('.', table)}] is written in a form CodeAlta does not edit: change it by hand.");
        return removed;
    }

    // The keys of the labels under a label, at any depth.
    private static HashSet<string> Below(ProjectManifest map, string key)
    {
        var below = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([key]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in map.Labels.Where(label => label.Parent == current))
                if (below.Add(child.Key)) queue.Enqueue(child.Key);
        }

        return below;
    }

    // `dev-tools` is named "Dev tools".
    private static string NameOf(string key)
    {
        var words = key.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Required(string name, string key) => string.IsNullOrWhiteSpace(name) ? NameOf(key) : name.Trim();

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SamePath(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static (string? Text, ProjectManifestChange Change) Fail(ProjectManifestChangeStatus status, string message) => (null, new(status, message));
}
