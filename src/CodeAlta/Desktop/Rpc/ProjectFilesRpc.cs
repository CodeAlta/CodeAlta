using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The files of a project for the desktop code editor: the entries of a folder, the text of a file with the
/// terminal editor's rules (its encoding, BOM and newlines are kept, and a file changed elsewhere since it was
/// read is reported as a conflict instead of being replaced), creating, renaming and deleting, images, and a
/// text search through the project.
/// </summary>
/// <remarks>
/// A file is addressed by a project id and a path relative to that project's folder, never by an absolute
/// path. The path must stay inside the folder: a rooted path, a drive or stream name, a parent segment and
/// any link (reparse point) between the folder and the entry are refused as <c>outside_root</c>. The folder of
/// the project itself is never renamed or deleted. Where a request names a project it can name the folder of a
/// source plugin (<see cref="PluginFolder"/>), of a skill (<see cref="SkillFolder"/>), or a folder of the disk
/// that the host opened for a link (<see cref="DiskFolders"/>) instead: the editor then works in that folder,
/// by the same rules. The folder of a built-in skill, or of a skill that a plugin brings, is only read: its
/// files are reported as read-only, and a change is answered as <c>read_only</c>.
/// </remarks>
[NeoRpcService("projectFiles", Version = 1)]
internal sealed class ProjectFilesService
{
    /// <summary>Largest file read or written, in bytes on disk (including a BOM).</summary>
    internal const int MaximumFileBytes = 1024 * 1024;

    /// <summary>Longest project-relative path accepted, in UTF-16 units.</summary>
    internal const int MaximumPathLength = 1024;

    /// <summary>Folders listed by one request.</summary>
    internal const int MaximumListedFolders = 256;

    /// <summary>Entries returned for one folder.</summary>
    internal const int MaximumFolderEntries = 5000;

    /// <summary>Files looked at by one <c>stat</c> request.</summary>
    internal const int MaximumStatFiles = 128;

    /// <summary>Largest image returned, in bytes.</summary>
    internal const int MaximumImageBytes = 16 * 1024 * 1024;

    // An overwrite reads the file for its encoding first; a writer racing that read gets this many tries.
    private const int MaximumOverwriteAttempts = 3;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly IProjectFileSearchService? _search;
    private readonly DesktopEditorView? _view;
    private readonly SkillFolders? _skills;
    private readonly DiskFolders? _folders;
    private readonly IDesktopFileTrash _trash;
    private readonly Func<string, bool> _reveal;
    private readonly TextFileCodec _textFiles = new();
    private readonly ProjectFileTree _tree = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Two searches at most read the disk together: a new one starts while the one it replaces winds down.
    private readonly SemaphoreSlim _searches = new(2, 2);

