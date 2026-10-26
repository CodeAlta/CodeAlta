using SharpYaml;
using Path = System.IO.Path;

namespace CodeAlta.Catalog;

/// <summary>The result of a change of the spaces a project belongs to.</summary>
public enum ProjectSpacesStatus
{
    /// <summary>The project file names the new spaces.</summary>
    Updated,
    /// <summary>The project already named exactly these spaces; nothing was written.</summary>
    Unchanged,
    /// <summary>No project has this identifier; nothing was written.</summary>
    NotFound,
    /// <summary>The project file kept changing under the edit; nothing was written.</summary>
    Conflict,
    /// <summary>The project file cannot be edited without changing something else in it; nothing was written.</summary>
    Unsupported,
}

public sealed partial class ProjectCatalog
{
    /// <summary>
    /// Changes the spaces a project belongs to, in its own file: only the <c>spaces</c> entry of the
    /// front matter is written, added or removed, and every other byte of the file stays as it is.
    /// </summary>
    /// <remarks>
    /// Runs one at a time with the other conditional edits of this catalog instance (renames, archive changes),
    /// from its read to its save, so that the change is worked out from the spaces the file names at that
    /// moment. A file another process changes meanwhile is read again, twice at most.
    /// </remarks>
    /// <param name="projectId">The persisted project identifier.</param>
    /// <param name="change">Gives the spaces the project must name from the ones it names now.</param>
    /// <param name="cancellationToken">Cancels before the save.</param>
    /// <returns>What happened; nothing is written unless it is <see cref="ProjectSpacesStatus.Updated"/>.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The catalog read or the conditional save failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the save.</exception>
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    public async Task<ProjectSpacesStatus> UpdateSpacesAsync(string projectId, Func<IReadOnlyList<string>, IEnumerable<string>> change,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(change);
        await _projectEditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var status = await UpdateSpacesCoreAsync(projectId, change, cancellationToken).ConfigureAwait(false);
                if (status != ProjectSpacesStatus.Conflict || attempt == 2) return status;
            }
        }
        finally
        {
            _projectEditGate.Release();
        }
    }

    private async Task<ProjectSpacesStatus> UpdateSpacesCoreAsync(string projectId, Func<IReadOnlyList<string>, IEnumerable<string>> change,
        CancellationToken cancellationToken)
    {
        var project = await GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return ProjectSpacesStatus.NotFound;
        if (project.SourcePath is null) return ProjectSpacesStatus.Unsupported;
        var sourcePath = Path.GetFullPath(project.SourcePath);
        var projectsRoot = Path.GetFullPath(_options.ProjectsRoot);
        if (sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, $"{project.Slug}.md"))
            && sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, project.Slug, "readme.md")))
            return ProjectSpacesStatus.Unsupported;
        // As for a rename: a link observed in the catalog is not authority to edit what it points to.
        foreach (var path in new[] { _options.GlobalRoot, projectsRoot, Path.GetDirectoryName(sourcePath)!, sourcePath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return ProjectSpacesStatus.Unsupported;
        var snapshot = await _projectNameFiles.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var current = _serializer.DeserializeProjectMarkdown(snapshot.Text);
        if (!string.Equals(current.Id, project.Id, StringComparison.Ordinal)) return ProjectSpacesStatus.Conflict;
        var next = SpaceDescriptor.NormalizeIds(change(current.Spaces.AsReadOnly()));
        if (next.SequenceEqual(current.Spaces, StringComparer.Ordinal)) return ProjectSpacesStatus.Unchanged;
        if (ReplaceSpacesEntry(snapshot.Text, next) is not { } updated) return ProjectSpacesStatus.Unsupported;
        // Do not claim success if a YAML parser reads the edited file differently.
        ProjectDescriptor reread;
        try { reread = _serializer.DeserializeProjectMarkdown(updated); }
        catch (Exception ex) when (ex is YamlException or InvalidDataException) { return ProjectSpacesStatus.Unsupported; }
        if (!reread.Spaces.SequenceEqual(next, StringComparer.Ordinal) || reread.Id != current.Id || reread.Slug != current.Slug
            || reread.ProjectPath != current.ProjectPath || reread.DisplayName != current.DisplayName || reread.Name != current.Name
            || reread.Archived != current.Archived || reread.Description != current.Description || !reread.Tags.SequenceEqual(current.Tags)
            || !reread.Labels.SequenceEqual(current.Labels, StringComparer.Ordinal))
            return ProjectSpacesStatus.Unsupported;
        var result = await SaveSourceAsync(new TextFileSaveRequest(sourcePath, updated, snapshot.Encoding, snapshot.HasByteOrderMark,
            snapshot.Revision), cancellationToken).ConfigureAwait(false);
        return result.IsConflict ? ProjectSpacesStatus.Conflict : ProjectSpacesStatus.Updated;
    }

    /// <summary>
    /// Chooses the sandbox of the sessions of a project, in its own file: only the <c>sandbox</c> entry of the front
    /// matter is written, added or removed, and every other byte of the file stays as it is. The outcomes are those of
    /// <see cref="UpdateSpacesAsync"/>.
    /// </summary>
    /// <param name="projectId">The persisted project identifier.</param>
    /// <param name="sandbox">A sandbox profile, <c>off</c>, or null for no choice of the project.</param>
    /// <param name="cancellationToken">Cancels before the save.</param>
    /// <returns>What happened; nothing is written unless it is <see cref="ProjectSpacesStatus.Updated"/>.</returns>
    /// <exception cref="ArgumentException">The identifier is empty, or <paramref name="sandbox"/> is not a sandbox name.</exception>
    /// <exception cref="IOException">The catalog read or the conditional save failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the save.</exception>
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    public async Task<ProjectSpacesStatus> UpdateSandboxAsync(string projectId, string? sandbox, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        sandbox = string.IsNullOrWhiteSpace(sandbox) ? null : sandbox.Trim();
        if (sandbox is not null && !SpaceNewSessions.IsSandboxName(sandbox)) throw new ArgumentException($"'{sandbox}' is not a sandbox name.", nameof(sandbox));
        await _projectEditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var status = await UpdateSandboxCoreAsync(projectId, sandbox, cancellationToken).ConfigureAwait(false);
                if (status != ProjectSpacesStatus.Conflict || attempt == 2) return status;
            }
        }
        finally
        {
            _projectEditGate.Release();
        }
    }

    private async Task<ProjectSpacesStatus> UpdateSandboxCoreAsync(string projectId, string? sandbox, CancellationToken cancellationToken)
    {
        var project = await GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return ProjectSpacesStatus.NotFound;
        if (project.SourcePath is null) return ProjectSpacesStatus.Unsupported;
        var sourcePath = Path.GetFullPath(project.SourcePath);
        var projectsRoot = Path.GetFullPath(_options.ProjectsRoot);
        if (sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, $"{project.Slug}.md"))
            && sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, project.Slug, "readme.md")))
            return ProjectSpacesStatus.Unsupported;
        foreach (var path in new[] { _options.GlobalRoot, projectsRoot, Path.GetDirectoryName(sourcePath)!, sourcePath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return ProjectSpacesStatus.Unsupported;
        var snapshot = await _projectNameFiles.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var current = _serializer.DeserializeProjectMarkdown(snapshot.Text);
        if (!string.Equals(current.Id, project.Id, StringComparison.Ordinal)) return ProjectSpacesStatus.Conflict;
        if (string.Equals(current.Sandbox, sandbox, StringComparison.Ordinal)) return ProjectSpacesStatus.Unchanged;
        // Quoted: YAML reads `off` as false.
        if (ReplaceEntry(snapshot.Text, "sandbox", sandbox is null ? null : $"sandbox: \"{sandbox}\"") is not { } updated) return ProjectSpacesStatus.Unsupported;
        ProjectDescriptor reread;
        try { reread = _serializer.DeserializeProjectMarkdown(updated); }
        catch (Exception ex) when (ex is YamlException or InvalidDataException) { return ProjectSpacesStatus.Unsupported; }
        if (!string.Equals(reread.Sandbox, sandbox, StringComparison.Ordinal) || reread.Id != current.Id || reread.Slug != current.Slug
            || reread.ProjectPath != current.ProjectPath || reread.DisplayName != current.DisplayName || reread.Name != current.Name
            || reread.Archived != current.Archived || reread.Description != current.Description || !reread.Tags.SequenceEqual(current.Tags)
            || !reread.Spaces.SequenceEqual(current.Spaces, StringComparer.Ordinal) || !reread.Labels.SequenceEqual(current.Labels, StringComparer.Ordinal))
            return ProjectSpacesStatus.Unsupported;
        var result = await SaveSourceAsync(new TextFileSaveRequest(sourcePath, updated, snapshot.Encoding, snapshot.HasByteOrderMark,
            snapshot.Revision), cancellationToken).ConfigureAwait(false);
        return result.IsConflict ? ProjectSpacesStatus.Conflict : ProjectSpacesStatus.Updated;
    }

    /// <summary>
    /// Changes the labels of a project, in its own file: only the <c>labels</c> entry of the front matter is written,
    /// added or removed, and every other byte of the file stays as it is. The outcomes are those of
    /// <see cref="UpdateSpacesAsync"/>.
    /// </summary>
    /// <param name="projectId">The persisted project identifier.</param>
    /// <param name="change">Gives the labels the project must have from the ones it has now.</param>
    /// <param name="cancellationToken">Cancels before the save.</param>
    /// <returns>What happened; nothing is written unless it is <see cref="ProjectSpacesStatus.Updated"/>.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The catalog read or the conditional save failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the save.</exception>
    /// <exception cref="InvalidOperationException">The catalog is read-only (<see cref="IsReadOnly"/>).</exception>
    public async Task<ProjectSpacesStatus> UpdateLabelsAsync(string projectId, Func<IReadOnlyList<string>, IEnumerable<string>> change,
        CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(change);
        await _projectEditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var status = await UpdateLabelsCoreAsync(projectId, change, cancellationToken).ConfigureAwait(false);
                if (status != ProjectSpacesStatus.Conflict || attempt == 2) return status;
            }
        }
        finally
        {
            _projectEditGate.Release();
        }
    }

    private async Task<ProjectSpacesStatus> UpdateLabelsCoreAsync(string projectId, Func<IReadOnlyList<string>, IEnumerable<string>> change,
        CancellationToken cancellationToken)
    {
        var project = await GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return ProjectSpacesStatus.NotFound;
        if (project.SourcePath is null) return ProjectSpacesStatus.Unsupported;
        var sourcePath = Path.GetFullPath(project.SourcePath);
        var projectsRoot = Path.GetFullPath(_options.ProjectsRoot);
        if (sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, $"{project.Slug}.md"))
            && sourcePath != Path.GetFullPath(Path.Combine(projectsRoot, project.Slug, "readme.md")))
            return ProjectSpacesStatus.Unsupported;
        foreach (var path in new[] { _options.GlobalRoot, projectsRoot, Path.GetDirectoryName(sourcePath)!, sourcePath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return ProjectSpacesStatus.Unsupported;
        var snapshot = await _projectNameFiles.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var current = _serializer.DeserializeProjectMarkdown(snapshot.Text);
        if (!string.Equals(current.Id, project.Id, StringComparison.Ordinal)) return ProjectSpacesStatus.Conflict;
        var next = LabelDescriptor.NormalizeIds(change(current.Labels.AsReadOnly()));
        if (next.SequenceEqual(current.Labels, StringComparer.Ordinal)) return ProjectSpacesStatus.Unchanged;
        if (ReplaceEntry(snapshot.Text, "labels", next.Count == 0 ? null : $"labels: [{string.Join(", ", next.Select(Scalar))}]") is not { } updated)
            return ProjectSpacesStatus.Unsupported;
        ProjectDescriptor reread;
        try { reread = _serializer.DeserializeProjectMarkdown(updated); }
        catch (Exception ex) when (ex is YamlException or InvalidDataException) { return ProjectSpacesStatus.Unsupported; }
        if (!reread.Labels.SequenceEqual(next, StringComparer.Ordinal) || reread.Id != current.Id || reread.Slug != current.Slug
            || reread.ProjectPath != current.ProjectPath || reread.DisplayName != current.DisplayName || reread.Name != current.Name
            || reread.Archived != current.Archived || reread.Description != current.Description || !reread.Tags.SequenceEqual(current.Tags)
            || !reread.Spaces.SequenceEqual(current.Spaces, StringComparer.Ordinal) || !string.Equals(reread.Sandbox, current.Sandbox, StringComparison.Ordinal))
            return ProjectSpacesStatus.Unsupported;
        var result = await SaveSourceAsync(new TextFileSaveRequest(sourcePath, updated, snapshot.Encoding, snapshot.HasByteOrderMark,
            snapshot.Revision), cancellationToken).ConfigureAwait(false);
        return result.IsConflict ? ProjectSpacesStatus.Conflict : ProjectSpacesStatus.Updated;
    }

    // Writes the `spaces` entry of the front matter on one line, replaces the lines it took, or removes it
    // when the project is in no space. Null when the front matter has no single such entry to replace.
    private static string? ReplaceSpacesEntry(string text, IReadOnlyList<string> ids)
        => ReplaceEntry(text, "spaces", ids.Count == 0 ? null : $"spaces: [{string.Join(", ", ids.Select(Scalar))}]");

    // An identifier is letters, digits, `-`, `_` and `.`: it is quoted when YAML would read it as something
    // else than a text (a number, `true`, `null`, `no`).
    private static string Scalar(string id)
        => id[0] is >= 'a' and <= 'z' && !id.Contains('.') && id is not ("true" or "false" or "null" or "yes" or "no" or "on" or "off")
            ? id : $"\"{id}\"";

    // Writes an entry of the front matter on one line, replaces the lines it took, or removes it when the line is
    // null. Null when the front matter has no single such entry to replace.
    private static string? ReplaceEntry(string text, string key, string? entryLine)
    {
        var firstEnd = text.IndexOf('\n');
        if (firstEnd < 0 || text.AsSpan(0, firstEnd).TrimEnd('\r') is not "---") return null;
        var newline = firstEnd > 0 && text[firstEnd - 1] == '\r' ? "\r\n" : "\n";
        int entryStart = -1, entryEnd = -1, end = -1;
        for (var pos = firstEnd + 1; pos < text.Length;)
        {
            var next = text.IndexOf('\n', pos);
            var lineEnd = next < 0 ? text.Length : next + 1;
            var line = text.AsSpan(pos, lineEnd - pos).TrimEnd("\r\n");
            if (line is "---")
            {
                end = pos;
                if (entryStart >= 0 && entryEnd < 0) entryEnd = pos;
                break;
            }

            if (entryStart >= 0 && entryEnd < 0)
            {
                // The entry goes on with its indented lines and with the items of a list written at the margin.
                if (!(line.Length > 0 && (line[0] is ' ' or '\t' || line is "-" || line.StartsWith("- ")))) entryEnd = pos;
            }

            if (line.StartsWith(key) && line[key.Length..].TrimStart(' ').StartsWith(":"))
            {
                if (entryStart >= 0) return null; // Written twice: which one counts is not ours to decide.
                entryStart = pos;
            }

            pos = lineEnd;
        }

        if (end < 0) return null;
        var entry = entryLine is null ? string.Empty : entryLine + newline;
        return entryStart < 0
            ? string.Concat(text.AsSpan(0, end), entry, text.AsSpan(end))
            : string.Concat(text.AsSpan(0, entryStart), entry, text.AsSpan(entryEnd));
    }
}
