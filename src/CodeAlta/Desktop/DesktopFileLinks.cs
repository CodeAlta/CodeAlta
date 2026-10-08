using System.Globalization;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop;

/// <summary>A link to a file of the disk, as the text of a message or of a document writes its target.</summary>
/// <remarks>
/// <para>
/// The target is a path, relative to the folder the link is read in or full, or a <c>file:</c> address. A place
/// in the file follows it: <c>#L10</c>, <c>#L10C5</c> and <c>#L10-L20</c> as a fragment, or <c>:10</c>,
/// <c>:10:5</c> and <c>:10-20</c> after the path. A range goes to its start.
/// </para>
/// <para>
/// Reading a target never touches the disk. A target that names another computer (a UNC path, a device path, a
/// <c>file:</c> address with a host) is no link to a file: looking at such a path would already send the
/// credentials of the user to that computer. Neither is a target with another scheme.
/// </para>
/// </remarks>
/// <param name="Path">The path, with the separators of the system: full, relative, or starting with <c>~</c> for the home folder.</param>
/// <param name="Line">The 1-based line to go to; null for none.</param>
/// <param name="Column">The 1-based column on that line; null for none.</param>
/// <param name="IsAddress">Whether the target was written as a <c>file:</c> address.</param>
internal readonly partial record struct DesktopFileLink(string Path, int? Line, int? Column, bool IsAddress)
{
    /// <summary>The longest target that is read.</summary>
    internal const int MaximumLength = 2048;

    /// <summary>Reads the target of a link; false for anything that is not a link to a file of this computer.</summary>
    internal static bool TryParse(string? target, out DesktopFileLink link)
    {
        link = default;
        if (target is not { Length: > 0 and <= MaximumLength } || target != target.Trim() || target.Any(char.IsControl)) return false;
        return target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? TryParseAddress(target, out link) : TryParsePath(target, out link);
    }

    private static bool TryParseAddress(string target, out DesktopFileLink link)
    {
        link = default;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) return false;
        if (uri.Host.Length > 0 && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.LocalPath;
        if (path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path) || NamesAnotherComputer(path)) return false;
        var (line, column) = uri.Fragment.Length > 1 ? Place(uri.Fragment.AsSpan(1)) : (null, null);
        if (line is null) (path, line, column) = PlaceAfter(path);
        link = new(path.Replace('/', System.IO.Path.DirectorySeparatorChar), line, column, IsAddress: true);
        return true;
    }

    private static bool TryParsePath(string target, out DesktopFileLink link)
    {
        link = default;
        var fragment = target.IndexOf('#');
        // Markdown writes a space, a backslash and a letter outside ASCII of a target as an escape.
        var path = Uri.UnescapeDataString(fragment < 0 ? target : target[..fragment]).Replace('\\', '/');
        if (path.Length == 0 || path.Any(char.IsControl) || NamesAnotherComputer(path)) return false;
        var (line, column) = fragment >= 0 ? Place(target.AsSpan(fragment + 1)) : (null, null);
        if (line is null) (path, line, column) = PlaceAfter(path);
        // "/C:/folder/file", as an address without its scheme writes a path of Windows.
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        var colon = path.IndexOf(':');
        if (colon >= 0)
        {
            // A drive is the one colon of a path; any other is a scheme, or a stream of a file.
            var drive = OperatingSystem.IsWindows() && colon == 1 && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] == '/')
                && path.IndexOf(':', 2) < 0;
            var name = !OperatingSystem.IsWindows() && path.AsSpan(0, colon).Contains('/');
            if (!drive && !name) return false;
        }

        if (path.Length == 0) return false;
        link = new(path.Replace('/', System.IO.Path.DirectorySeparatorChar), line, column, IsAddress: false);
        return true;
    }

    // "//server/share", "//?/C:/" and "//./device": the first names another computer, the others leave the rules of a path.
    private static bool NamesAnotherComputer(string path) => path.Length >= 2 && path[0] is '/' or '\\' && path[1] is '/' or '\\';

    // The place a fragment names: "L10", "L10C5", "L10-L20", or the same without the letter.
    private static (int? Line, int? Column) Place(ReadOnlySpan<char> fragment)
        => FragmentPlace().Match(fragment.ToString()) is { Success: true } match ? Numbers(match) : (null, null);

    // The place written after a path: ":10", ":10:5", ":10-20".
    private static (string Path, int? Line, int? Column) PlaceAfter(string path)
    {
        if (PathPlace().Match(path) is not { Success: true } match || match.Index == 0) return (path, null, null);
        var (line, column) = Numbers(match);
        return (path[..match.Index], line, column);
    }

    private static (int? Line, int? Column) Numbers(Match match)
    {
        var line = int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        if (line < 1) return (null, null);
        int? column = match.Groups[2].Success ? int.Parse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        return (line, column is < 1 ? null : column);
    }

    [GeneratedRegex(@"^L?(\d{1,7})(?:[C:](\d{1,7}))?(?:-L?\d{1,7}(?:[C:]\d{1,7})?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FragmentPlace();

    [GeneratedRegex(@":(\d{1,7})(?::(\d{1,7}))?(?:-\d{1,7}(?::\d{1,7})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPlace();
}

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
    internal async Task<string> OpenAsync(DesktopFileLink link, string? sessionId, string? projectId, string? directory, CancellationToken cancellationToken)
    {
        var sessionRoot = sessionId is { Length: > 0 and <= 128 } ? await _sessionFolder(sessionId, cancellationToken).ConfigureAwait(false) : null;
        var start = sessionRoot ?? await DocumentFolderAsync(projectId, directory, cancellationToken).ConfigureAwait(false);
        if (Locate(link.Path, start) is not { } full) return "not_found";
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
            if (link.IsAddress && IsDocument(full)) return _openDocument(full) ? "ok" : "failed";
            if (!IsPicture(full) && await IsBinaryAsync(full, cancellationToken).ConfigureAwait(false)) return "binary";
        }

        if (await ProjectOfAsync(full, cancellationToken).ConfigureAwait(false) is { } project)
        {
            var inside = Relative(project.Root, full);
            // The folder of the project, or a folder in it: its files are shown.
            if (inside is null || isDirectory) return _view.Open(project.Id, null, null, null) ? "ok" : "failed";
            if (ProjectFilesService.Normalize(inside, out var file) == "ok") return _view.Open(project.Id, file, link.Line, link.Column) ? "ok" : "failed";
        }

        if (isDirectory) return _view.OpenFolder(_folders.Give(full), null, null, null) ? "ok" : "failed";
        // The folder the session works in when the file is in it: a worktree keeps its own files together.
        if (sessionRoot is not null && Path.IsPathFullyQualified(sessionRoot) && GitWorktreeService.IsWithin(full, sessionRoot)
            && Relative(sessionRoot, full) is { } below && ProjectFilesService.Normalize(below, out var worked) == "ok")
        {
            return _view.OpenFolder(_folders.Give(sessionRoot), worked, link.Line, link.Column) ? "ok" : "failed";
        }

        if (Path.GetDirectoryName(full) is not { Length: > 0 } parent || ProjectFilesService.Normalize(Path.GetFileName(full), out var name) != "ok") return "not_found";
        return _view.OpenFolder(_folders.Give(parent), name, link.Line, link.Column) ? "ok" : "failed";
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

    // The full path a link names; null when it has none.
    private string? Locate(string path, string? start)
    {
        try
        {
            if (path == "~" || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return _home.Length == 0 ? null : Path.GetFullPath(Path.Combine(_home, path.Length > 2 ? path[2..] : string.Empty));
            if (Path.IsPathFullyQualified(path))
            {
                var full = Path.GetFullPath(path);
                // "/src/file" is also written for a file of the project, as a site writes a path from its root.
                if (start is null || !path.StartsWith(Path.DirectorySeparatorChar) || Path.Exists(full)) return full;
            }
            else if (!path.StartsWith(Path.DirectorySeparatorChar) && Path.IsPathRooted(path))
            {
                return null; // "C:file": a path that depends on the current folder of a drive.
            }

            return start is null || !Path.IsPathFullyQualified(start) ? null
                : Path.GetFullPath(Path.Combine(start, path.TrimStart(Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // The project whose folder holds a path: the innermost one when a project is inside another.
    private async Task<(string Id, string Root)?> ProjectOfAsync(string full, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await _projects.LoadAsync(cancellationToken).ConfigureAwait(false);
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