    private enum EntryKind
    {
        File,
        Directory,
        Any,
    }

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProjectFilesService()
    {
        _trash = new DesktopFileTrash();
        _reveal = DesktopFileReveal.Show;
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="search">Receives each file read as a recent file, or null to record nothing.</param>
    /// <param name="view">The requests of <c>alta editor open</c> for the window, or null when there are none.</param>
    /// <param name="trash">Where deleted entries go; the trash of the system by default.</param>
    /// <param name="reveal">Shows an entry in the file manager; the one of the system by default.</param>
    /// <param name="skills">Finds the folder of a skill that a request names, or null when no skill is edited.</param>
    /// <param name="folders">The folders of the disk that links opened, or null when no link opens one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProjectFilesService(ProjectCatalog projects, string epoch, IProjectFileSearchService? search = null, DesktopEditorView? view = null,
        IDesktopFileTrash? trash = null, Func<string, bool>? reveal = null, SkillFolders? skills = null, DiskFolders? folders = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _epoch = epoch;
        _search = search;
        _view = view;
        _skills = skills;
        _folders = folders;
        _trash = trash ?? new DesktopFileTrash();
        _reveal = reveal ?? DesktopFileReveal.Show;
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
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        try
        {
            var located = Locate(root, path!, EntryKind.File, out var full, out _);
            if (located != "ok") return Refused(located);
            // Checked before reading: a large file is never loaded to find out that it is too large.
            var info = new FileInfo(full);
            var onDisk = info.Length;
            if (onDisk > MaximumFileBytes) return Refused("too_large", onDisk);
            // Taken before the text: a change made while it is read shows as a newer stamp, never as an older text.
            var stamp = Stamp(info);
            var snapshot = await _textFiles.LoadAsync(full, cancellationToken).ConfigureAwait(false);
            // The size of what was actually read: the file may have grown since the check.
            var length = snapshot.Encoding.GetPreamble().Length + (long)snapshot.Encoding.GetByteCount(snapshot.Text);
            if (length > MaximumFileBytes) return Refused("too_large", length);
            if (snapshot.Text.Contains('\0')) return Refused("binary");
            var readOnly = ReadOnlyFolder(request.ProjectId) || (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0;
            // A file read again because it changed on disk was not opened again.
            if (!request.Reload) await RecordUsageAsync(root, path!).ConfigureAwait(false);
            return new("ok", path, snapshot.Text, snapshot.Revision.ContentHash, readOnly, (int)length, stamp, EncodingName(snapshot.Encoding, snapshot.HasByteOrderMark));
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
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writing = false;
        try
        {
            var located = Locate(root, path!, EntryKind.File, out var full, out _);
            if (located != "ok") return Refused(located);
            for (var attempt = 1; ; attempt++)
            {
                if (new FileInfo(full).Length > MaximumFileBytes) return Refused("too_large");
                // The file on disk decides the encoding and BOM of what replaces it.
                var snapshot = await _textFiles.LoadAsync(full, cancellationToken).ConfigureAwait(false);
                if (snapshot.Text.Contains('\0')) return Refused("binary");
                var current = snapshot.Revision.ContentHash;
                if (ReadOnlyFolder(request.ProjectId) || (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) return Refused("read_only", current);
                // Another editor (the TUI, an agent, a text editor) changed the file since it was read.
                if (!request.Overwrite && !string.Equals(current, request.ExpectedRevision, StringComparison.Ordinal)) return Refused("conflict", current);
                if (snapshot.Encoding.GetPreamble().Length + (long)snapshot.Encoding.GetByteCount(request.Content) > MaximumFileBytes) return Refused("too_large");
                writing = true;
                // Not cancelable once it starts: the page must learn whether the file was replaced.
                var result = await _textFiles.SaveAsync(new TextFileSaveRequest(full, request.Content, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision),
                    CancellationToken.None).ConfigureAwait(false);
                writing = false;
                if (!result.IsConflict) return new("ok", path, result.CurrentRevision.ContentHash, StampOf(full));
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
    /// Lists the entries of folders of a project, each folder on its own and nothing below it: what git ignores
    /// is left out, or marked when <c>IncludeIgnored</c> is set. A folder whose entries are the ones the page
    /// already has (<c>KnownRevision</c>) is answered as <c>unchanged</c>.
    /// </summary>
    [NeoRpcMethod("list")]
    public async Task<ProjectFileListResponse> ListAsync(ProjectFileListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProjectFileListResponse Refused(string status) => new(status, request.ProjectId, [], false, false);
        var (status, root) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (root is null) return Refused(status);
        if (request.Folders is not { Count: > 0 and <= MaximumListedFolders } folders || folders.Any(static folder => folder is null)) return Refused("invalid");
        var includeIgnored = request.IncludeIgnored;
        var listed = await Task.Run(() =>
        {
            var result = new ProjectFileFolder[folders.Count];
            for (var index = 0; index < result.Length; index++) result[index] = ListFolder(root, folders[index], includeIgnored, cancellationToken);
            return result;
        }, cancellationToken).ConfigureAwait(false);
        return new("ok", request.ProjectId, listed, _trash.Available, DesktopFileReveal.Available);
    }

    /// <summary>
    /// Returns the stamp (size and last write time) of files of a project, which changes when a file does. The
    /// editor compares it with the stamp of what it read to notice a file changed by something else.
    /// </summary>
    [NeoRpcMethod("stat")]
    public async Task<ProjectFileStatResponse> StatAsync(ProjectFileStatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProjectFileStatResponse Refused(string status) => new(status, request.ProjectId, []);
        var (status, root) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (root is null) return Refused(status);
        if (request.Paths is not { Count: > 0 and <= MaximumStatFiles } paths) return Refused("invalid");
        var readOnly = ReadOnlyFolder(request.ProjectId);
        var files = await Task.Run(() =>
        {
            var result = new ProjectFileStat[paths.Count];
            for (var index = 0; index < result.Length; index++) result[index] = Stat(root, paths[index], readOnly);
            return result;
        }, cancellationToken).ConfigureAwait(false);
        return new("ok", request.ProjectId, files);
    }

    /// <summary>
    /// Creates an empty file or a folder, with the folders above it that do not exist yet. Nothing that exists
    /// is replaced: a name already taken is answered as <c>exists</c>.
    /// </summary>
    [NeoRpcMethod("create")]
    public async Task<ProjectFileChangeResponse> CreateAsync(ProjectFileCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileChangeResponse Refused(string status) => new(status, path);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null) return Refused("invalid");
        var named = Normalize(request.Path, out path);
        if (named != "ok") return Refused(named);
        if (ReadOnlyFolder(request.ProjectId)) return Refused("read_only");
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var located = LocateNew(root, path!, out var full);
            if (located != "ok") return Refused(located);
            if (request.Directory) Directory.CreateDirectory(full);
            else if (!await _textFiles.TryCreateAsync(full, string.Empty, CancellationToken.None).ConfigureAwait(false)) return Refused("exists");
            return new("ok", path);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("write_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renames or moves a file or a folder inside the project. Nothing that exists is replaced: a name already
    /// taken is answered as <c>exists</c>, except when the new name is the old one in another case.
    /// </summary>
    [NeoRpcMethod("rename")]
    public async Task<ProjectFileChangeResponse> RenameAsync(ProjectFileRenameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileChangeResponse Refused(string status) => new(status, path);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null) return Refused("invalid");
        var named = Normalize(request.Path, out var source);
        if (named != "ok") return Refused(named);
        named = Normalize(request.NewPath, out path);
        if (named != "ok") return Refused(named);
        if (ReadOnlyFolder(request.ProjectId)) return Refused("read_only");
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var from = string.Empty;
        var to = string.Empty;
        try
        {
            var located = Locate(root, source!, EntryKind.Any, out from, out var isDirectory);
            if (located != "ok") return Refused(located);
            if (string.Equals(source, path, StringComparison.Ordinal)) return new("ok", path);
            // A folder cannot be moved into itself.
            if (isDirectory && path!.StartsWith(source + "/", PathComparison)) return Refused("invalid");
            // The same name in another case is the entry itself where names do not compare case.
            var caseOnly = string.Equals(source, path, StringComparison.OrdinalIgnoreCase);
            var target = LocateNew(root, path!, out to);
            if (target != "ok" && !(target == "exists" && caseOnly)) return Refused(target);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (isDirectory) Directory.Move(from, to);
            else File.Move(from, to, overwrite: false);
            return new("ok", path);
        }
        catch (IOException) when (from.Length > 0 && to.Length > 0 && EntryExists(from) && EntryExists(to))
        {
            // Both are still there: the new name belongs to something else.
            return Refused("exists");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("write_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes a file or a folder with all it holds: moved to the trash of the system, or, with
    /// <c>Permanent</c>, removed for good. Without a trash the first is answered as <c>trash_unavailable</c>,
    /// and nothing is ever removed for good that was not asked to be.
    /// </summary>
    [NeoRpcMethod("delete", TimeoutMilliseconds = 120_000)]
    public async Task<ProjectFileChangeResponse> DeleteAsync(ProjectFileDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileChangeResponse Refused(string status) => new(status, path);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null) return Refused("invalid");
        var named = Normalize(request.Path, out path);
        if (named != "ok") return Refused(named);
        if (ReadOnlyFolder(request.ProjectId)) return Refused("read_only");
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        try
        {
            if (!request.Permanent)
            {
                if (!_trash.Available) return Refused("trash_unavailable");
                var moved = Locate(root, path!, EntryKind.Any, out var entry, out _);
                if (moved != "ok") return Refused(moved);
                // Not under the gate: the system may ask a question before it moves something.
                return await _trash.MoveAsync(entry, cancellationToken).ConfigureAwait(false) ? new("ok", path) : Refused("trash_failed");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var located = Locate(root, path!, EntryKind.Any, out var full, out var isDirectory);
                if (located != "ok") return Refused(located);
                if (isDirectory) Directory.Delete(full, recursive: true);
                else File.Delete(full);
                return new("ok", path);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("write_failed");
        }
    }

    /// <summary>Reads an image file of a project (PNG, JPEG, GIF, WebP, BMP, ICO or AVIF, by its content) as base64.</summary>
    [NeoRpcMethod("image", TimeoutMilliseconds = 60_000)]
    public async Task<ProjectFileImageResponse> ImageAsync(ProjectFileImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileImageResponse Refused(string status, long length = 0) => new(status, path, null, null, (int)Math.Min(length, int.MaxValue), null);
        if (_projects is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is null) return Refused("invalid");
        var named = Normalize(request.Path, out path);
        if (named != "ok") return Refused(named);
        var project = await RootAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        try
        {
            var located = Locate(root, path!, EntryKind.File, out var full, out _);
            if (located != "ok") return Refused(located);
            var info = new FileInfo(full);
            if (info.Length > MaximumImageBytes) return Refused("too_large", info.Length);
            var stamp = Stamp(info);
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumImageBytes) return Refused("too_large", bytes.Length);
            if (ImageMediaType(bytes) is not { } mediaType) return Refused("unsupported_type", bytes.Length);
            if (!request.Reload) await RecordUsageAsync(root, path!).ConfigureAwait(false);
            return new("ok", path, mediaType, Convert.ToBase64String(bytes), bytes.Length, stamp);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("read_failed");
        }
    }

    /// <summary>Shows a file or a folder of a project (the project folder itself for an empty path) in the system's file manager.</summary>
    [NeoRpcMethod("reveal")]
    public async Task<ProjectFileChangeResponse> RevealAsync(ProjectFileRevealRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = null;
        ProjectFileChangeResponse Refused(string status) => new(status, path);
        var (status, root) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (root is null) return Refused(status);
        var full = root;
        if (request.Path is { Length: > 0 })
        {
            var named = Normalize(request.Path, out path);
            if (named != "ok") return Refused(named);
            try
            {
                var located = Locate(root, path!, EntryKind.Any, out full, out _);
                if (located != "ok") return Refused(located);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return Refused("read_failed");
            }
        }
        else
        {
            path = string.Empty;
        }

        return _reveal(full) ? new("ok", path) : Refused("failed");
    }

    /// <summary>
    /// Searches the text files of a project for a text or a regular expression, line by line. The files are
    /// those the XenoAtom.Glob scanner walks (what git ignores is left out), narrowed by the include and exclude
    /// globs. One <c>file</c> event is sent for each file with matches as it is found, then one <c>done</c> event
    /// with the totals, or with why the search could not run.
    /// </summary>
    [NeoRpcMethod("search")]
    public NeoRpcChannel<ProjectFileSearchEvent> Search(ProjectFileSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(SearchAsync(request, cancellationToken), DesktopJsonContext.Default.ProjectFileSearchEvent);
    }

    internal async IAsyncEnumerable<ProjectFileSearchEvent> SearchAsync(ProjectFileSearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        static ProjectFileSearchEvent Ended(string status, int files = 0, int matches = 0, bool truncated = false) => new("done", status, null, null, files, matches, truncated);
        var (opened, root) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            yield return Ended(opened);
            yield break;
        }

        var created = ProjectFileSearch.TryCreate(request.Query, request.Regex, request.MatchCase, request.WholeWord, request.Include, request.Exclude, out var query);
        if (query is null)
        {
            yield return Ended(created);
            yield break;
        }

        var results = Channel.CreateBounded<ProjectFileSearchEvent>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        // The files are read on another thread; a page that stops reading stops the search.
        var producer = Task.Run(async () =>
        {
            int files = 0, matches = 0;
            var truncated = false;
            var entered = false;
            try
            {
                await _searches.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                foreach (var file in _tree.EnumerateFiles(root, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Length is 0 or > ProjectFileSearch.MaximumFileBytes || !ProjectFileSearch.Selects(query, file.RelativePath)) continue;
                    string? text;
                    try
                    {
                        text = ProjectGitChanges.DecodeText(await File.ReadAllBytesAsync(file.FullPath, cancellationToken).ConfigureAwait(false));
                    }
                    catch (Exception exception) when (IsStorageFailure(exception))
                    {
                        continue; // A file that cannot be read has no match.
                    }

                    if (text is null) continue;
                    var found = ProjectFileSearch.Find(query, text, Math.Min(ProjectFileSearch.MaximumFileMatches, ProjectFileSearch.MaximumMatches - matches), out var more);
                    if (found.Count == 0) continue;
                    files++;
                    matches += found.Count;
                    await results.Writer.WriteAsync(new("file", "ok", file.RelativePath, found, files, matches, more), cancellationToken).ConfigureAwait(false);
                    if (matches >= ProjectFileSearch.MaximumMatches || files >= ProjectFileSearch.MaximumFiles)
                    {
                        truncated = true;
                        break;
                    }
                }

                await results.Writer.WriteAsync(Ended("ok", files, matches, truncated), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The page asked for something else.
            }
            catch (RegexMatchTimeoutException)
            {
                results.Writer.TryWrite(Ended("timeout", files, matches, true));
            }
            catch (Exception exception) when (IsStorageFailure(exception) || exception is InvalidOperationException)
            {
                results.Writer.TryWrite(Ended("read_failed", files, matches, true));
            }
            finally
            {
                if (entered) _searches.Release();
                results.Writer.TryComplete();
            }
        }, CancellationToken.None);
        await foreach (var value in results.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // What was found before the page stopped reading is not sent after it did.
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }

        await producer.ConfigureAwait(false);
    }

    /// <summary>The requests of <c>alta editor open</c>, as they are made.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<ProjectFileShowEvent> Watch(ProjectFileWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.ProjectFileShowEvent);
    }

    internal async IAsyncEnumerable<ProjectFileShowEvent> WatchAsync(ProjectFileWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_view is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // A page that does not read keeps the newest requests.
        var requests = Channel.CreateBounded<ProjectFileShowEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _view.Watch(value => requests.Writer.TryWrite(value));
        await foreach (var value in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
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

    /// <summary>The media type of an image by the first bytes of its content, or null for anything else.</summary>
    internal static string? ImageMediaType(ReadOnlySpan<byte> content) => content switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x00, 0x00, 0x01, 0x00, ..] => "image/x-icon",
        [_, _, _, _, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 or 0x73, ..] => "image/avif",
        _ => null,
    };

    // The folder of the project a request names, or why it cannot be answered.
    private async Task<(string Status, string? Root)> OpenAsync(string? expectedEpoch, string? projectId, CancellationToken cancellationToken)
    {
        if (_projects is null) return ("unavailable", null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null);
        if (projectId is null) return ("invalid", null);
        var project = await RootAsync(projectId, cancellationToken).ConfigureAwait(false);
        return (project.Status, project.Root);
    }

    /// <summary>
    /// The folder an id names: the one of a project of the catalog, of a source plugin, of a skill, or a folder
    /// of the disk that a link opened. Returns <c>ok</c> with the folder, or why it has none.
    /// </summary>
    internal Task<(string Status, string? Root)> RootAsync(string projectId, CancellationToken cancellationToken)
        => _projects is null ? Task.FromResult<(string Status, string? Root)>(("unavailable", null))
            : PluginFolder.TryParse(projectId, out var folder) ? folder.ResolveAsync(_projects, cancellationToken)
            : SkillFolder.TryParse(projectId, out var skill)
                ? _skills?.ResolveAsync(skill, cancellationToken) ?? Task.FromResult<(string Status, string? Root)>(("unknown_project", null))
            : DiskFolders.IsId(projectId) ? Task.FromResult(_folders?.Resolve(projectId) ?? ("unknown_project", null))
            : SettingsProjectScope.ResolveAsync(_projects, projectId, cancellationToken);

    // Whether a request names a folder that is only read: nothing is created, changed or removed in it.
    private static bool ReadOnlyFolder(string? projectId) => SkillFolder.TryParse(projectId, out var skill) && skill.ReadOnly;

    private ProjectFileFolder ListFolder(string root, ProjectFileFolderQuery query, bool includeIgnored, CancellationToken cancellationToken)
    {
        var path = query.Path ?? string.Empty;
        ProjectFileFolder Refused(string status) => new(path, status, null, [], false);
        var folder = string.Empty;
        if (path.Length > 0)
        {
            var named = Normalize(path, out var normalized);
            if (named != "ok") return Refused(named);
            path = folder = normalized!;
        }

        try
        {
            if (folder.Length > 0)
            {
                var located = Locate(root, folder, EntryKind.Directory, out _, out _);
                if (located != "ok") return Refused(located);
            }

            var listing = _tree.ListFolder(root, folder, includeIgnored, MaximumFolderEntries, cancellationToken);
            var entries = new ProjectFileEntry[listing.Entries.Count];
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = listing.Entries[index];
                entries[index] = new(entry.Name, entry.IsDirectory, entry.IsIgnored);
            }

            var revision = FolderRevision(entries, listing.Truncated);
            return string.Equals(revision, query.KnownRevision, StringComparison.Ordinal)
                ? new(path, "unchanged", revision, [], false)
                : new(path, "ok", revision, entries, listing.Truncated);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Refused("not_found");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Refused("read_failed");
        }
    }

    // A short name for the entries of a folder, which changes when one is added, removed, renamed or ignored.
    private static string FolderRevision(ProjectFileEntry[] entries, bool truncated)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> mark = stackalloc byte[2];
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Name));
            mark[0] = 0;
            mark[1] = (byte)((entry.Directory ? 1 : 0) | (entry.Ignored ? 2 : 0));
            hash.AppendData(mark);
        }

        mark[0] = (byte)(truncated ? 1 : 0);
        hash.AppendData(mark[..1]);
        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8));
    }

