using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop;

/// <summary>A picture that a link names, read for the window to draw it.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>not_image</c>, <c>too_large</c> or <c>failed</c>.</param>
/// <param name="MediaType">The media type of its content; null unless <c>ok</c>.</param>
/// <param name="Bytes">Its content; null unless <c>ok</c>.</param>
/// <param name="FullPath">The full path of the file; null unless <c>ok</c>.</param>
internal sealed record DesktopLinkedImage(string Status, string? MediaType = null, byte[]? Bytes = null, string? FullPath = null);

/// <summary>
/// Follows a link to a file of the disk: finds the file, and has the window show it in the code editor.
/// </summary>
/// <remarks>
/// <para>
/// A file inside the folder of a project opens in the code editor of that project. Any other file opens the
/// code editor on a folder that is no project (<see cref="DiskFolders"/>): the folder a session works in when
/// the file is in it, its git worktree for example, and the folder of the file otherwise. A folder opens with
/// its files shown.
/// </para>
/// <para>
/// A file that is not text is not opened: the answer is <c>binary</c>. A picture is, because the code editor
/// shows it. A <c>file:</c> address of a document that a browser shows (an HTML page, a PDF) is handed to the
/// system instead, as an address of the web is: no other kind of file is, because the system would start a
/// program for it.
/// </para>
/// </remarks>
internal sealed class DesktopFileLinks
{
    // What is read of a file to tell text from anything else.
    private const int SniffedBytes = 8192;

    private readonly ProjectCatalog _projects;
    private readonly DiskFolders _folders;
    private readonly DesktopEditorView _view;
    private readonly Func<string, CancellationToken, ValueTask<string?>> _sessionFolder;
    private readonly Func<string, CancellationToken, Task<(string Status, string? Root)>> _folder;
    private readonly Func<string, bool> _openDocument;
    private readonly string _home;

