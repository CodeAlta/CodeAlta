using System.Diagnostics.CodeAnalysis;
using CodeAlta.Presentation.Chat;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal sealed class SessionTabHostView
{
    private const double DefaultPromptSplitRatio = 0.75;
    private const double AskPromptSplitRatio = 0.60;

    private readonly Dictionary<string, TabPage> _tabPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VSplitter> _sessionTabContentSplitters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionPromptPanel> _sessionPromptPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AskProjectionState> _askProjectionStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSessionTabContentId;

    public SessionTabHostView(SessionTabHostController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        SessionTabControl = new TabControl();
        AddAskNavigationCommands();
        SessionTabControl.SelectionChanged((_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, SessionTabControl))
            {
                return;
            }

            controller.SelectTab(e.NewIndex);
        });

        var sessionPaneLayout = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        sessionPaneLayout.Cell(SessionTabControl.Stretch(), 0, 0);
        Root = sessionPaneLayout;
    }

    public Visual Root { get; }

    public TabControl SessionTabControl { get; }

    public SessionPromptPanel? ActivePromptPanel
        => !string.IsNullOrWhiteSpace(_activeSessionTabContentId) &&
           _sessionPromptPanels.TryGetValue(_activeSessionTabContentId, out var panel)
            ? panel
            : null;

    public bool TryGetTabPage(string tabId, [NotNullWhen(true)] out TabPage? page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _tabPages.TryGetValue(tabId, out page);
    }

    public bool TryGetPromptPanel(string tabId, [NotNullWhen(true)] out SessionPromptPanel? panel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _sessionPromptPanels.TryGetValue(tabId, out panel);
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
            SessionTabControl.TryCloseTab(page);
        }

        var splitter = _sessionTabContentSplitters.Remove(tabId, out var cachedSplitter)
            ? cachedSplitter
            : page?.Content as VSplitter;
        if (splitter is not null)
        {
            splitter.First = null;
            splitter.Second = null;
        }

        _sessionPromptPanels.Remove(tabId);
        _askProjectionStates.Remove(tabId);
        if (string.Equals(_activeSessionTabContentId, tabId, StringComparison.OrdinalIgnoreCase))
        {
            _activeSessionTabContentId = null;
        }

        return removed;
    }

    public Visual CreateSessionTabContent(string tabId, Visual primaryContent, Func<SessionPromptPanel> promptPanelFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(primaryContent);
        ArgumentNullException.ThrowIfNull(promptPanelFactory);

        if (_sessionTabContentSplitters.TryGetValue(tabId, out var existing))
        {
            return existing;
        }

        if (primaryContent.Parent is not null)
        {
            throw new InvalidOperationException($"Cannot create prompt tab content for '{tabId}' because the primary content is already attached to a visual tree.");
        }

        var promptPanel = promptPanelFactory();
        var splitter = new VSplitter
        {
            First = primaryContent,
            Second = promptPanel.Root,
            Ratio = DefaultPromptSplitRatio,
            MinFirst = 6,
            MinSecond = 7,
        };
        _sessionPromptPanels[tabId] = promptPanel;
        _sessionTabContentSplitters[tabId] = splitter;
        return splitter;
    }

    public void ActivateSessionTabContent(string? tabId)
    {
        _activeSessionTabContentId = null;
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        if (!_sessionTabContentSplitters.ContainsKey(tabId))
        {
            if (!_tabPages.TryGetValue(tabId, out var page) || page.Content is not VSplitter splitter)
            {
                return;
            }

            _sessionTabContentSplitters[tabId] = splitter;
        }

        if (!_sessionPromptPanels.ContainsKey(tabId))
        {
            return;
        }

        _activeSessionTabContentId = tabId;
    }

    public bool EnterAskMode(string tabId, Visual askForm, Visual? fileReview = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(askForm);
        if (!_sessionTabContentSplitters.TryGetValue(tabId, out var splitter) || !_sessionPromptPanels.TryGetValue(tabId, out var promptPanel))
        {
            return false;
        }

        if (_askProjectionStates.ContainsKey(tabId))
        {
            return true;
        }

        _askProjectionStates[tabId] = new AskProjectionState(askForm, splitter.First, splitter.Second, splitter.Ratio);
        if (fileReview is not null)
        {
            splitter.First = fileReview;
        }

        splitter.Ratio = AskPromptSplitRatio;
        splitter.Second = askForm;
        return true;
    }

    public bool ExitAskMode(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (!_sessionTabContentSplitters.TryGetValue(tabId, out var splitter) || !_askProjectionStates.Remove(tabId, out var state))
        {
            return false;
        }

        splitter.First = state.Primary;
        splitter.Second = state.Bottom;
        splitter.Ratio = state.Ratio;
        return true;
    }

    private void AddAskNavigationCommands()
    {
        SessionTabControl.AddCommand(new Command
        {
            Id = "CodeAlta.SessionTabs.AskPreviousQuestion",
            LabelMarkup = "Previous ask question",
            Gesture = new KeyGesture(TerminalKey.Left),
            Presentation = CommandPresentation.None,
            ConsumesGestureWhenUnavailable = false,
            CanExecute = _ => CanRouteSessionTabKeyToAsk(),
            Execute = _ => ExecuteActiveAskCommand("CodeAlta.Ask.Previous"),
        });
        SessionTabControl.AddCommand(new Command
        {
            Id = "CodeAlta.SessionTabs.AskNextQuestion",
            LabelMarkup = "Next ask question",
            Gesture = new KeyGesture(TerminalKey.Right),
            Presentation = CommandPresentation.None,
            ConsumesGestureWhenUnavailable = false,
            CanExecute = _ => CanRouteSessionTabKeyToAsk(),
            Execute = _ => ExecuteActiveAskCommand("CodeAlta.Ask.Next"),
        });
        SessionTabControl.AddCommand(new Command
        {
            Id = "CodeAlta.SessionTabs.AskConsumeUp",
            LabelMarkup = "Keep ask focus",
            Gesture = new KeyGesture(TerminalKey.Up),
            Presentation = CommandPresentation.None,
            ConsumesGestureWhenUnavailable = false,
            CanExecute = _ => CanConsumeSplitterKeyDuringAsk(),
            Execute = static _ => { },
        });
        SessionTabControl.AddCommand(new Command
        {
            Id = "CodeAlta.SessionTabs.AskConsumeDown",
            LabelMarkup = "Keep ask focus",
            Gesture = new KeyGesture(TerminalKey.Down),
            Presentation = CommandPresentation.None,
            ConsumesGestureWhenUnavailable = false,
            CanExecute = _ => CanConsumeSplitterKeyDuringAsk(),
            Execute = static _ => { },
        });
    }

    private bool CanRouteSessionTabKeyToAsk()
        => TryGetActiveAskProjection(out _) && SessionTabControl.App?.FocusedElement is not TextBox;

    private bool CanConsumeSplitterKeyDuringAsk()
        => TryGetActiveAskProjection(out _) && SessionTabControl.App?.FocusedElement is VSplitter;

    private bool ExecuteActiveAskCommand(string commandId)
    {
        if (!TryGetActiveAskProjection(out var state))
        {
            return false;
        }

        var commands = state.AskForm.Commands;
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (string.Equals(command.Id, commandId, StringComparison.Ordinal) && command.IsVisibleFor(state.AskForm) && command.CanExecuteFor(state.AskForm))
            {
                command.Execute(state.AskForm);
                return true;
            }
        }

        return false;
    }

    private bool TryGetActiveAskProjection([NotNullWhen(true)] out AskProjectionState? state)
    {
        state = null;
        if (TryGetSelectedTabId() is { } selectedTabId && _askProjectionStates.TryGetValue(selectedTabId, out state))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_activeSessionTabContentId) && _askProjectionStates.TryGetValue(_activeSessionTabContentId, out state))
        {
            return true;
        }

        if (_askProjectionStates.Count == 1)
        {
            state = _askProjectionStates.Values.First();
            return true;
        }

        return false;
    }

    private string? TryGetSelectedTabId()
    {
        if ((uint)SessionTabControl.SelectedIndex >= (uint)SessionTabControl.Tabs.Count)
        {
            return null;
        }

        var selectedPage = SessionTabControl.Tabs[SessionTabControl.SelectedIndex];
        foreach (var (tabId, page) in _tabPages)
        {
            if (ReferenceEquals(page, selectedPage))
            {
                return tabId;
            }
        }

        return null;
    }

    private sealed record AskProjectionState(Visual AskForm, Visual? Primary, Visual? Bottom, double Ratio);
}