    private static ProjectFileStat Stat(string root, string? requested, bool readOnlyFolder)
    {
        var named = Normalize(requested, out var path);
        if (named != "ok") return new(requested ?? string.Empty, named, null, 0, false);
        try
        {
            var located = Locate(root, path!, EntryKind.File, out var full, out _);
            if (located != "ok") return new(path!, located, null, 0, false);
            var info = new FileInfo(full);
            return new(path!, "ok", Stamp(info), (int)Math.Min(info.Length, int.MaxValue), readOnlyFolder || (info.Attributes & FileAttributes.ReadOnly) != 0);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(path!, "not_found", null, 0, false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(path!, "read_failed", null, 0, false);
        }
    }

    // The size and last write time of a file: enough to tell that it changed, without reading it.
    private static string Stamp(FileInfo file) => $"{file.Length:x}:{file.LastWriteTimeUtc.Ticks:x}";

    private static string? StampOf(string full)
    {
        try
        {
            var file = new FileInfo(full);
            return file.Exists ? Stamp(file) : null;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return null;
        }
    }

    private static string EncodingName(Encoding encoding, bool byteOrderMark) => encoding.CodePage switch
    {
        65001 => byteOrderMark ? "UTF-8 BOM" : "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32 LE",
        12001 => "UTF-32 BE",
        _ => encoding.WebName,
    };

    // Resolves a normalized path to the entry it names inside the folder: ok, outside_root or not_found.
    private static string Locate(string root, string relative, EntryKind kind, out string full, out bool isDirectory)
    {
        isDirectory = false;
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
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            // Every segment but the last is a folder; the last is what was asked for.
            if (index < parts.Length - 1 ? !isDirectory : kind == EntryKind.File ? isDirectory : kind == EntryKind.Directory && !isDirectory) return "not_found";
        }

        return "ok";
    }

