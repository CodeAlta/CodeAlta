using System.Security;
using System.Text;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reads and writes one existing text file of a project for the desktop file editor, with the terminal
/// editor's rules: the file's encoding, BOM and newlines are kept, and a file changed elsewhere since it
/// was read is reported as a conflict instead of being replaced.
/// </summary>
/// <remarks>
/// A file is addressed by a project id and a path relative to that project's folder, never by an absolute
/// path. The path must stay inside the folder: a rooted path, a drive or stream name, a parent segment and
/// any link (reparse point) between the folder and the file are refused as <c>outside_root</c>. The service
/// edits files; it does not create, rename or delete them.
/// </remarks>
[NeoRpcService("projectFiles", Version = 1)]
internal sealed class ProjectFilesService
{
    /// <summary>Largest file read or written, in bytes on disk (including a BOM).</summary>
    internal const int MaximumFileBytes = 1024 * 1024;

    /// <summary>Longest project-relative path accepted, in UTF-16 units.</summary>
    internal const int MaximumPathLength = 1024;

    // An overwrite reads the file for its encoding first; a writer racing that read gets this many tries.
    private const int MaximumOverwriteAttempts = 3;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly IProjectFileSearchService? _search;
    private readonly TextFileCodec _textFiles = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProjectFilesService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="search">Receives each file read as a recent file, or null to record nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProjectFilesService(ProjectCatalog projects, string epoch, IProjectFileSearchService? search = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _epoch = epoch;
        _search = search;
    }

    /// <summary>Reads a whole text file and the revision a later write must name.</summary>
    [NeoRpcMethod("read")]
    public async Task<ProjectFileReadResponse> ReadAsync(ProjectFileReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        // A size beyond what the contract's 32-bit number holds is reported as that maximum.
        ProjectFileReadResponse Refused(string status, long length = 0) => new(status, path, null, null, false, (int)Math.Min(length, int.MaxValue));
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null) return Refused("invalid");
        var named = Normalize(request.Path, out path);
        if (named != "ok") return Refused(named);
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        try
        {
            var located = Locate(root, path!, out var full);
            if (located != "ok") return Refused(located);
            // Checked before reading: a large file is never loaded to find out that it is too large.
            var onDisk = new FileInfo(full).Length;
            if (onDisk > MaximumFileBytes) return Refused("too_large", onDisk);
            var snapshot = await _textFiles.LoadAsync(full, cancellationToken).ConfigureAwait(false);
            // The size of what was actually read: the file may have grown since the check.
            var length = snapshot.Encoding.GetPreamble().Length + (long)snapshot.Encoding.GetByteCount(snapshot.Text);
            if (length > MaximumFileBytes) return Refused("too_large", length);
            if (snapshot.Text.Contains('\0')) return Refused("binary");
            var readOnly = (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0;
            await RecordUsageAsync(root, path!).ConfigureAwait(false);
            return new("ok", path, snapshot.Text, snapshot.Revision.ContentHash, readOnly, (int)length);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8, nor UTF-16/UTF-32 with a BOM.
            return Refused("binary");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("read_failed"); // Never serialize exception details: they name absolute paths.
        }
    }

