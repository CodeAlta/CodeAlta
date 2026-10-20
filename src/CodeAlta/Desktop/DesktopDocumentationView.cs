using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Passes the requests of <c>alta documentation open</c>, and the links to pages of the user guide that are
/// followed in the window, to the window: the page that watches them opens the Documentation tab at the page that
/// was asked for. With several windows, the request goes to the window that has the Documentation tab, else to the
/// window of the session that asked, else to the main one.
/// </summary>
internal sealed class DesktopDocumentationView : IAltaDocumentationView
{
    private readonly DesktopPageWatchers<DocumentationShowEvent> _watchers = new();

    /// <summary>
    /// Chooses the window of a request from the tab it opens and the session that asked, and brings it to the front;
    /// null for every page (a host with one window).
    /// </summary>
    internal Func<string, string?, string>? Router { get; set; }

    /// <inheritdoc />
    public bool Show(string? page, string? anchor) => Show(page, anchor, null);

    /// <inheritdoc />
    public bool Show(string? page, string? anchor, string? callerSessionId)
    {
        var shown = string.IsNullOrWhiteSpace(page) ? null : page;
        return _watchers.Publish(new(shown, shown is null || string.IsNullOrWhiteSpace(anchor) ? null : anchor), Router?.Invoke("documentation", callerSessionId));
    }

    /// <summary>Registers a page for the requests; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of the window of the page; null for the main one.</param>
    /// <param name="watcher">What receives the requests.</param>
    internal IDisposable Watch(string? label, Action<DocumentationShowEvent> watcher) => _watchers.Watch(label, watcher);

    /// <summary>Registers a page of the main window for the requests; disposing the result ends it.</summary>
    internal IDisposable Watch(Action<DocumentationShowEvent> watcher) => _watchers.Watch(null, watcher);
}
