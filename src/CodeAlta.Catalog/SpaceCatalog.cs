using System.Text;
using SharpYaml;

namespace CodeAlta.Catalog;

/// <summary>How a change of the spaces ended.</summary>
public enum SpaceChangeStatus
{
    /// <summary>The change was made, or there was nothing to change.</summary>
    Ok,
    /// <summary>No space, or no project, has the identifier given.</summary>
    NotFound,
    /// <summary>A value given is not valid; nothing was written.</summary>
    Invalid,
    /// <summary>The change is not one that can be made, such as deleting the default space; nothing was written.</summary>
    Refused,
    /// <summary>A file kept changing under the edit, or cannot be edited as it is written.</summary>
    Conflict,
}

/// <summary>The result of a change of the spaces.</summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Space">The space after the change, when there is one.</param>
/// <param name="Message">What was wrong, for a change that was not made.</param>
public sealed record SpaceChange(SpaceChangeStatus Status, SpaceDescriptor? Space = null, string? Message = null)
{
    /// <summary>Gets whether the change was made.</summary>
    public bool Succeeded => Status == SpaceChangeStatus.Ok;
}

/// <summary>What to change of a space: a value left <see langword="null"/> stays as it is.</summary>
/// <param name="Name">The new name.</param>
/// <param name="Description">The new description; empty removes it.</param>
/// <param name="Icon">The new icon; empty goes back to the usual one.</param>
/// <param name="Color">The new color; empty removes it.</param>
public sealed record SpaceEdit(string? Name = null, string? Description = null, string? Icon = null, string? Color = null);

/// <summary>A space with the projects that belong to it.</summary>
/// <param name="Space">The space.</param>
/// <param name="ProjectIds">The identifiers of its projects, in the order of the project catalog: every project for the default space.</param>
public sealed record SpaceMembers(SpaceDescriptor Space, IReadOnlyList<string> ProjectIds);

/// <summary>
/// Loads and persists the spaces of the global catalog: one Markdown file for each under
/// <see cref="CatalogOptions.SpacesRoot"/>, and in each project file the spaces the project belongs to.
/// </summary>
/// <remarks>
/// The default space holds every project and always exists; a file <c>default.md</c> only gives it another
/// name, icon, color or description. The changes of one instance run one after the other. Two processes on
/// the same catalog (the normal and the developer instance) see each other's changes at their next read.
/// </remarks>
public sealed class SpaceCatalog
{
    /// <summary>The most spaces a catalog holds, the default one included.</summary>
    public const int MaximumSpaces = 32;

    private readonly CatalogOptions _options;
    private readonly ProjectCatalog _projects;
    private readonly CatalogYamlSerializer _serializer;
    private readonly TextFileCodec _files = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SpaceCatalog"/> class.</summary>
    /// <param name="projects">The project catalog whose files name the spaces of each project.</param>
    /// <param name="serializer">Optional YAML serializer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="projects"/> is <see langword="null"/>.</exception>
    public SpaceCatalog(ProjectCatalog projects, CatalogYamlSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects;
        _options = projects.Options;
        _serializer = serializer ?? new CatalogYamlSerializer();
    }

    /// <summary>Gets the project catalog the spaces group.</summary>
    public ProjectCatalog Projects => _projects;

