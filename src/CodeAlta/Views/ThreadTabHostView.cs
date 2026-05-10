using System.Diagnostics.CodeAnalysis;
using CodeAlta.Presentation.Chat;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal sealed class ThreadTabHostView
{
    private readonly Dictionary<string, TabPage> _tabPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VSplitter> _threadTabContentSplitters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreadPromptPanel> _threadPromptPanels = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeThreadTabContentId;

    public ThreadTabHostView(ThreadTabHostController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        ThreadTabControl = new TabControl();
        ThreadTabControl.SelectionChanged((_, e) => controller.SelectTab(e.NewIndex));

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

    public ThreadPromptPanel? ActivePromptPanel
        => !string.IsNullOrWhiteSpace(_activeThreadTabContentId) &&
           _threadPromptPanels.TryGetValue(_activeThreadTabContentId, out var panel)
            ? panel
            : null;

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
        var removed = _tabPages.Remove(tabId, out var page);
        if (removed && page is not null)
        {
            ThreadTabControl.TryCloseTab(page);
        }

        _threadTabContentSplitters.Remove(tabId);
        _threadPromptPanels.Remove(tabId);
        if (string.Equals(_activeThreadTabContentId, tabId, StringComparison.OrdinalIgnoreCase))
        {
            _activeThreadTabContentId = null;
        }

        return removed;
    }

    public Visual CreateThreadTabContent(string tabId, Visual primaryContent, Func<ThreadPromptPanel> promptPanelFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(primaryContent);
        ArgumentNullException.ThrowIfNull(promptPanelFactory);

        if (_threadTabContentSplitters.TryGetValue(tabId, out var existing))
        {
            return existing;
        }

        if (primaryContent.Parent is VSplitter existingParent && ReferenceEquals(existingParent.First, primaryContent))
        {
            _threadTabContentSplitters[tabId] = existingParent;
            if (!_threadPromptPanels.ContainsKey(tabId))
            {
                var recoveredPromptPanel = promptPanelFactory();
                existingParent.Second = recoveredPromptPanel.Root;
                _threadPromptPanels[tabId] = recoveredPromptPanel;
            }

            return existingParent;
        }

        var promptPanel = promptPanelFactory();
        var splitter = new VSplitter
        {
            First = primaryContent,
            Second = promptPanel.Root,
            Ratio = 0.75,
            MinFirst = 6,
            MinSecond = 7,
        };
        _threadPromptPanels[tabId] = promptPanel;
        _threadTabContentSplitters[tabId] = splitter;
        return splitter;
    }

    public void ActivateThreadTabContent(string? tabId)
    {
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

        _activeThreadTabContentId = tabId;
    }
}

internal sealed class ThreadPromptPanel
{
    public ThreadPromptPanel(
        Visual root,
        ChatPromptEditor editor,
        Visual editorView,
        Visual sendPromptButton,
        Visual expandPromptButton,
        PromptComposerView composer,
        ModelProviderSelectorView modelProviderSelectorView,
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(editorView);
        ArgumentNullException.ThrowIfNull(sendPromptButton);
        ArgumentNullException.ThrowIfNull(expandPromptButton);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(modelProviderSelectorView);
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        Root = root;
        Editor = editor;
        EditorView = editorView;
        SendPromptButton = sendPromptButton;
        ExpandPromptButton = expandPromptButton;
        Composer = composer;
        ModelProviderSelectorView = modelProviderSelectorView;
        ShellViewModel = shellViewModel;
        WorkspaceViewModel = workspaceViewModel;
        PromptComposerViewModel = promptComposerViewModel;
    }

    public Visual Root { get; }

    public ChatPromptEditor Editor { get; }

    public Visual EditorView { get; }

    public Visual SendPromptButton { get; }

    public Visual ExpandPromptButton { get; }

    public PromptComposerView Composer { get; }

    public ModelProviderSelectorView ModelProviderSelectorView { get; }

    public CodeAltaShellViewModel ShellViewModel { get; }

    public ThreadWorkspaceViewModel WorkspaceViewModel { get; }

    public PromptComposerViewModel PromptComposerViewModel { get; }
}
