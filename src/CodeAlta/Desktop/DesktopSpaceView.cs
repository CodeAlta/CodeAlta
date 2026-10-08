using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// The space the window shows, and the requests about spaces that reach the window from elsewhere:
/// <c>alta space switch</c> asks it to show another one, and a command that changed the spaces asks it
/// to read them again. The page says what it shows; nothing here is a setting of the user.
/// </summary>
internal sealed class DesktopSpaceView : IAltaSpaceView
{
    private readonly Lock _gate = new();
    private readonly List<Action<SpacesEvent>> _watchers = [];
    private string? _shown;

    /// <inheritdoc />
    public string? ShownSpaceId
    {
        get { lock (_gate) return _shown; }
    }

    /// <summary>Records the space a page says it shows.</summary>
    internal void SetShown(string spaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        lock (_gate) _shown = spaceId;
    }

    /// <inheritdoc />
    public bool Show(string spaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        return Publish(new("show", spaceId));
    }

    /// <inheritdoc />
    public void NotifyChanged() => Publish(new("changed", null));

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<SpacesEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private bool Publish(SpacesEvent value)
    {
        Action<SpacesEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher(value);
        return watchers.Length > 0;
    }

    private sealed class Registration(DesktopSpaceView view, Action<SpacesEvent> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (view._gate) view._watchers.Remove(watcher);
        }
    }
}