    // Resolves a normalized path that is about to be created: ok when nothing has that name and every folder
    // above it that exists is a plain folder; otherwise exists, outside_root, or not_found for a file where a
    // folder is needed.
    private static string LocateNew(string root, string relative, out string full)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var plain = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        full = Path.GetFullPath(plain);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison) || !string.Equals(full, plain, PathComparison)) return "outside_root";
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
                return "ok"; // Nothing exists from here down.
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0) return "outside_root";
            if (index == parts.Length - 1) return "exists";
            if ((attributes & FileAttributes.Directory) == 0) return "not_found";
        }

        return "exists";
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
/// <param name="Reload">The file is read again because it changed: it is not recorded as a recently opened file.</param>
internal sealed record ProjectFileReadRequest(string? ExpectedEpoch, string? ProjectId, string? Path, bool Reload = false);

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
/// <param name="Stamp">The file's stamp when it was read, as <c>stat</c> returns it.</param>
/// <param name="Encoding">The encoding the text was read with, for display: <c>UTF-8</c>, <c>UTF-8 BOM</c>, <c>UTF-16 LE</c>…</param>
internal sealed record ProjectFileReadResponse(string Status, string? Path, string? Content, string? Revision, bool ReadOnly, int Length, string? Stamp = null, string? Encoding = null);

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
/// <param name="Stamp">The file's stamp after an accepted write, as <c>stat</c> returns it.</param>
internal sealed record ProjectFileWriteResponse(string Status, string? Path, string? Revision, string? Stamp = null);

