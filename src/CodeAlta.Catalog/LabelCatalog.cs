using System.Text;
using SharpYaml;

namespace CodeAlta.Catalog;

/// <summary>How a change of the labels ended.</summary>
public enum LabelChangeStatus
{
    /// <summary>The change was made, or there was nothing to change.</summary>
    Ok,
    /// <summary>No label, no space or no project has the identifier given.</summary>
    NotFound,
    /// <summary>A value given is not valid, or the change would break the tree of the labels; nothing was written.</summary>
    Invalid,
    /// <summary>The change is not one that can be made here, such as in a read-only process; nothing was written.</summary>
    Refused,
    /// <summary>A file kept changing under the edit, or cannot be edited as it is written.</summary>
    Conflict,
}

/// <summary>The result of a change of the labels.</summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Label">The label after the change, when there is one.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
public sealed record LabelChange(LabelChangeStatus Status, LabelDescriptor? Label = null, string? Message = null)
{
    /// <summary>Gets whether the change was made.</summary>
    public bool Succeeded => Status == LabelChangeStatus.Ok;
}

/// <summary>What to change of a label: a value left <see langword="null"/> stays as it is.</summary>
/// <param name="Name">The new name.</param>
/// <param name="Description">The new description; empty removes it.</param>
/// <param name="Icon">The new icon; empty goes back to the usual one.</param>
/// <param name="Color">The new color; empty removes it.</param>
/// <param name="Favorite">Whether the label is a favorite.</param>
/// <param name="Space">The new space of a top-level label; empty for every space.</param>
public sealed record LabelEdit(string? Name = null, string? Description = null, string? Icon = null, string? Color = null,
    bool? Favorite = null, string? Space = null);

/// <summary>A label with the projects that have it.</summary>
/// <param name="Label">The label.</param>
/// <param name="ProjectIds">The identifiers of the projects whose file names the label, in the order of the project catalog.</param>
public sealed record LabelMembers(LabelDescriptor Label, IReadOnlyList<string> ProjectIds);

/// <summary>
/// Loads and persists the labels of the global catalog: one Markdown file for each under
/// <see cref="CatalogOptions.LabelsRoot"/>, and in each project file the labels of the project.
/// </summary>
/// <remarks>
/// The labels are listed in the order of their tree: a label, then its sub-labels, each level by its order. A file that
/// cannot be read is left out; a parent that does not exist, or that would make a loop or a tree too deep, is read as
/// none. The changes of one instance run one after the other; two processes on the same catalog see each other's
/// changes at their next read.
/// </remarks>
public sealed class LabelCatalog
{
    /// <summary>The most labels a catalog holds.</summary>
    public const int MaximumLabels = 256;

    /// <summary>What separates the names of a label and of the labels above it, in its path.</summary>
    public const string PathSeparator = " › ";

    private const string ReadOnlyMessage = "The labels are read-only in this process: no file of the global profile is written. Change them in CodeAlta Desktop.";

    private readonly SpaceCatalog _spaces;
    private readonly CatalogOptions _options;
    private readonly CatalogYamlSerializer _serializer;
    private readonly TextFileCodec _files = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="LabelCatalog"/> class.</summary>
    /// <param name="spaces">The spaces a label can belong to, with the project catalog whose files name the labels.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spaces"/> is <see langword="null"/>.</exception>
    public LabelCatalog(SpaceCatalog spaces, CatalogYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(spaces);
        _spaces = spaces;
        _options = spaces.Projects.Options;
        _serializer = serializer ?? new CatalogYamlSerializer();
    }

    /// <summary>Gets the project catalog whose files name the labels.</summary>
    public ProjectCatalog Projects => _spaces.Projects;

    /// <summary>
    /// Gets whether the labels are read-only, as the project catalog is (<see cref="ProjectCatalog.IsReadOnly"/>). The
    /// changes then write nothing and end <see cref="LabelChangeStatus.Refused"/>.
    /// </summary>
    public bool IsReadOnly => _spaces.IsReadOnly;

