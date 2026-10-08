namespace CodeAlta.Catalog;

/// <summary>Exact source evidence for an explicit archive-state update.</summary>
/// <param name="Archived">Observed metadata state.</param><param name="SourcePath">Catalog-derived source.</param><param name="Revision">Raw-byte revision.</param>
public sealed record ProjectArchiveSnapshot(bool Archived, string SourcePath, TextFileRevision Revision);

public sealed partial class ProjectCatalog
{
    /// <summary>Reads archive evidence only for a bounded, uniquely owned supported catalog source.</summary>
    /// <param name="projectId">Exact persisted ID.</param><param name="projectPath">Exact normalized project path.</param><param name="cancellationToken">Read cancellation.</param>
    /// <returns>Evidence, or null for missing, ambiguous, linked or unsupported sources.</returns>
    /// <exception cref="IOException">The source could not be read.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<ProjectArchiveSnapshot?> ReadArchiveAsync(string projectId, string projectPath, CancellationToken cancellationToken = default)
    {
        var source = await ReadArchiveSourceAsync(projectId, projectPath, cancellationToken).ConfigureAwait(false);
        return source is null ? null : new(source.Value.Project.Archived, source.Value.Path, source.Value.Snapshot.Revision);
    }

    /// <summary>Conditionally replaces only an explicit root archived scalar; preserves unrelated source bytes and encoding.</summary>
    /// <remarks>Runs one at a time with the renames of this catalog instance. This is not cross-process CAS or protection against concurrent path swaps.
    /// Missing/complex archived scalars are unsupported. No directory, journal or running work is changed.</remarks>
    /// <param name="projectId">Exact persisted ID.</param><param name="projectPath">Exact project path.</param>
    /// <param name="expectedSourcePath">Previously observed catalog source, never renderer authority.</param><param name="expectedRevision">Previously observed raw-byte revision.</param>
    /// <param name="expectedArchived">Previously observed archive state.</param><param name="archived">Requested opposite state.</param><param name="cancellationToken">Cancellation before commit.</param>
    /// <returns>Updated, conflict or unsupported; never automatically retries.</returns>
    /// <exception cref="IOException">A read or conditional save failed; callers must not infer no write.</exception>
    /// <exception cref="OperationCanceledException">Canceled before commit.</exception>
    public async Task<ProjectDisplayNameRenameStatus> SetArchivedAsync(string projectId, string projectPath, string expectedSourcePath,
        TextFileRevision expectedRevision, bool expectedArchived, bool archived, CancellationToken cancellationToken = default)
    {
        await _projectEditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SetArchivedCoreAsync(projectId, projectPath, expectedSourcePath, expectedRevision, expectedArchived, archived, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectEditGate.Release();
        }
    }

    private async Task<ProjectDisplayNameRenameStatus> SetArchivedCoreAsync(string projectId, string projectPath, string expectedSourcePath,
        TextFileRevision expectedRevision, bool expectedArchived, bool archived, CancellationToken cancellationToken)
    {
        var source = await ReadArchiveSourceAsync(projectId, projectPath, cancellationToken).ConfigureAwait(false);
        if (source is null) return ProjectDisplayNameRenameStatus.Unsupported;
        var (project, path, snapshot, start, end) = source.Value;
        if (expectedArchived == archived || project.Archived != expectedArchived || path != expectedSourcePath || snapshot.Revision != expectedRevision)
            return ProjectDisplayNameRenameStatus.Conflict;
        var updated = string.Concat(snapshot.Text.AsSpan(0, start), archived ? "true" : "false", snapshot.Text.AsSpan(end));
        if (_serializer.DeserializeProjectMarkdown(updated).Archived != archived) return ProjectDisplayNameRenameStatus.Unsupported;
        var result = await _projectNameFiles.SaveAsync(new TextFileSaveRequest(path, updated, snapshot.Encoding, snapshot.HasByteOrderMark, expectedRevision), cancellationToken).ConfigureAwait(false);
        return result.IsConflict ? ProjectDisplayNameRenameStatus.Conflict : ProjectDisplayNameRenameStatus.Updated;
    }

    private async Task<(ProjectDescriptor Project, string Path, TextFileSnapshot Snapshot, int Start, int End)?> ReadArchiveSourceAsync(string id, string path, CancellationToken token)
    {
        if ((await ReadBoundedOwnershipAsync(id, path, token).ConfigureAwait(false)).Status != ProjectOwnershipStatus.Match) return null;
        var project = await GetByIdAsync(id, token).ConfigureAwait(false);
        if (project is null || project.Id != id || project.ProjectPath != path) return null;
        // Reuse the supported catalog-source/identity/link checks, not a renderer filename.
        var source = await ReadNameSourceAsync(project, id, path, token, "archived").ConfigureAwait(false);
        if (source is null) return null;
        var parsed = ParseName(source.Value.Snapshot.Text, "archived");
        if (parsed is null || parsed.Value.Name is not ("true" or "false")) return null;
        var current = _serializer.DeserializeProjectMarkdown(source.Value.Snapshot.Text);
        return (current, source.Value.Path, source.Value.Snapshot, parsed.Value.Start, parsed.Value.End);
    }
}