/// <summary>One folder to list.</summary>
/// <param name="Path">The folder's path relative to the project folder; empty or null for the project folder itself.</param>
/// <param name="KnownRevision">The revision of the entries the page already has for it, if any.</param>
internal sealed record ProjectFileFolderQuery(string? Path, string? KnownRevision);

/// <summary>Asks for the entries of folders of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Folders">The folders, at most <see cref="ProjectFilesService.MaximumListedFolders"/>.</param>
/// <param name="IncludeIgnored">Returns what git ignores too, marked as ignored.</param>
internal sealed record ProjectFileListRequest(string? ExpectedEpoch, string? ProjectId, IReadOnlyList<ProjectFileFolderQuery>? Folders, bool IncludeIgnored);

/// <summary>One file or folder of a listed folder.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Directory">Whether it is a folder.</param>
/// <param name="Ignored">Whether git ignores it; only ever set when the ignored entries were asked for.</param>
internal sealed record ProjectFileEntry(string Name, bool Directory, bool Ignored);

/// <summary>The entries of one folder, in the order of the request.</summary>
/// <param name="Path">The folder's path with forward slashes, as asked when it was not a usable path.</param>
/// <param name="Status"><c>ok</c>, <c>unchanged</c>, <c>not_found</c>, <c>outside_root</c>, <c>invalid</c> or <c>read_failed</c>.</param>
/// <param name="Revision">A name for the entries, sent back as <c>KnownRevision</c>.</param>
/// <param name="Entries">Folders first, then files, by name; empty unless <c>ok</c>.</param>
/// <param name="Truncated">The folder holds more than <see cref="ProjectFilesService.MaximumFolderEntries"/> entries.</param>
internal sealed record ProjectFileFolder(string Path, string Status, string? Revision, IReadOnlyList<ProjectFileEntry> Entries, bool Truncated);

