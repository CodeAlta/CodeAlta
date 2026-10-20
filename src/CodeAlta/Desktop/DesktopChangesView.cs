using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta diff show</c> to the window: the page that watches them opens the changes
/// tab of the project. With several windows, the request goes to the window that has the changes of that project,
/// else to the window of the session that asked, else to the main one.
/// </summary>
internal sealed class DesktopChangesView : IAltaChangesView
{
    private readonly DesktopPageWatchers<ProjectGitShowEvent> _watchers = new();

    /// <summary>
    /// Chooses the window of a request from the tab it opens and the session that asked, and brings it to the front;
    /// null for every page (a host with one window).
    /// </summary>
    internal Func<string, string?, string>? Router { get; set; }

    /// <inheritdoc />
    public bool Show(string projectId, string? path, string? worktree = null) => Show(projectId, path, worktree, null);

    /// <inheritdoc />
    public bool Show(string projectId, string? path, string? worktree, string? callerSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _watchers.Publish(new(projectId, string.IsNullOrWhiteSpace(path) ? null : path, string.IsNullOrWhiteSpace(worktree) ? null : worktree),
            Router?.Invoke("changes:" + projectId, callerSessionId));
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of the window of the page; null for the main one.</param>
    /// <param name="watcher">What receives the requests.</param>
    internal IDisposable Watch(string? label, Action<ProjectGitShowEvent> watcher) => _watchers.Watch(label, watcher);

    /// <summary>Registers a page of the main window for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<ProjectGitShowEvent> watcher) => _watchers.Watch(null, watcher);
}
