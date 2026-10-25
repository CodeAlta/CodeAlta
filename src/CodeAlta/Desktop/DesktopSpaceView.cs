using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// The space each window shows, and the requests about spaces that reach the windows from elsewhere:
/// <c>alta space switch</c> asks the main window to show another one, and a command that changed the spaces asks
/// every window to read them again. The pages say what they show; nothing here is a setting of the user.
/// </summary>
/// <remarks>
/// A secondary window keeps the space it was created in. What the host does for "the space the window shows" (the
/// space of a new session, of a terminal, of a plugin) is for the window the user used last.
/// </remarks>
internal sealed class DesktopSpaceView : IAltaSpaceView
{
    private readonly Lock _gate = new();
    private readonly DesktopPageWatchers<SpacesEvent> _watchers = new();
    private readonly Dictionary<string, string> _shown = new(StringComparer.Ordinal);
    // The session tabs of each page, by its window and document: the main page says those of every space.
    private readonly Dictionary<string, string[]> _open = new(StringComparer.Ordinal);
    private readonly Lock _openOrder = new();

    /// <summary>
    /// Raised with every session the windows have a tab of, when that changes: the host holds their leases, so that another
    /// process does not run a session open here. Raised outside the lock of the view, in order.
    /// </summary>
    internal event Action<IReadOnlyCollection<string>>? OpenSessionsChanged;

    /// <summary>The label of the view of the window the user used last; null for the main one.</summary>
    internal Func<string>? FocusedLabel { get; set; }

    /// <summary>Raised when a page says which space it shows, with the label of the view of its window.</summary>
    internal event Action<string, string>? ShownChanged;

    /// <inheritdoc />
    public string? ShownSpaceId
    {
        get
        {
            var label = FocusedLabel?.Invoke() ?? DesktopWindows.MainLabel;
            lock (_gate) return _shown.TryGetValue(label, out var space) ? space : _shown.GetValueOrDefault(DesktopWindows.MainLabel);
        }
    }

    /// <summary>The space a window shows; null when its page has not said it yet.</summary>
    internal string? ShownSpaceOf(string label)
    {
        lock (_gate) return _shown.GetValueOrDefault(label);
    }

    /// <summary>Records the space the page of the main window says it shows.</summary>
    internal void SetShown(string spaceId) => SetShown(DesktopWindows.MainLabel, spaceId);

    /// <summary>Records the space a page says it shows.</summary>
    /// <param name="label">The label of the view of the window of the page.</param>
    /// <param name="spaceId">The space.</param>
    internal void SetShown(string label, string spaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        lock (_gate) _shown[label] = spaceId;
        ShownChanged?.Invoke(label, spaceId);
    }

    /// <inheritdoc />
    public bool Show(string spaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        // Only the main window changes its space: a secondary window keeps the one it was created in.
        return _watchers.Publish(new("show", spaceId), DesktopWindows.MainLabel);
    }

    /// <inheritdoc />
    public void NotifyChanged() => _watchers.Publish(new("changed", null), null);

    /// <summary>
    /// Asks every window to read the sessions again: a session another process writes was listed, changed or released.
    /// </summary>
    internal void NotifySessionsChanged() => _watchers.Publish(new("sessions", null), null);

    /// <summary>Records the session tabs of a page; an empty list when it has none, or when it went away.</summary>
    /// <param name="page">The page: the label of the view of its window and its document.</param>
    /// <param name="sessionIds">The sessions it has a tab of.</param>
    internal void SetOpenSessions(string page, IReadOnlyCollection<string> sessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        ArgumentNullException.ThrowIfNull(sessionIds);
        // One publication at a time, so that the last one told is the last one recorded.
        lock (_openOrder)
        {
            string[] union;
            lock (_gate)
            {
                if (sessionIds.Count == 0) _open.Remove(page);
                else _open[page] = [.. sessionIds];
                union = [.. _open.Values.SelectMany(static ids => ids).Distinct(StringComparer.OrdinalIgnoreCase)];
            }

            OpenSessionsChanged?.Invoke(union);
        }
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of the window of the page; null for the main one.</param>
    /// <param name="watcher">What receives the requests.</param>
    internal IDisposable Watch(string? label, Action<SpacesEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        return _watchers.Watch(label, watcher);
    }

    /// <summary>Registers a page of the main window for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<SpacesEvent> watcher) => Watch(null, watcher);
}