/// <summary><c>ok</c> with one answer per folder, or why none could be listed.</summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project that was asked for.</param>
/// <param name="Folders">One answer for each folder of the request, in its order.</param>
/// <param name="Trash">Whether a deleted entry can be moved to the trash of the system.</param>
/// <param name="Reveal">Whether an entry can be shown in the file manager of the system.</param>
internal sealed record ProjectFileListResponse(string Status, string? ProjectId, IReadOnlyList<ProjectFileFolder> Folders, bool Trash, bool Reveal);

/// <summary>Asks for the stamps of files of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Paths">The files, at most <see cref="ProjectFilesService.MaximumStatFiles"/>.</param>
internal sealed record ProjectFileStatRequest(string? ExpectedEpoch, string? ProjectId, IReadOnlyList<string>? Paths);

/// <summary>What is known of one file without reading it.</summary>
/// <param name="Path">The file's path with forward slashes, as asked when it was not a usable path.</param>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>outside_root</c>, <c>invalid</c> or <c>read_failed</c>.</param>
/// <param name="Stamp">Its size and last write time as one text; it changes when the file does.</param>
/// <param name="Length">Its size in bytes, stopping at <see cref="int.MaxValue"/>.</param>
/// <param name="ReadOnly">Whether it has the read-only attribute.</param>
internal sealed record ProjectFileStat(string Path, string Status, string? Stamp, int Length, bool ReadOnly);

