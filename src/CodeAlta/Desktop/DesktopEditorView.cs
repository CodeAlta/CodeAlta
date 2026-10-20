using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta editor open</c>, and the links to files that are followed in the window, to
/// the window: the page that watches them opens the code editor of the project, and the file that was asked
/// for in it. With several windows, the request goes to the window that has the editor of that project, else to
/// the window of the session that asked, else to the main one.
/// </summary>
internal sealed class DesktopEditorView : IAltaEditorView
{
    private readonly DesktopPageWatchers<ProjectFileShowEvent> _watchers = new();

    /// <summary>
    /// Chooses the window of a request from the tab it opens, the window it came from and the session that asked, and
    /// brings it to the front; null for every page (a host with one window).
    /// </summary>
    internal Func<string, string?, string?, string>? Router { get; set; }

    /// <inheritdoc />
    public bool Open(string projectId, string? path, int? line, int? column) => Open(projectId, path, line, column, null);

    /// <inheritdoc />
    public bool Open(string projectId, string? path, int? line, int? column, string? callerSessionId) => Open(projectId, path, line, column, callerSessionId, null);

    /// <inheritdoc cref="Open(string, string?, int?, int?, string?)"/>
    /// <param name="projectId">The project whose code editor opens.</param>
    /// <param name="path">The file to open, relative to the project; null to show its files only.</param>
    /// <param name="line">The 1-based line to go to in the file; null to keep where the file was.</param>
    /// <param name="column">The 1-based column on that line; null for its start.</param>
    /// <param name="callerSessionId">The session whose agent asked or whose link was followed; null for none.</param>
    /// <param name="originLabel">The label of the view of the window where a link was followed; null when not known.</param>
    internal bool Open(string projectId, string? path, int? line, int? column, string? callerSessionId, string? originLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var file = string.IsNullOrWhiteSpace(path) ? null : path;
        return Publish(projectId, callerSessionId, originLabel, new(projectId, file, file is null ? null : line, file is null || line is null ? null : column));
    }

    /// <summary>Asks the window to open the code editor on the folder of a source plugin, and a file in it.</summary>
    /// <param name="folder">The folder of the plugin.</param>
    /// <param name="root">The path of that folder, which the tab shows.</param>
    /// <param name="path">The file to open, relative to the folder; null to show its files only.</param>
    /// <param name="line">The 1-based line to go to in the file; null to keep where the file was.</param>
    /// <param name="column">The 1-based column on that line; null for its start.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null or blank.</exception>
    internal bool OpenFolder(PluginFolder folder, string root, string? path, int? line, int? column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var file = string.IsNullOrWhiteSpace(path) ? null : path;
        return Publish(folder.Id, null, null, new(folder.Id, file, file is null ? null : line, file is null || line is null ? null : column, folder.PackageId, root));
    }

    /// <summary>Asks the window to open the code editor on a folder of the disk that is no project, and a file in it.</summary>
    /// <param name="folder">The folder, as <see cref="DiskFolders"/> gave it.</param>
    /// <param name="path">The file to open, relative to the folder; null to show its files only.</param>
    /// <param name="line">The 1-based line to go to in the file; null to keep where the file was.</param>
    /// <param name="column">The 1-based column on that line; null for its start.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    internal bool OpenFolder(DiskFolder folder, string? path, int? line, int? column) => OpenFolder(folder, path, line, column, null, null);

    /// <inheritdoc cref="OpenFolder(DiskFolder, string?, int?, int?)"/>
    /// <param name="folder">The folder, as <see cref="DiskFolders"/> gave it.</param>
    /// <param name="path">The file to open, relative to the folder; null to show its files only.</param>
    /// <param name="line">The 1-based line to go to in the file; null to keep where the file was.</param>
    /// <param name="column">The 1-based column on that line; null for its start.</param>
    /// <param name="callerSessionId">The session whose link was followed, whose window opens the editor; null for none.</param>
    /// <param name="originLabel">The label of the view of the window where the link was followed; null when not known.</param>
    internal bool OpenFolder(DiskFolder folder, string? path, int? line, int? column, string? callerSessionId, string? originLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder.Id);
        var file = string.IsNullOrWhiteSpace(path) ? null : path;
        return Publish(folder.Id, callerSessionId, originLabel, new(folder.Id, file, file is null ? null : line, file is null || line is null ? null : column, folder.Name, folder.Root));
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of the window of the page; null for the main one.</param>
    /// <param name="watcher">What receives the requests.</param>
    internal IDisposable Watch(string? label, Action<ProjectFileShowEvent> watcher) => _watchers.Watch(label, watcher);

    /// <summary>Registers a page of the main window for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<ProjectFileShowEvent> watcher) => _watchers.Watch(null, watcher);

    private bool Publish(string folderId, string? callerSessionId, string? originLabel, ProjectFileShowEvent value)
        => _watchers.Publish(value, Router?.Invoke("editor:" + folderId, originLabel, callerSessionId));
}