internal sealed class SessionPromptPanel
{
    public SessionPromptPanel(
        Visual root,
        ChatPromptEditor editor,
        Visual editorView,
        Visual sendPromptButton,
        Visual expandPromptButton,
        PromptComposerView composer,
        AgentPromptSelectorView agentPromptSelectorView,
        ModelProviderSelectorView modelProviderSelectorView,
        SessionPromptChromeState chromeState)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(editorView);
        ArgumentNullException.ThrowIfNull(sendPromptButton);
        ArgumentNullException.ThrowIfNull(expandPromptButton);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(agentPromptSelectorView);
        ArgumentNullException.ThrowIfNull(modelProviderSelectorView);
        ArgumentNullException.ThrowIfNull(chromeState);

        Root = root;
        Editor = editor;
        EditorView = editorView;
        SendPromptButton = sendPromptButton;
        ExpandPromptButton = expandPromptButton;
        Composer = composer;
        AgentPromptSelectorView = agentPromptSelectorView;
        ModelProviderSelectorView = modelProviderSelectorView;
        ChromeState = chromeState;
    }

    public SessionPromptChromeState ChromeState { get; }

    public Visual Root { get; }

    public ChatPromptEditor Editor { get; }

    public Visual EditorView { get; }

    public Visual SendPromptButton { get; }

    public Visual ExpandPromptButton { get; }

    public PromptComposerView Composer { get; }

    public AgentPromptSelectorView AgentPromptSelectorView { get; }

    public ModelProviderSelectorView ModelProviderSelectorView { get; }

    public CodeAltaShellViewModel ShellViewModel => ChromeState.ShellViewModel;

    public SessionWorkspaceViewModel WorkspaceViewModel => ChromeState.WorkspaceViewModel;

    public PromptComposerViewModel PromptComposerViewModel => ChromeState.PromptComposerViewModel;
}