/// <summary><c>ok</c> with one answer per file, in the order of the request, or why none could be looked at.</summary>
internal sealed record ProjectFileStatResponse(string Status, string? ProjectId, IReadOnlyList<ProjectFileStat> Files);

/// <summary>Asks to create an empty file or a folder.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The new entry's path relative to the project folder; folders above it are created as needed.</param>
/// <param name="Directory">Creates a folder instead of a file.</param>
internal sealed record ProjectFileCreateRequest(string? ExpectedEpoch, string? ProjectId, string? Path, bool Directory);

/// <summary>Asks to rename or move a file or a folder.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The entry's path relative to the project folder.</param>
/// <param name="NewPath">Its new path relative to the project folder.</param>
internal sealed record ProjectFileRenameRequest(string? ExpectedEpoch, string? ProjectId, string? Path, string? NewPath);

/// <summary>Asks to delete a file or a folder.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The entry's path relative to the project folder.</param>
/// <param name="Permanent">Removes it for good instead of moving it to the trash of the system.</param>
internal sealed record ProjectFileDeleteRequest(string? ExpectedEpoch, string? ProjectId, string? Path, bool Permanent);

/// <summary>
/// <c>ok</c>, or why nothing changed: a read refusal code, <c>exists</c>, <c>write_failed</c>, and for a
/// deletion <c>trash_unavailable</c> or <c>trash_failed</c>; <c>failed</c> for an entry that could not be shown.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Path">The path of the entry after the change, with forward slashes; null when it was not a usable path.</param>
internal sealed record ProjectFileChangeResponse(string Status, string? Path);

