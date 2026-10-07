using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta diff show</c> to the window: the page that watches them opens the changes
/// tab of the project.
/// </summary>
internal sealed class DesktopChangesView : IAltaChangesView
{
    private readonly Lock _gate = new();
    private readonly List<Action<ProjectGitShowEvent>> _watchers = [];

    /// <inheritdoc />
    public bool Show(string projectId, string? path, string? worktree = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        Action<ProjectGitShowEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        foreach (var watcher in watchers)
            watcher(new(projectId, string.IsNullOrWhiteSpace(path) ? null : path, string.IsNullOrWhiteSpace(worktree) ? null : worktree));
        return watchers.Length > 0;
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<ProjectGitShowEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private sealed class Registration(DesktopChangesView view, Action<ProjectGitShowEvent> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (view._gate) view._watchers.Remove(watcher);
        }
    }
}