    /// <summary>
    /// Replaces the text of an existing file whose revision is still the expected one, or of any existing
    /// text file when <c>Overwrite</c> is set.
    /// </summary>
    [NeoRpcMethod("write")]
    public async Task<ProjectFileWriteResponse> WriteAsync(ProjectFileWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileWriteResponse Refused(string status, string? revision = null) => new(status, path, revision);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null || request.Content is null) return Refused("invalid");
        var named = Normalize(request.Path, out path);
        if (named != "ok") return Refused(named);
        // Every supported encoding needs at least one byte for each UTF-16 unit.
        if (request.Content.Length > MaximumFileBytes) return Refused("too_large");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writing = false;
        try
        {
            var located = Locate(root, path!, out var full);
            if (located != "ok") return Refused(located);
            for (var attempt = 1; ; attempt++)
            {
                if (new FileInfo(full).Length > MaximumFileBytes) return Refused("too_large");
                // The file on disk decides the encoding and BOM of what replaces it.
                var snapshot = await _textFiles.LoadAsync(full, cancellationToken).ConfigureAwait(false);
                if (snapshot.Text.Contains('\0')) return Refused("binary");
                var current = snapshot.Revision.ContentHash;
                if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) return Refused("read_only", current);
                // Another editor (the TUI, an agent, a text editor) changed the file since it was read.
                if (!request.Overwrite && !string.Equals(current, request.ExpectedRevision, StringComparison.Ordinal)) return Refused("conflict", current);
                if (snapshot.Encoding.GetPreamble().Length + (long)snapshot.Encoding.GetByteCount(request.Content) > MaximumFileBytes) return Refused("too_large");
                writing = true;
                // Not cancelable once it starts: the page must learn whether the file was replaced.
                var result = await _textFiles.SaveAsync(new TextFileSaveRequest(full, request.Content, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision),
                    CancellationToken.None).ConfigureAwait(false);
                writing = false;
                if (!result.IsConflict) return new("ok", path, result.CurrentRevision.ContentHash);
                // Changed between the read above and the replacement; nothing was written.
                if (!result.CurrentRevision.Exists) return Refused("not_found");
                if (!request.Overwrite || attempt == MaximumOverwriteAttempts) return Refused("conflict", result.CurrentRevision.ContentHash);
            }
        }
        catch (DecoderFallbackException)
        {
            return Refused("binary");
        }
        catch (EncoderFallbackException)
        {
            // The content is not valid Unicode (an unpaired surrogate).
            return Refused("invalid");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused(writing ? "write_failed" : "read_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks a requested path without touching the disk and returns <c>ok</c> with its forward-slash form,
    /// <c>outside_root</c> for a path that names something outside the project folder, or <c>invalid</c>.
    /// </summary>
    internal static string Normalize(string? path, out string? normalized)
    {
        normalized = null;
        if (path is not { Length: > 0 and <= MaximumPathLength } || path.Any(char.IsControl)) return "invalid";
        // A drive, a UNC or device path, and an alternate data stream of a file inside the folder.
        if (path[0] is '/' or '\\' || path.Contains(':') || Path.IsPathRooted(path)) return "outside_root";
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Contains("..")) return "outside_root";
        // Windows drops trailing dots and spaces of a name: such a segment would address another name.
        if (parts.Any(static part => part.Length == 0 || part != part.Trim() || part.EndsWith('.'))) return "invalid";
        if (OperatingSystem.IsWindows() && path.AsSpan().IndexOfAny("\"<>|*?") >= 0) return "invalid";
        normalized = string.Join('/', parts);
        return "ok";
    }

    // Resolves a normalized path to the file it names inside the folder: ok, outside_root or not_found.
    private static string Locate(string root, string relative, out string full)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var plain = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        full = Path.GetFullPath(plain);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        // A name the system resolves elsewhere (a device such as NUL or COM1) is not a file of the folder.
        if (!full.StartsWith(prefix, PathComparison) || !string.Equals(full, plain, PathComparison)) return "outside_root";
        // From the folder down, so that nothing beyond a link is examined.
        var current = root;
        var parts = relative.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return "not_found";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0) return "outside_root";
            // Every segment but the last is a folder, and the last is a file.
            if (((attributes & FileAttributes.Directory) != 0) == (index == parts.Length - 1)) return "not_found";
        }

        return "ok";
    }

    // Feeds the recent files of the project's file search; a failure never fails the read.
    private async Task RecordUsageAsync(string root, string path)
    {
        if (_search is null) return;
        try
        {
            await _search.RecordUsageAsync(new ProjectFileUsageEvent(root, path, ProjectFileSearchItemKind.File, DateTimeOffset.UtcNow,
                ProjectFileUsageAccessKind.EditorOpened), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static bool IsStorageFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}

/// <summary>Asks for the text of one file of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The file's path relative to the project folder, with forward or back slashes.</param>
internal sealed record ProjectFileReadRequest(string? ExpectedEpoch, string? ProjectId, string? Path);

/// <summary>
/// <c>ok</c> with the whole text, or one of <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>,
/// <c>unknown_project</c>, <c>archived_project</c>, <c>project_unavailable</c>, <c>outside_root</c>,
/// <c>not_found</c>, <c>too_large</c>, <c>binary</c> and <c>read_failed</c> with none.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Path">The requested path with forward slashes, or null when it was not a usable path.</param>
/// <param name="Content">The decoded text with its newlines unchanged.</param>
/// <param name="Revision">SHA-256 of the file's bytes in hexadecimal; a write names it as its expected revision.</param>
/// <param name="ReadOnly">Whether the file has the read-only attribute; a write would be refused.</param>
/// <param name="Length">The file's size in bytes; also set for <c>too_large</c>, where it stops at <see cref="int.MaxValue"/>.</param>
internal sealed record ProjectFileReadResponse(string Status, string? Path, string? Content, string? Revision, bool ReadOnly, int Length);

/// <summary>Asks to replace the text of one existing file of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The file's path relative to the project folder, with forward or back slashes.</param>
/// <param name="Content">The whole new text; its newlines are written as given.</param>
/// <param name="ExpectedRevision">The revision that was read; ignored when <paramref name="Overwrite"/> is set.</param>
/// <param name="Overwrite">Replaces the file whatever its current revision.</param>
internal sealed record ProjectFileWriteRequest(string? ExpectedEpoch, string? ProjectId, string? Path, string? Content, string? ExpectedRevision, bool Overwrite);

/// <summary>
/// <c>ok</c> with the new revision; <c>conflict</c> (and <c>read_only</c>) with the revision now on disk and
/// nothing written; otherwise a read refusal code or <c>write_failed</c>.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Path">The requested path with forward slashes, or null when it was not a usable path.</param>
/// <param name="Revision">The revision of the file after an accepted write, or the one found on disk.</param>
internal sealed record ProjectFileWriteResponse(string Status, string? Path, string? Revision);