/// <summary>Asks for one image file of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The file's path relative to the project folder.</param>
/// <param name="Reload">The image is read again because it changed: it is not recorded as a recently opened file.</param>
internal sealed record ProjectFileImageRequest(string? ExpectedEpoch, string? ProjectId, string? Path, bool Reload = false);

/// <summary><c>ok</c> with the image, or a read refusal code, <c>too_large</c> or <c>unsupported_type</c>.</summary>
/// <param name="Status">The outcome code.</param>
/// <param name="Path">The requested path with forward slashes, or null when it was not a usable path.</param>
/// <param name="MediaType">The media type found in the file's content.</param>
/// <param name="Base64">The file's bytes.</param>
/// <param name="Length">The file's size in bytes.</param>
/// <param name="Stamp">The file's stamp when it was read, as <c>stat</c> returns it.</param>
internal sealed record ProjectFileImageResponse(string Status, string? Path, string? MediaType, string? Base64, int Length, string? Stamp);

/// <summary>Asks to show an entry of a project in the system's file manager.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Path">The entry's path relative to the project folder; empty or null for the project folder itself.</param>
internal sealed record ProjectFileRevealRequest(string? ExpectedEpoch, string? ProjectId, string? Path);

/// <summary>Asks to search the text files of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Query">The text, or the regular expression, to look for inside a line.</param>
/// <param name="Regex">Reads <paramref name="Query"/> as a regular expression.</param>
/// <param name="MatchCase">Compares case.</param>
/// <param name="WholeWord">Matches whole words only.</param>
/// <param name="Include">Globs separated by commas: only the files they match are searched.</param>
/// <param name="Exclude">Globs separated by commas: the files they match are skipped.</param>
internal sealed record ProjectFileSearchRequest(string? ExpectedEpoch, string? ProjectId, string? Query, bool Regex, bool MatchCase, bool WholeWord, string? Include, string? Exclude);

/// <summary>What a search found in one file, or how it ended.</summary>
/// <param name="Kind"><c>file</c> for the matches of one file, <c>done</c> for the end of the search.</param>
/// <param name="Status">
/// <c>ok</c>, or with <c>done</c> why the search did not complete: a read refusal code, <c>invalid</c>,
/// <c>invalid_pattern</c>, <c>invalid_glob</c>, <c>timeout</c> or <c>read_failed</c>.
/// </param>
/// <param name="Path">The file's path relative to the project folder.</param>
/// <param name="Matches">The matches in the file, in the order of its lines.</param>
/// <param name="Files">The number of files with matches so far.</param>
/// <param name="MatchCount">The number of matches so far.</param>
/// <param name="Truncated">With <c>file</c>, the file holds more matches; with <c>done</c>, the search stopped at its limits.</param>
internal sealed record ProjectFileSearchEvent(string Kind, string Status, string? Path, IReadOnlyList<ProjectFileSearchMatch>? Matches, int Files, int MatchCount, bool Truncated);

/// <summary>Asks for the requests of <c>alta editor open</c>.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record ProjectFileWatchRequest(string? ExpectedEpoch);

/// <summary>
/// A request to open the code editor of a project, of the folder of a source plugin, or of a folder of the disk
/// that is no project.
/// </summary>
/// <param name="ProjectId">The project, or the id of the folder.</param>
/// <param name="Path">The file to open, relative to the project folder; null to show the project's files.</param>
/// <param name="Line">The 1-based line to go to.</param>
/// <param name="Column">The 1-based column on that line.</param>
/// <param name="Name">For a folder, the name its tab shows; null for a project.</param>
/// <param name="Root">For a folder, its path, which the tab shows; null for a project.</param>
internal sealed record ProjectFileShowEvent(string ProjectId, string? Path, int? Line, int? Column, string? Name = null, string? Root = null);