    private static LabelChange ReadOnlyChange() => new(LabelChangeStatus.Refused, Message: ReadOnlyMessage);

    /// <summary>Loads the labels, in the order of their tree.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The labels; each one owned by the caller.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<IReadOnlyList<LabelDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var labels = new List<LabelDescriptor>();
        foreach (var path in LabelFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (labels.Count >= MaximumLabels) break;
            if (await ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } label) labels.Add(label);
        }

        return Arranged(labels);
    }

    /// <summary>Gets one label.</summary>
    /// <param name="id">The identifier of the label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The label, or <see langword="null"/> when none has this identifier.</returns>
    public async Task<LabelDescriptor?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!LabelDescriptor.IsValidId(id)) return null;
        return (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(label => label.Id == id);
    }

    /// <summary>Loads the labels with the projects whose file names each.</summary>
    /// <param name="includeArchived">Whether archived projects are listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The labels in the order of their tree.</returns>
    public async Task<IReadOnlyList<LabelMembers>> LoadMembersAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        var labels = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var projects = (await Projects.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(project => includeArchived || !project.Archived).ToArray();
        return labels.Select(label => new LabelMembers(label, projects
            .Where(project => project.Labels.Contains(label.Id, StringComparer.Ordinal))
            .Select(static project => project.Id).ToArray())).ToArray();
    }

    /// <summary>
    /// Creates a label, last among the labels of its parent. Its identifier is worked out from its name, with a number
    /// after it when the identifier is taken, and never changes afterwards.
    /// </summary>
    /// <param name="name">The name of the label.</param>
    /// <param name="parent">The identifier of the label it goes under; <see langword="null"/> for a top-level label.</param>
    /// <param name="icon">The name of its icon.</param>
    /// <param name="color">The color of its badge, <c>#rgb</c> or <c>#rrggbb</c>.</param>
    /// <param name="space">The space of a top-level label; <see langword="null"/> for every space. A sub-label takes the space of its parent.</param>
    /// <param name="favorite">Whether the label is a favorite.</param>
    /// <param name="description">What the label stands for.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>The label created, or why none was.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task<LabelChange> CreateAsync(string name, string? parent = null, string? icon = null, string? color = null, string? space = null,
        bool favorite = false, string? description = null, CancellationToken cancellationToken = default)
    {
        if (IsReadOnly) return ReadOnlyChange();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var labels = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (labels.Count >= MaximumLabels) return new(LabelChangeStatus.Refused, Message: $"There are already {MaximumLabels} labels.");
            parent = Optional(parent);
            LabelDescriptor? above = null;
            if (parent is not null)
            {
                above = labels.FirstOrDefault(label => label.Id == parent);
                if (above is null) return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{parent}'.");
                if (Depth(above, labels) >= LabelDescriptor.MaximumDepth)
                    return new(LabelChangeStatus.Invalid, Message: $"Labels go {LabelDescriptor.MaximumDepth} levels deep at most.");
            }

            var label = new LabelDescriptor
            {
                Name = name?.Trim() ?? string.Empty,
                Description = Optional(description),
                Icon = Optional(icon)?.ToLowerInvariant(),
                Color = Optional(color),
                Parent = parent,
                Favorite = favorite,
                Order = labels.Where(value => value.Parent == parent).Select(static value => value.Order).DefaultIfEmpty(0).Max() + 1,
            };
            if (above is not null) label.Space = above.Space;
            else if (Optional(space) is { } own)
            {
                if (await SpaceProblemAsync(own, cancellationToken).ConfigureAwait(false) is { } problem) return problem;
                label.Space = own;
            }

            if (Invalid(label, labels) is { } invalid) return new(LabelChangeStatus.Invalid, Message: invalid);
            var baseId = LabelDescriptor.IdFromName(label.Name);
            for (var suffix = 1; suffix < 1000; suffix++)
            {
                label.Id = suffix == 1 ? baseId : $"{(baseId.Length > 60 ? baseId[..60] : baseId)}-{suffix}";
                if (labels.Any(value => value.Id == label.Id)) continue;
                var path = PathOf(label.Id);
                Directory.CreateDirectory(_options.LabelsRoot);
                if (!await _files.TryCreateAsync(path, _serializer.SerializeLabelMarkdown(label), cancellationToken).ConfigureAwait(false)) continue;
                label.SourcePath = path;
                return new(LabelChangeStatus.Ok, label);
            }

            return new(LabelChangeStatus.Conflict, Message: "No free identifier was found for this name.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes the name, the description, the icon, the color, the favorite or the space of a label. Its identifier stays.</summary>
    /// <param name="id">The identifier of the label.</param>
    /// <param name="edit">What to change. A space is refused for a sub-label, which is in the space of its parent.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>The label after the change, or why it was not made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task<LabelChange> UpdateAsync(string id, LabelEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (IsReadOnly) return ReadOnlyChange();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var labels = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var label = labels.FirstOrDefault(value => value.Id == id);
            if (label is null) return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{id}'.");
            if (edit.Name is not null) label.Name = edit.Name.Trim();
            if (edit.Description is not null) label.Description = Optional(edit.Description);
            if (edit.Icon is not null) label.Icon = Optional(edit.Icon)?.ToLowerInvariant();
            if (edit.Color is not null) label.Color = Optional(edit.Color);
            if (edit.Favorite is { } favorite) label.Favorite = favorite;
            if (edit.Space is not null && Optional(edit.Space) != label.Space)
            {
                if (label.Parent is not null) return new(LabelChangeStatus.Invalid, Message: "A sub-label is in the space of the label it is under.");
                if (Optional(edit.Space) is { } space && await SpaceProblemAsync(space, cancellationToken).ConfigureAwait(false) is { } problem) return problem;
                label.Space = Optional(edit.Space);
            }

            if (Invalid(label, labels) is { } invalid) return new(LabelChangeStatus.Invalid, Message: invalid);
            return await WriteAsync(label, cancellationToken).ConfigureAwait(false)
                ? new(LabelChangeStatus.Ok, label)
                : new(LabelChangeStatus.Conflict, Message: "The file of the label changed meanwhile.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves a label, with its sub-labels, under another label or to the top level, at a place among its new siblings.
    /// A top-level label keeps the space it had; a sub-label takes the space of its new parent.
    /// </summary>
    /// <param name="id">The identifier of the label.</param>
    /// <param name="parent">The identifier of its new parent; <see langword="null"/> for the top level.</param>
    /// <param name="index">Its place among its new siblings, from 0; a negative or too large one puts it last.</param>
    /// <param name="cancellationToken">Cancels before a file is written.</param>
    /// <returns>The label after the move, or why it was not made.</returns>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task<LabelChange> MoveAsync(string id, string? parent, int index, CancellationToken cancellationToken = default)
    {
        if (IsReadOnly) return ReadOnlyChange();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var labels = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var label = labels.FirstOrDefault(value => value.Id == id);
            if (label is null) return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{id}'.");
            parent = Optional(parent);
            LabelDescriptor? above = null;
            if (parent is not null)
            {
                above = labels.FirstOrDefault(value => value.Id == parent);
                if (above is null) return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{parent}'.");
                if (Subtree(label.Id, labels).Contains(parent, StringComparer.Ordinal))
                    return new(LabelChangeStatus.Invalid, Message: "A label cannot go under itself or under one of its sub-labels.");
                if (Depth(above, labels) + Height(label, labels) > LabelDescriptor.MaximumDepth)
                    return new(LabelChangeStatus.Invalid, Message: $"Labels go {LabelDescriptor.MaximumDepth} levels deep at most.");
            }

            var moved = label.Parent != parent;
            label.Parent = parent;
            if (above is not null) label.Space = above.Space;
            if (moved && Invalid(label, labels) is { } invalid) return new(LabelChangeStatus.Invalid, Message: invalid);
            var siblings = labels.Where(value => value.Parent == parent && !ReferenceEquals(value, label)).ToList();
            siblings.Insert(index < 0 || index > siblings.Count ? siblings.Count : index, label);
            var complete = true;
            for (var place = 0; place < siblings.Count; place++)
            {
                if (siblings[place].Order == place + 1 && !(moved && ReferenceEquals(siblings[place], label))) continue;
                siblings[place].Order = place + 1;
                complete &= await WriteAsync(siblings[place], cancellationToken).ConfigureAwait(false);
            }

            return complete ? new(LabelChangeStatus.Ok, label) : new(LabelChangeStatus.Conflict, Message: "A label file changed meanwhile.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes a label: its file, then its name in the file of each of its projects. Its sub-labels take its place
    /// under its parent. The projects stay.
    /// </summary>
    /// <param name="id">The identifier of the label.</param>
    /// <param name="cancellationToken">Cancels before the file is deleted.</param>
    /// <returns>The label deleted, or why none was.</returns>
    /// <exception cref="IOException">A file could not be changed.</exception>
    public async Task<LabelChange> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (IsReadOnly) return ReadOnlyChange();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var labels = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var label = labels.FirstOrDefault(value => value.Id == id);
            if (label?.SourcePath is null) return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{id}'.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(label.SourcePath);
            // From here the label is gone whatever happens: its sub-labels move up, and a project file that still
            // names it means nothing.
            var siblings = labels.Where(value => value.Parent == label.Parent).ToList();
            var place = siblings.IndexOf(label);
            var children = labels.Where(value => value.Parent == label.Id).ToList();
            foreach (var child in children)
            {
                child.Parent = label.Parent;
                child.Space = label.Space;
            }

            siblings.RemoveAt(place);
            siblings.InsertRange(place, children);
            for (var index = 0; index < siblings.Count; index++)
            {
                if (siblings[index].Order == index + 1 && !children.Contains(siblings[index])) continue;
                siblings[index].Order = index + 1;
                await WriteAsync(siblings[index], CancellationToken.None).ConfigureAwait(false);
            }

            await ForgetAsync([label.Id]).ConfigureAwait(false);
            label.SourcePath = null;
            return new(LabelChangeStatus.Ok, label);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Changes the labels of one project in one edit of its file: it gets some and loses others.
    /// </summary>
    /// <param name="projectId">The identifier of the project.</param>
    /// <param name="add">The labels the project gets, after the ones it has.</param>
    /// <param name="remove">The labels the project loses.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="IOException">The project file could not be changed.</exception>
    public async Task<LabelChange> AssignAsync(string projectId, IReadOnlyList<string>? add, IReadOnlyList<string>? remove,
        CancellationToken cancellationToken = default)
    {
        add ??= [];
        remove ??= [];
        if (string.IsNullOrWhiteSpace(projectId)) return new(LabelChangeStatus.NotFound, Message: "A project is required.");
        if (IsReadOnly) return ReadOnlyChange();
        var known = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Select(static label => label.Id).ToHashSet(StringComparer.Ordinal);
        if (add.FirstOrDefault(id => !known.Contains(id)) is { } missing)
            return new(LabelChangeStatus.NotFound, Message: $"No label has the id '{missing}'.");
        var status = await Projects.UpdateLabelsAsync(projectId,
            current => current.Where(id => !remove.Contains(id, StringComparer.Ordinal)).Concat(add), cancellationToken).ConfigureAwait(false);
        return status switch
        {
            ProjectSpacesStatus.Updated or ProjectSpacesStatus.Unchanged => new(LabelChangeStatus.Ok),
            ProjectSpacesStatus.NotFound => new(LabelChangeStatus.NotFound, Message: $"No project has the id '{projectId}'."),
            ProjectSpacesStatus.Conflict => new(LabelChangeStatus.Conflict, Message: "The project file changed meanwhile."),
            _ => new(LabelChangeStatus.Conflict, Message: "The project file cannot be edited as it is written."),
        };
    }

    /// <summary>
    /// Deletes the labels of a space, with their sub-labels, and their names in the project files: a space that is
    /// deleted takes its labels with it.
    /// </summary>
    /// <param name="spaceId">The identifier of the space.</param>
    /// <returns>The labels deleted.</returns>
    /// <exception cref="IOException">A file could not be changed.</exception>
    internal async Task<IReadOnlyList<LabelDescriptor>> DeleteOfSpaceAsync(string spaceId)
    {
        if (IsReadOnly) return [];
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var labels = await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var deleted = labels.Where(label => label.Space == spaceId).ToArray();
            foreach (var label in deleted)
            {
                if (label.SourcePath is not null) File.Delete(label.SourcePath);
            }

            if (deleted.Length > 0) await ForgetAsync([.. deleted.Select(static label => label.Id)]).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tells the path of a label: the names of the labels above it and its own, from the top.</summary>
    /// <param name="label">The label.</param>
    /// <param name="labels">The labels, as <see cref="LoadAsync"/> gives them.</param>
    /// <returns>Such as <c>Infra › Helm charts</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static string PathOf(LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(labels);
        var names = new List<string>();
        for (var current = label; current is not null && names.Count < LabelDescriptor.MaximumDepth; current = Find(current.Parent, labels))
            names.Insert(0, current.Name);
        return string.Join(PathSeparator, names);
    }

    /// <summary>Tells the identifiers of a label and of every label under it.</summary>
    /// <param name="id">The identifier of the label.</param>
    /// <param name="labels">The labels, as <see cref="LoadAsync"/> gives them.</param>
    /// <returns>The identifier given first, then those under it; only the one given when no label has it.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Subtree(string id, IReadOnlyList<LabelDescriptor> labels)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(labels);
        var ids = new List<string> { id };
        for (var index = 0; index < ids.Count; index++)
            ids.AddRange(labels.Where(label => label.Parent == ids[index]).Select(static label => label.Id));
        return ids;
    }

    private IEnumerable<string> LabelFiles()
        => Directory.Exists(_options.LabelsRoot)
            ? Directory.EnumerateFiles(_options.LabelsRoot, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
            : [];

    private static LabelDescriptor? Find(string? id, IReadOnlyList<LabelDescriptor> labels)
        => id is null ? null : labels.FirstOrDefault(label => label.Id == id);

    // The level of a label: 1 at the top.
    private static int Depth(LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels)
    {
        var depth = 1;
        for (var current = Find(label.Parent, labels); current is not null && depth <= labels.Count; current = Find(current.Parent, labels)) depth++;
        return depth;
    }

    // The levels a label and its sub-labels take: 1 for a label without any.
    private static int Height(LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels)
        => 1 + labels.Where(value => value.Parent == label.Id).Select(child => Height(child, labels)).DefaultIfEmpty(0).Max();

    // Puts the labels in the order of their tree. A parent that does not exist, that makes a loop or that puts the
    // label too deep is read as none; a sub-label is in the space of its top-level label.
    private static List<LabelDescriptor> Arranged(List<LabelDescriptor> labels)
    {
        var byId = labels.ToDictionary(static label => label.Id, StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.Parent is null) continue;
            if (!byId.ContainsKey(label.Parent)) { label.Parent = null; continue; }
            var seen = new HashSet<string>(StringComparer.Ordinal) { label.Id };
            var depth = 1;
            for (var current = label.Parent; current is not null; current = byId.TryGetValue(current, out var next) ? next.Parent : null)
            {
                if (!seen.Add(current) || ++depth > LabelDescriptor.MaximumDepth) { label.Parent = null; break; }
            }
        }

        var arranged = new List<LabelDescriptor>(labels.Count);
        void Add(string? parent, string? space)
        {
            var children = labels.Where(label => label.Parent == parent).ToList();
            children.Sort(static (left, right) =>
            {
                var order = left.Order.CompareTo(right.Order);
                if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            foreach (var child in children)
            {
                if (parent is not null) child.Space = space;
                arranged.Add(child);
                Add(child.Id, child.Space);
            }
        }

        Add(null, null);
        return arranged;
    }

    private async Task<LabelChange?> SpaceProblemAsync(string space, CancellationToken cancellationToken)
    {
        if (!SpaceDescriptor.IsValidId(space)) return new(LabelChangeStatus.Invalid, Message: $"'{space}' is not the identifier of a space.");
        return await _spaces.GetAsync(space, cancellationToken).ConfigureAwait(false) is null
            ? new(LabelChangeStatus.NotFound, Message: $"No space has the id '{space}'.")
            : null;
    }

    // Removes labels from every project file that names them.
    private async Task ForgetAsync(IReadOnlyCollection<string> ids)
    {
        foreach (var project in await Projects.LoadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (!project.Labels.Any(ids.Contains)) continue;
            await Projects.UpdateLabelsAsync(project.Id, current => current.Where(value => !ids.Contains(value)), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string PathOf(string id) => Path.Combine(_options.LabelsRoot, $"{id}.md");

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // What is wrong with a label about to be written, among the others; null when nothing is.
    private static string? Invalid(LabelDescriptor label, IReadOnlyList<LabelDescriptor> labels)
    {
        if (!SpaceDescriptor.IsValidName(label.Name) || label.Name.Length > LabelDescriptor.MaximumNameLength) return "A label name is 1 to 64 characters on one line.";
        if (label.Description is { Length: > LabelDescriptor.MaximumDescriptionLength }) return "A label description is at most 2000 characters.";
        if (label.Icon is not null && !SpaceDescriptor.IsValidIcon(label.Icon)) return $"'{label.Icon}' is not the name of an icon.";
        if (label.Color is not null && !SpaceDescriptor.IsValidColor(label.Color)) return $"'{label.Color}' is not a color written #rgb or #rrggbb.";
        return labels.Any(other => !ReferenceEquals(other, label) && other.Id != label.Id && other.Parent == label.Parent
                && string.Equals(other.Name, label.Name, StringComparison.OrdinalIgnoreCase))
            ? $"A label beside it is already named '{label.Name}'."
            : null;
    }

    // The file name is the identifier: what the front matter says of it is not what other files refer to.
    private async Task<LabelDescriptor?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        if (!LabelDescriptor.IsValidId(id)) return null;
        try
        {
            return Parse(id, path, await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or YamlException or DecoderFallbackException)
        {
            return null;
        }
    }

    // A value that is not valid is left out.
    private LabelDescriptor? Parse(string id, string path, string text)
    {
        var label = _serializer.DeserializeLabelMarkdown(text);
        label.Id = id;
        label.SourcePath = path;
        if (string.IsNullOrWhiteSpace(label.Name)) label.Name = id;
        if (label.Name.Length > LabelDescriptor.MaximumNameLength) label.Name = label.Name[..LabelDescriptor.MaximumNameLength].Trim();
        if (!SpaceDescriptor.IsValidIcon(label.Icon)) label.Icon = null;
        if (!SpaceDescriptor.IsValidColor(label.Color)) label.Color = null;
        if (label.Parent is not null && (!LabelDescriptor.IsValidId(label.Parent) || label.Parent == id)) label.Parent = null;
        if (label.Space is not null && !SpaceDescriptor.IsValidId(label.Space)) label.Space = null;
        if (label.Description is { Length: > LabelDescriptor.MaximumDescriptionLength }) label.Description = label.Description[..LabelDescriptor.MaximumDescriptionLength];
        return SpaceDescriptor.IsValidName(label.Name) ? label : null;
    }

    // Writes the whole file of a label over the bytes last read, or as a new file when it has none.
    private async Task<bool> WriteAsync(LabelDescriptor label, CancellationToken cancellationToken)
    {
        var text = _serializer.SerializeLabelMarkdown(label);
        var path = PathOf(label.Id);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_options.LabelsRoot);
            if (!await _files.TryCreateAsync(path, text, cancellationToken).ConfigureAwait(false)) return false;
            label.SourcePath = path;
            return true;
        }

        var snapshot = await _files.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await _files.SaveAsync(new TextFileSaveRequest(path, text, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision),
            cancellationToken).ConfigureAwait(false);
        return !result.IsConflict;
    }
}
