namespace CodeAlta.Desktop.Windows;

/// <summary>
/// The pages that watch a kind of request, each under the label of the view of its window (<c>main</c>,
/// <c>window-&lt;id&gt;</c>). A request goes to the pages of one window, so that a file asked for opens in one
/// window, not in each: the window that holds its tab, else the one the request came from, else the main one.
/// </summary>
/// <typeparam name="T">The request the pages get.</typeparam>
internal sealed class DesktopPageWatchers<T>
{
    private readonly Lock _gate = new();
    private readonly List<(string Label, Action<T> Watcher)> _watchers = [];

    /// <summary>Registers a page; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of its window; null for the main one.</param>
    /// <param name="watcher">What receives the requests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="watcher"/> is null.</exception>
    internal IDisposable Watch(string? label, Action<T> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        var entry = (string.IsNullOrWhiteSpace(label) ? DesktopWindows.MainLabel : label, watcher);
        lock (_gate) _watchers.Add(entry);
        return new Registration(this, entry);
    }

    /// <summary>Whether a page of a window watches.</summary>
    internal bool Watches(string label)
    {
        lock (_gate) return _watchers.Any(entry => entry.Label == label);
    }

    /// <summary>
    /// Gives a request to the pages of a window, or to those of the main window when that window has none; to every
    /// page when no window is named.
    /// </summary>
    /// <param name="value">The request.</param>
    /// <param name="label">The label of the window; null for every page.</param>
    /// <returns>True when a page received it.</returns>
    internal bool Publish(T value, string? label)
    {
        Action<T>[] watchers;
        lock (_gate)
        {
            watchers = label is null ? [.. _watchers.Select(static entry => entry.Watcher)]
                : [.. _watchers.Where(entry => entry.Label == label).Select(static entry => entry.Watcher)];
            if (watchers.Length == 0 && label is not null && label != DesktopWindows.MainLabel)
                watchers = [.. _watchers.Where(static entry => entry.Label == DesktopWindows.MainLabel).Select(static entry => entry.Watcher)];
        }

        foreach (var watcher in watchers) watcher(value);
        return watchers.Length > 0;
    }

    private sealed class Registration(DesktopPageWatchers<T> owner, (string Label, Action<T> Watcher) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._watchers.Remove(entry);
        }
    }
}
