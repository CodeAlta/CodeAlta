using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta documentation open</c>, and the links to pages of the user guide that are
/// followed in the window, to the window: the page that watches them opens the Documentation tab at the page that
/// was asked for.
/// </summary>
internal sealed class DesktopDocumentationView : IAltaDocumentationView
{
    private readonly Lock _gate = new();
    private readonly List<Action<DocumentationShowEvent>> _watchers = [];

    /// <inheritdoc />
    public bool Show(string? page, string? anchor)
    {
        Action<DocumentationShowEvent>[] watchers;
        lock (_gate) watchers = [.. _watchers];
        var shown = string.IsNullOrWhiteSpace(page) ? null : page;
        foreach (var watcher in watchers) watcher(new(shown, shown is null || string.IsNullOrWhiteSpace(anchor) ? null : anchor));
        return watchers.Length > 0;
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<DocumentationShowEvent> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate) _watchers.Add(watcher);
        return new Registration(this, watcher);
    }

    private sealed class Registration(DesktopDocumentationView view, Action<DocumentationShowEvent> watcher) : IDisposable
    {
        public void Dispose()
        {
            lock (view._gate) view._watchers.Remove(watcher);
        }
    }
}
