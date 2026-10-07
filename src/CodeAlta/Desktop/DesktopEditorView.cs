using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta editor open</c> to the window: the page that watches them opens the code
/// editor of the project, and the file that was asked for in it.
/// </summary>
internal sealed class DesktopEditorView : IAltaEditorView
{
    private readonly Lock _gate = new();
    private readonly List<Action<ProjectFileShowEvent>> _watchers = [];

    /// <inheritdoc />
    public bool Open(string projectId, string? path, int? line, int? column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        Action<ProjectFileShowEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        var file = string.IsNullOrWhiteSpace(path) ? null : path;
        foreach (var watcher in watchers) watcher(new(projectId, file, file is null ? null : line, file is null || line is null ? null : column));
        return watchers.Length > 0;
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
        Action<ProjectFileShowEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        var file = string.IsNullOrWhiteSpace(path) ? null : path;
        foreach (var watcher in watchers)
        {
            watcher(new(folder.Id, file, file is null ? null : line, file is null || line is null ? null : column, folder.PackageId, root));
        }

        return watchers.Length > 0;
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<ProjectFileShowEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private sealed class Registration(DesktopEditorView view, Action<ProjectFileShowEvent> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (view._gate) view._watchers.Remove(watcher);
        }
    }
}