    /// <summary>
    /// Loads the spaces: the default one first, then the others by their place and their name. A file that
    /// cannot be read as a space is left out, so that one bad file never hides the others.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The spaces; never empty.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<IReadOnlyList<SpaceDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var spaces = new List<SpaceDescriptor>();
        SpaceDescriptor? defaultSpace = null;
        if (Directory.Exists(_options.SpacesRoot))
        {
            foreach (var path in Directory.EnumerateFiles(_options.SpacesRoot, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } space) continue;
                if (space.IsDefault) defaultSpace = space;
                else if (spaces.Count < MaximumSpaces - 1) spaces.Add(space);
            }
        }

        spaces.Sort(static (left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return order != 0 ? order : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });
        spaces.Insert(0, defaultSpace ?? SpaceDescriptor.CreateDefault());
        return spaces;
    }

    /// <summary>Gets one space.</summary>
    /// <param name="id">The identifier of the space.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The space, or <see langword="null"/> when none has this identifier.</returns>
    public async Task<SpaceDescriptor?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!SpaceDescriptor.IsValidId(id)) return null;
        var spaces = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return spaces.FirstOrDefault(space => string.Equals(space.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Loads the spaces with the projects of each.</summary>
    /// <param name="includeArchived">Whether archived projects are listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The spaces in their order, the default one first with every project.</returns>
    public async Task<IReadOnlyList<SpaceMembers>> LoadMembersAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        var spaces = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var projects = (await _projects.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(project => includeArchived || !project.Archived).ToArray();
        return spaces.Select(space => new SpaceMembers(space, projects
            .Where(project => space.IsDefault || project.Spaces.Contains(space.Id, StringComparer.Ordinal))
            .Select(static project => project.Id).ToArray())).ToArray();
    }

    /// <summary>
    /// Creates a space. Its identifier is worked out from its name, with a number after it when the
    /// identifier is taken, and never changes afterwards.
    /// </summary>
    /// <param name="name">The name of the space.</param>
    /// <param name="description">What the space is for.</param>
    /// <param name="icon">The name of its icon.</param>
    /// <param name="color">Its color, <c>#rgb</c> or <c>#rrggbb</c>.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>The space created, or why none was.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task<SpaceChange> CreateAsync(string name, string? description = null, string? icon = null, string? color = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var spaces = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (spaces.Count >= MaximumSpaces) return new(SpaceChangeStatus.Refused, Message: $"There are already {MaximumSpaces} spaces.");
            var space = new SpaceDescriptor
            {
                Name = name?.Trim() ?? string.Empty,
                Description = Optional(description),
                Icon = Optional(icon)?.ToLowerInvariant(),
                Color = Optional(color),
                Order = spaces.Max(static value => value.Order) + 1,
            };
            if (Invalid(space, spaces) is { } problem) return new(SpaceChangeStatus.Invalid, Message: problem);
            var baseId = SpaceDescriptor.IdFromName(space.Name);
            for (var suffix = 1; suffix < 1000; suffix++)
            {
                space.Id = suffix == 1 ? baseId : $"{(baseId.Length > 60 ? baseId[..60] : baseId)}-{suffix}";
                if (space.IsDefault || spaces.Any(value => value.Id == space.Id)) continue;
                var path = PathOf(space.Id);
                if (!await _files.TryCreateAsync(path, _serializer.SerializeSpaceMarkdown(space), cancellationToken).ConfigureAwait(false)) continue;
                space.SourcePath = path;
                return new(SpaceChangeStatus.Ok, space);
            }

            return new(SpaceChangeStatus.Conflict, Message: "No free identifier was found for this name.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes the name, the description, the icon or the color of a space. Its identifier stays.</summary>
    /// <param name="id">The identifier of the space.</param>
    /// <param name="edit">What to change.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>The space after the change, or why it was not made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task<SpaceChange> UpdateAsync(string id, SpaceEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var spaces = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var space = spaces.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal));
            if (space is null) return new(SpaceChangeStatus.NotFound, Message: $"No space has the id '{id}'.");
            if (edit.Name is not null) space.Name = edit.Name.Trim();
            if (edit.Description is not null) space.Description = Optional(edit.Description);
            if (edit.Icon is not null) space.Icon = Optional(edit.Icon)?.ToLowerInvariant();
            if (edit.Color is not null) space.Color = Optional(edit.Color);
            if (Invalid(space, spaces) is { } problem) return new(SpaceChangeStatus.Invalid, Message: problem);
            return await WriteAsync(space, cancellationToken).ConfigureAwait(false)
                ? new(SpaceChangeStatus.Ok, space)
                : new(SpaceChangeStatus.Conflict, Message: "The file of the space changed meanwhile.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes a space: its file, then its name in the file of each of its projects. The projects, their
    /// folders and their sessions stay, and are still in the default space.
    /// </summary>
    /// <param name="id">The identifier of the space.</param>
    /// <param name="cancellationToken">Cancels before the file is deleted.</param>
    /// <returns>The space deleted, or why none was. The default space is refused.</returns>
    /// <exception cref="IOException">A file could not be changed.</exception>
    public async Task<SpaceChange> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(id, SpaceDescriptor.DefaultId, StringComparison.Ordinal))
                return new(SpaceChangeStatus.Refused, Message: "The default space holds every project and cannot be deleted.");
            var space = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal));
            if (space?.SourcePath is null) return new(SpaceChangeStatus.NotFound, Message: $"No space has the id '{id}'.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(space.SourcePath);
            // From here the space is gone whatever happens: a project file that still names it means nothing.
            foreach (var project in await _projects.LoadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (!project.Spaces.Contains(id, StringComparer.Ordinal)) continue;
                await _projects.UpdateSpacesAsync(project.Id, current => current.Where(value => value != id), CancellationToken.None).ConfigureAwait(false);
            }

            space.SourcePath = null;
            return new(SpaceChangeStatus.Ok, space);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts the spaces in the order given; the ones not named follow in their present order.</summary>
    /// <param name="ids">The identifiers, first to last. The default space is always first and is ignored here.</param>
    /// <param name="cancellationToken">Cancels before a file is written.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task<SpaceChange> ReorderAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var spaces = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Where(static space => !space.IsDefault).ToList();
            var ordered = ids.Distinct(StringComparer.Ordinal).Select(id => spaces.FirstOrDefault(space => space.Id == id)).OfType<SpaceDescriptor>().ToList();
            ordered.AddRange(spaces.Except(ordered));
            var complete = true;
            for (var index = 0; index < ordered.Count; index++)
            {
                if (ordered[index].Order == index + 1) continue;
                ordered[index].Order = index + 1;
                complete &= await WriteAsync(ordered[index], cancellationToken).ConfigureAwait(false);
            }

            return complete ? new(SpaceChangeStatus.Ok) : new(SpaceChangeStatus.Conflict, Message: "A space file changed meanwhile.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Changes the spaces of one project in one edit of its file: it joins some and leaves others. A project
    /// is always in the default space, which can be neither joined nor left.
    /// </summary>
    /// <param name="projectId">The identifier of the project.</param>
    /// <param name="join">The spaces the project joins.</param>
    /// <param name="leave">The spaces the project leaves.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>How the change ended.</returns>
    /// <exception cref="IOException">The project file could not be changed.</exception>
    public async Task<SpaceChange> AssignAsync(string projectId, IReadOnlyList<string>? join, IReadOnlyList<string>? leave,
        CancellationToken cancellationToken = default)
    {
        join ??= [];
        leave ??= [];
        if (string.IsNullOrWhiteSpace(projectId)) return new(SpaceChangeStatus.NotFound, Message: "A project is required.");
        if (join.Contains(SpaceDescriptor.DefaultId, StringComparer.Ordinal) || leave.Contains(SpaceDescriptor.DefaultId, StringComparer.Ordinal))
            return new(SpaceChangeStatus.Refused, Message: "Every project is in the default space.");
        var known = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Select(static space => space.Id).ToHashSet(StringComparer.Ordinal);
        if (join.FirstOrDefault(id => !known.Contains(id)) is { } missing)
            return new(SpaceChangeStatus.NotFound, Message: $"No space has the id '{missing}'.");
        var status = await _projects.UpdateSpacesAsync(projectId,
            current => current.Where(id => !leave.Contains(id, StringComparer.Ordinal)).Concat(join), cancellationToken).ConfigureAwait(false);
        return status switch
        {
            ProjectSpacesStatus.Updated or ProjectSpacesStatus.Unchanged => new(SpaceChangeStatus.Ok),
            ProjectSpacesStatus.NotFound => new(SpaceChangeStatus.NotFound, Message: $"No project has the id '{projectId}'."),
            ProjectSpacesStatus.Conflict => new(SpaceChangeStatus.Conflict, Message: "The project file changed meanwhile."),
            _ => new(SpaceChangeStatus.Conflict, Message: "The project file cannot be edited as it is written."),
        };
    }

    /// <summary>
    /// Gives a catalog that never had spaces its first ones, <c>Work</c> and <c>Personal</c>, which the user
    /// fills, renames or deletes. Does nothing once the folder of the spaces exists, so that what the user
    /// deleted does not come back.
    /// </summary>
    /// <param name="cancellationToken">Cancels before a file is written.</param>
    /// <returns><see langword="true"/> when the first spaces were written.</returns>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_options.SpacesRoot)) return false;
            Directory.CreateDirectory(_options.SpacesRoot);
            SpaceDescriptor[] first =
            [
                new() { Id = "work", Name = "Work", Icon = "briefcase", Color = "#2d72d2", Order = 1 },
                new() { Id = "personal", Name = "Personal", Icon = "house", Color = "#238551", Order = 2 },
            ];
            foreach (var space in first)
            {
                await _files.TryCreateAsync(PathOf(space.Id), _serializer.SerializeSpaceMarkdown(space), cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathOf(string id) => Path.Combine(_options.SpacesRoot, $"{id}.md");

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // What is wrong with a space about to be written, among the others; null when nothing is.
    private static string? Invalid(SpaceDescriptor space, IReadOnlyList<SpaceDescriptor> spaces)
    {
        if (!SpaceDescriptor.IsValidName(space.Name)) return "A space name is 1 to 64 characters on one line.";
        if (space.Description is { Length: > SpaceDescriptor.MaximumDescriptionLength }) return "A space description is at most 2000 characters.";
        if (space.Icon is not null && !SpaceDescriptor.IsValidIcon(space.Icon)) return $"'{space.Icon}' is not the name of an icon.";
        if (space.Color is not null && !SpaceDescriptor.IsValidColor(space.Color)) return $"'{space.Color}' is not a color written #rgb or #rrggbb.";
        return spaces.Any(other => !ReferenceEquals(other, space) && string.Equals(other.Name, space.Name, StringComparison.OrdinalIgnoreCase))
            ? $"A space is already named '{space.Name}'."
            : null;
    }

    // The file name is the identifier: what the front matter says of it is not what other files refer to.
    private async Task<SpaceDescriptor?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        if (!SpaceDescriptor.IsValidId(id)) return null;
        try
        {
            var space = _serializer.DeserializeSpaceMarkdown(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            space.Id = id;
            space.SourcePath = path;
            if (string.IsNullOrWhiteSpace(space.Name)) space.Name = space.IsDefault ? SpaceDescriptor.DefaultName : id;
            if (space.Name.Length > SpaceDescriptor.MaximumNameLength) space.Name = space.Name[..SpaceDescriptor.MaximumNameLength].Trim();
            if (!SpaceDescriptor.IsValidIcon(space.Icon)) space.Icon = null;
            if (!SpaceDescriptor.IsValidColor(space.Color)) space.Color = null;
            if (space.Description is { Length: > SpaceDescriptor.MaximumDescriptionLength }) space.Description = space.Description[..SpaceDescriptor.MaximumDescriptionLength];
            if (space.IsDefault) space.Order = 0;
            return SpaceDescriptor.IsValidName(space.Name) ? space : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or YamlException or DecoderFallbackException)
        {
            return null;
        }
    }

    // Writes the whole file of a space: over the bytes last read when it has one, as a new file otherwise
    // (the default space has none until it is given a name, an icon, a color or a description).
    private async Task<bool> WriteAsync(SpaceDescriptor space, CancellationToken cancellationToken)
    {
        var text = _serializer.SerializeSpaceMarkdown(space);
        var path = PathOf(space.Id);
        if (!File.Exists(path))
        {
            if (!await _files.TryCreateAsync(path, text, cancellationToken).ConfigureAwait(false)) return false;
            space.SourcePath = path;
            return true;
        }

        var snapshot = await _files.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await _files.SaveAsync(new TextFileSaveRequest(path, text, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision),
            cancellationToken).ConfigureAwait(false);
        return !result.IsConflict;
    }
}