    /// <summary>Creates the links of a host.</summary>
    /// <param name="projects">The project catalog of the host.</param>
    /// <param name="folders">Gives an id to a folder that is no project.</param>
    /// <param name="view">Passes the file to show to the window.</param>
    /// <param name="sessionFolder">The folder a session works in, or null for an unknown session.</param>
    /// <param name="folder">The folder that the id of a project or of a folder names, as the code editor finds it.</param>
    /// <param name="openDocument">Hands the path of a document to the system; false when it could not.</param>
    /// <param name="home">The home folder of the user, which a path starting with <c>~</c> names.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopFileLinks(ProjectCatalog projects, DiskFolders folders, DesktopEditorView view,
        Func<string, CancellationToken, ValueTask<string?>> sessionFolder,
        Func<string, CancellationToken, Task<(string Status, string? Root)>> folder, Func<string, bool> openDocument, string home)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(sessionFolder);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(openDocument);
        ArgumentNullException.ThrowIfNull(home);
        (_projects, _folders, _view, _sessionFolder, _folder, _openDocument, _home) = (projects, folders, view, sessionFolder, folder, openDocument, home);
    }

    /// <summary>
    /// Shows a file that is a page of the user guide of the application in the Documentation tab, at a heading of
    /// it: true when the file is such a page and the window was asked. Null where the guide is not shown.
    /// </summary>
    internal Func<string, string?, bool>? GuidePage { get; init; }

    /// <summary>Whether the system shows a file in a browser, by its name: an HTML page or a PDF.</summary>
    internal static bool IsDocument(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".pdf";

    /// <summary>Whether the code editor shows a file as a picture, by its name.</summary>
    internal static bool IsPicture(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".avif";

    /// <summary>
    /// Follows a link and returns <c>ok</c>, <c>not_found</c> for a file that is not there (or a relative path
    /// with no folder to start from), <c>binary</c> for a file that is not text, or <c>failed</c> when the window
    /// or the system could not show it.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal Task<string> OpenAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
        => OpenAsync(link, sessionId, projectId, directory, null, cancellationToken);

    /// <inheritdoc cref="OpenAsync(FileLink, string?, string?, string?, CancellationToken)"/>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="originLabel">The label of the view of the window where the link was followed: a code editor that no window shows opens there.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    internal async Task<string> OpenAsync(FileLink link, string? sessionId, string? projectId, string? directory, string? originLabel, CancellationToken cancellationToken)
    {
        var (full, sessionRoot) = await ResolveAsync(link, sessionId, projectId, directory, cancellationToken).ConfigureAwait(false);
        if (full is null) return "not_found";
        bool isDirectory;
        try
        {
            isDirectory = (File.GetAttributes(full) & FileAttributes.Directory) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "not_found";
        }

        if (!isDirectory)
        {
            // A page of the user guide is read in the Documentation tab, where its pictures and its links work.
            if (GuidePage is { } guide && guide(full, link.Anchor)) return "ok";
            if (link.IsAddress && IsDocument(full)) return _openDocument(full) ? "ok" : "failed";
            if (!IsPicture(full) && await IsBinaryAsync(full, cancellationToken).ConfigureAwait(false)) return "binary";
        }

        if (await ProjectOfAsync(full, cancellationToken).ConfigureAwait(false) is { } project)
        {
            var inside = Relative(project.Root, full);
            // The folder of the project, or a folder in it: its files are shown.
            if (inside is null || isDirectory) return _view.Open(project.Id, null, null, null, sessionId, originLabel) ? "ok" : "failed";
            if (ProjectFilesService.Normalize(inside, out var file) == "ok") return _view.Open(project.Id, file, link.Line, link.Column, sessionId, originLabel) ? "ok" : "failed";
        }

        if (isDirectory) return _view.OpenFolder(_folders.Give(full), null, null, null, sessionId, originLabel) ? "ok" : "failed";
        // The folder the session works in when the file is in it: a worktree keeps its own files together.
        if (sessionRoot is not null && Path.IsPathFullyQualified(sessionRoot) && GitWorktreeService.IsWithin(full, sessionRoot)
            && Relative(sessionRoot, full) is { } below && ProjectFilesService.Normalize(below, out var worked) == "ok")
        {
            return _view.OpenFolder(_folders.Give(sessionRoot), worked, link.Line, link.Column, sessionId, originLabel) ? "ok" : "failed";
        }

        if (Path.GetDirectoryName(full) is not { Length: > 0 } parent || ProjectFilesService.Normalize(Path.GetFileName(full), out var name) != "ok") return "not_found";
        return _view.OpenFolder(_folders.Give(parent), name, link.Line, link.Column, sessionId, originLabel) ? "ok" : "failed";
    }

    /// <summary>
    /// Reads a picture that a link names, for the window to draw it: <c>ok</c> with its media type and its bytes,
    /// <c>not_found</c>, <c>not_image</c> for a file that is no PNG, JPEG, GIF, WebP or BMP picture by its name and
    /// by its content, <c>too_large</c> past <see cref="ProjectFilesService.MaximumImageBytes"/>, or <c>failed</c>.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<DesktopLinkedImage> ReadImageAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        var (full, _) = await ResolveAsync(link, sessionId, projectId, directory, cancellationToken).ConfigureAwait(false);
        if (full is null) return new("not_found");
        if (Path.GetExtension(full).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) return new("not_image");
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return new("not_found");
            if (info.Length > ProjectFilesService.MaximumImageBytes) return new("too_large");
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > ProjectFilesService.MaximumImageBytes) return new("too_large");
            // The content decides: a file named as a picture may be anything else.
            return ProjectFilesService.ImageMediaType(bytes) is { } mediaType and ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp")
                ? new("ok", mediaType, bytes, full) : new("not_image");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("not_found");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new("failed");
        }
    }

    /// <summary>
    /// Reads a data file that a chart of the Markdown names (<see cref="ChartDataFiles"/>): below the folder a session works in,
    /// for a message of a session; else below the folder of a project, from the folder of the document, for a document.
    /// </summary>
    /// <param name="path">The path as the chart writes it.</param>
    /// <param name="sessionId">The session whose message has the chart: its folder is the one the file is read in.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the chart.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The text of the file, or why it was not read (<c>no_project</c> where there is no folder to read in).</returns>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<ChartDataRead> ReadChartDataAsync(string? path, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        if (sessionId is { Length: > 0 })
        {
            var folder = sessionId.Length <= 128 ? await _sessionFolder(sessionId, cancellationToken).ConfigureAwait(false) : null;
            return await ChartDataFiles.ReadAsync(folder, null, path, cancellationToken).ConfigureAwait(false);
        }

        if (projectId is not { Length: > 0 and <= 512 }) return new("no_project");
        var (status, root) = await _folder(projectId, cancellationToken).ConfigureAwait(false);
        return await ChartDataFiles.ReadAsync(status == "ok" ? root : null, directory, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the file that a link names in the file manager of the system: <c>ok</c>, <c>not_found</c>, or
    /// <c>failed</c> when the file manager could not be started.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<string> RevealAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        var (full, _) = await ResolveAsync(link, sessionId, projectId, directory, cancellationToken).ConfigureAwait(false);
        if (full is null || !Path.Exists(full)) return "not_found";
        cancellationToken.ThrowIfCancellationRequested();
        return Reveal(full) ? "ok" : "failed";
    }

    /// <summary>Shows a file in the file manager of the system; false when it could not. Tests replace it.</summary>
    internal Func<string, bool> Reveal { get; init; } = DesktopFileReveal.Show;

    /// <summary>The editors of the machine that <see cref="OpenExternalAsync"/> opens in; null where there are none.</summary>
    internal ExternalEditors? ExternalEditors { get; init; }

    /// <summary>The full path that a link names, whether or not the file is there; null when it names none.</summary>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<string?> FullPathAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
        => (await ResolveAsync(link, sessionId, projectId, directory, cancellationToken).ConfigureAwait(false)).Full;

    /// <summary>
    /// Opens what a link names in the external editor: a folder as it is, and a file at its line in the folder the session
    /// works in when it holds the file, else in its project, else in its own folder.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="sessionId">The session whose message has the link: a relative path starts from its folder.</param>
    /// <param name="projectId">Without a session, the project or the folder whose document has the link.</param>
    /// <param name="directory">The folder of that document inside the project, with forward slashes; null for the folder of the project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c>, <c>not_found</c>, <c>unavailable</c> without editors, or an answer of <see cref="CodeAlta.Desktop.ExternalEditors.OpenAsync"/>.</returns>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    internal async Task<string> OpenExternalAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        if (ExternalEditors is null) return "unavailable";
        var (full, sessionRoot) = await ResolveAsync(link, sessionId, projectId, directory, cancellationToken).ConfigureAwait(false);
        if (full is null || !Path.Exists(full)) return "not_found";
        if (Directory.Exists(full)) return await ExternalEditors.OpenAsync(full, null, null, null, cancellationToken).ConfigureAwait(false);
        var folder = sessionRoot is not null && Path.IsPathFullyQualified(sessionRoot) && GitWorktreeService.IsWithin(full, sessionRoot) ? sessionRoot
            : (await ProjectOfAsync(full, cancellationToken).ConfigureAwait(false))?.Root ?? Path.GetDirectoryName(full);
        return folder is null ? "not_found" : await ExternalEditors.OpenAsync(folder, full, link.Line, link.Column, cancellationToken).ConfigureAwait(false);
    }

    // The full path a link names, and the folder of its session: a relative path starts from that folder, or from
    // the folder of its document without a session.
    private async Task<(string? Full, string? SessionRoot)> ResolveAsync(FileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        var sessionRoot = sessionId is { Length: > 0 and <= 128 } ? await _sessionFolder(sessionId, cancellationToken).ConfigureAwait(false) : null;
        var start = sessionRoot ?? await DocumentFolderAsync(projectId, directory, cancellationToken).ConfigureAwait(false);
        return (link.Locate(start, _home), sessionRoot);
    }

    // The folder a relative link of a document starts from: the one of the document, inside its project.
    private async Task<string?> DocumentFolderAsync(string? projectId, string? directory, CancellationToken cancellationToken)
    {
        if (projectId is not { Length: > 0 and <= 512 }) return null;
        var (status, root) = await _folder(projectId, cancellationToken).ConfigureAwait(false);
        if (status != "ok" || root is null) return null;
        if (string.IsNullOrEmpty(directory)) return root;
        return ProjectFilesService.Normalize(directory, out var inside) == "ok" ? Path.Combine(root, inside!.Replace('/', Path.DirectorySeparatorChar)) : null;
    }

    private Task<(string Id, string Root)?> ProjectOfAsync(string full, CancellationToken cancellationToken) => ProjectOfAsync(_projects, full, cancellationToken);

    /// <summary>The project of a catalog whose folder holds a path: the innermost one when a project is inside another; null when none does.</summary>
    /// <param name="catalog">The project catalog.</param>
    /// <param name="full">The full path of a file or of a folder.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    internal static async Task<(string Id, string Root)?> ProjectOfAsync(ProjectCatalog catalog, string full, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        (string Id, string Root)? found = null;
        foreach (var project in projects)
        {
            if (project.Archived || !Path.IsPathFullyQualified(project.ProjectPath) || !GitWorktreeService.IsWithin(full, project.ProjectPath)) continue;
            var root = Path.GetFullPath(project.ProjectPath);
            if ((found is null || root.Length > found.Value.Root.Length) && Directory.Exists(root)) found = (project.Id, root);
        }

        return found;
    }

    // The path of an entry below a folder, with forward slashes; null for the folder itself.
    private static string? Relative(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return relative == "." ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    // Text has no NUL; the encodings that have one start with a byte order mark.
    private static async Task<bool> IsBinaryAsync(string full, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[SniffedBytes];
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> start = buffer.AsSpan(0, read);
            if (start is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false;
            return start.Contains((byte)0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false; // The code editor says why it cannot read the file.
        }
    }
}
