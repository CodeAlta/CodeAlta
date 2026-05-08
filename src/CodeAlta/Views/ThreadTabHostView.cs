using System.Diagnostics.CodeAnalysis;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal sealed class ThreadTabHostView
{
    private readonly Dictionary<string, TabPage> _tabPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VSplitter> _threadTabContentSplitters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Visual _threadBottomPanel;
    private string? _activeThreadTabContentId;

    public ThreadTabHostView(Visual threadBottomPanel, Action<int> onSelectedTabChanged)
    {
        ArgumentNullException.ThrowIfNull(threadBottomPanel);
        ArgumentNullException.ThrowIfNull(onSelectedTabChanged);

        _threadBottomPanel = threadBottomPanel;
        ThreadTabControl = new TabControl();
        ThreadTabControl.KeyDown((_, _) => ThreadTabControl.Dispatcher.Post(() => onSelectedTabChanged(ThreadTabControl.SelectedIndex)));
        ThreadTabControl.PointerReleased((_, _) => ThreadTabControl.Dispatcher.Post(() => onSelectedTabChanged(ThreadTabControl.SelectedIndex)));

        var threadPaneLayout = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        threadPaneLayout.Cell(ThreadTabControl.Stretch(), 0, 0);
        Root = threadPaneLayout;
    }

    public Visual Root { get; }

    public TabControl ThreadTabControl { get; }

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _tabPages.TryGetValue(tabId, out page);
    }

    public void RememberTabPage(string tabId, TabPage page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(page);
        _tabPages[tabId] = page;
    }

    public bool RemoveTabPage(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        var removed = _tabPages.Remove(tabId);
        if (_threadTabContentSplitters.Remove(tabId, out var splitter) &&
            string.Equals(_activeThreadTabContentId, tabId, StringComparison.OrdinalIgnoreCase))
        {
            if (ReferenceEquals(splitter.Second, _threadBottomPanel))
            {
                splitter.Second = null;
            }

            _activeThreadTabContentId = null;
        }

        return removed;
    }

    public Visual CreateThreadTabContent(string tabId, Visual primaryContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(primaryContent);

        if (_threadTabContentSplitters.TryGetValue(tabId, out var existing))
        {
            return existing;
        }

        if (primaryContent.Parent is VSplitter existingParent && ReferenceEquals(existingParent.First, primaryContent))
        {
            _threadTabContentSplitters[tabId] = existingParent;
            return existingParent;
        }

        var splitter = new VSplitter
        {
            First = primaryContent,
            Ratio = 0.75,
            MinFirst = 6,
            MinSecond = 7,
        };
        _threadTabContentSplitters[tabId] = splitter;
        return splitter;
    }

    public void ActivateThreadTabContent(string? tabId)
    {
        if (!string.IsNullOrWhiteSpace(_activeThreadTabContentId) &&
            _threadTabContentSplitters.TryGetValue(_activeThreadTabContentId, out var previous) &&
            ReferenceEquals(previous.Second, _threadBottomPanel))
        {
            previous.Second = null;
        }

        _activeThreadTabContentId = null;
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        if (!_threadTabContentSplitters.TryGetValue(tabId, out var current))
        {
            if (!_tabPages.TryGetValue(tabId, out var page) || page.Content is not VSplitter splitter)
            {
                return;
            }

            current = splitter;
            _threadTabContentSplitters[tabId] = current;
        }

        current.Second = _threadBottomPanel;
        _activeThreadTabContentId = tabId;
    }
}
