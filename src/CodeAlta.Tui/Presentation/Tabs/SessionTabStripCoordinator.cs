using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.Presentation.Tabs;

internal sealed class SessionTabStripCoordinator
{
    private readonly SessionSelectionContext _sessionSelection;
    private readonly SessionTabContext _sessionTabs;
    private readonly IShellTabService _shellTabs;
    private readonly Func<bool> _hasDraftPrompt;
    private readonly Func<string, bool> _isRuntimeSessionRunning;
    private readonly State<float> _welcomeAnimationPhase01;
    private bool _syncingSelection;
    private bool _syncingPages;
    private int _lastObservedSelectedIndex = -1;
    private string? _pendingSessionSelectionSessionId;
    private bool _restoredSessionTabsFromViewState;

    public SessionTabStripCoordinator(
        SessionSelectionContext sessionSelection,
        SessionTabContext sessionTabs,
        IShellTabService shellTabs,
        State<float> welcomeAnimationPhase01,
        Func<bool>? hasDraftPrompt = null,
        Func<string, bool>? isRuntimeSessionRunning = null)
    {
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(sessionTabs);
        ArgumentNullException.ThrowIfNull(shellTabs);
        ArgumentNullException.ThrowIfNull(welcomeAnimationPhase01);

        _sessionSelection = sessionSelection;
        _sessionTabs = sessionTabs;
        _shellTabs = shellTabs;
        _welcomeAnimationPhase01 = welcomeAnimationPhase01;
        _hasDraftPrompt = hasDraftPrompt ?? (static () => false);
        _isRuntimeSessionRunning = isRuntimeSessionRunning ?? (static _ => false);
    }

    public void SyncControl()
    {
        if (_syncingPages)
        {
            return;
        }

        var tabControl = _sessionTabs.GetTabControl();
        if (tabControl is null)
        {
            return;
        }

        var workspaceView = _sessionTabs.GetWorkspaceView();
        if (workspaceView is null)
        {
            return;
        }

        _syncingPages = true;
        try
        {
            var projection = BuildProjection();
            var desiredPages = BuildDesiredPages(projection);

            tabControl.IsVisible = projection.Tabs.Count > 0;

            var existingPages = tabControl.Tabs;
            var matches = existingPages.Count == desiredPages.Count;
            if (matches)
            {
                for (var i = 0; i < desiredPages.Count; i++)
                {
                    if (!ReferenceEquals(existingPages[i], desiredPages[i]))
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (!matches)
            {
                for (var i = existingPages.Count - 1; i >= 0; i--)
                {
                    tabControl.TryCloseTab(existingPages[i]);
                }

                foreach (var page in desiredPages)
                {
                    tabControl.AddTab(page);
                }
            }

            SyncSelection(projection, tabControl);
            SyncActiveSessionTabContent(workspaceView, tabControl);
        }
        finally
        {
            _syncingPages = false;
        }
    }

    public Task<bool> CloseSelectedTabAsync(CancellationToken cancellationToken = default)
    {
        var selectedTab = _shellTabs.GetTabs().FirstOrDefault(static tab => tab.IsSelected);
        return selectedTab is null
            ? Task.FromResult(false)
            : CloseTabAsync(selectedTab, ShellTabCloseReason.UserDetached, cancellationToken);
    }

    public void OnSelectionChanged(int selectedIndex)
    {
        var tabControl = _sessionTabs.GetTabControl();
        if (_syncingSelection || _syncingPages || tabControl is null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= tabControl.Tabs.Count)
        {
            return;
        }

        var workspaceView = _sessionTabs.GetWorkspaceView();
        if (workspaceView is not null)
        {
            SyncActiveSessionTabContent(workspaceView, tabControl);
        }

        var selection = _sessionSelection.Selection;
        if (IsDraftTab(tabControl.Tabs[selectedIndex]))
        {
            SelectShellTab(CodeAltaApp.DraftTabId);
            if (selection.Target is WorkspaceTarget.Draft)
            {
                _sessionTabs.ActivateSessionSurface();
                return;
            }

            _sessionTabs.ActivateDraftTab();
            return;
        }

        if (!TryGetTabId(tabControl.Tabs[selectedIndex], out var tabId))
        {
            return;
        }

        if (_sessionTabs.GetFileTab(tabId) is not null)
        {
            _sessionTabs.SelectFileTab(tabId);
            return;
        }

        if (_shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab) && shellTab.Kind == ShellTabKind.Plugin)
        {
            SelectShellTab(tabId);
            return;
        }

        SelectShellTab(tabId);
        if (string.Equals(tabId, selection.SelectedSessionId, StringComparison.OrdinalIgnoreCase))
        {
            _sessionTabs.ActivateSessionSurface();
            return;
        }

        if (string.Equals(tabId, _pendingSessionSelectionSessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingSessionSelectionSessionId = tabId;
        _sessionTabs.GetUiDispatcher().Post(
            () =>
            {
                if (!string.Equals(tabId, _pendingSessionSelectionSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pendingSessionSelectionSessionId = null;
                _sessionTabs.OpenSession(tabId);
            });
    }

    private void SelectShellTab(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId) ||
            !_shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab) ||
            shellTab.IsSelected)
        {
            return;
        }

        _shellTabs.SelectTabAsync(shellTab.TabId).GetAwaiter().GetResult();
    }

    public void ObserveBoundSelection(int selectedIndex)
    {
        if (_lastObservedSelectedIndex == selectedIndex)
        {
            return;
        }

        _lastObservedSelectedIndex = selectedIndex;
        OnSelectionChanged(selectedIndex);
    }

    public void ResetPendingSelection()
    {
        _pendingSessionSelectionSessionId = null;
    }

    public void SelectCurrentSessionSurfaceTab()
    {
        var workspaceView = _sessionTabs.GetWorkspaceView();
        if (workspaceView is null)
        {
            return;
        }

        EnsureSelectedSessionSurfaceShellTab(workspaceView);
        var selectedTabId = ResolveSessionSurfaceSelectedTabId();
        if (string.IsNullOrWhiteSpace(selectedTabId) ||
            !_shellTabs.TryGetTab(new ShellTabId(selectedTabId), out var selectedTab) ||
            selectedTab.IsSelected)
        {
            return;
        }

        _shellTabs.SelectTabAsync(selectedTab.TabId).GetAwaiter().GetResult();
    }

    public bool ReplaceDraftTabWithSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        ResetPendingSelection();
        if (_sessionTabs.GetWorkspaceView() is { } workspaceView && _sessionSelection.FindSession(sessionId) is { } session)
        {
            EnsureSessionShellTab(workspaceView, session);
        }

        if (_shellTabs.TryGetTab(new ShellTabId(sessionId), out var sessionTab) && !sessionTab.IsSelected)
        {
            _shellTabs.SelectTabAsync(sessionTab.TabId).GetAwaiter().GetResult();
        }

        var replaced = _shellTabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab) &&
            _shellTabs.CloseTabAsync(draftTab.TabId, ShellTabCloseReason.Replaced).GetAwaiter().GetResult();
        SyncControl();
        return replaced;
    }

    public bool TrySelectRelativeTab(int delta)
    {
        var tabControl = _sessionTabs.GetTabControl();
        if (tabControl is null || tabControl.Tabs.Count == 0)
        {
            return false;
        }

        var selectedIndex = ResolveSelectedIndex(tabControl);
        var targetIndex = GetAdjacentTabIndex(selectedIndex, tabControl.Tabs.Count, delta);
        if (targetIndex == selectedIndex)
        {
            return false;
        }

        tabControl.SelectedIndex = targetIndex;
        OnSelectionChanged(targetIndex);
        return true;
    }

    internal static int GetAdjacentTabIndex(int selectedIndex, int tabCount, int delta)
    {
        if (tabCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tabCount));
        }

        if (selectedIndex < 0 || selectedIndex >= tabCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        }

        if (delta == 0 || tabCount == 1)
        {
            return selectedIndex;
        }

        var offset = delta % tabCount;
        var targetIndex = (selectedIndex + offset) % tabCount;
        return targetIndex < 0 ? targetIndex + tabCount : targetIndex;
    }

    private SessionTabStripProjection BuildProjection()
    {
        var workspaceView = _sessionTabs.GetWorkspaceView()
            ?? throw new InvalidOperationException("Session workspace view is not initialized.");

        PruneMissingSessionShellTabs(workspaceView);
        RestoreSessionTabsFromViewState(workspaceView);
        EnsureSelectedSessionSurfaceShellTab(workspaceView);
        EnsureCurrentSessionSurfaceShellTabSelected();
        EnsureSelectedShellTab();
        return SessionTabStripProjectionBuilder.Build(_shellTabs.GetTabs());
    }

    private void PruneMissingSessionShellTabs(SessionWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        var missingSessionTabs = _shellTabs.GetTabs()
            .Where(tab => tab.Kind == ShellTabKind.Session && _sessionSelection.FindSession(tab.TabId.Value) is null)
            .ToArray();
        foreach (var tab in missingSessionTabs)
        {
            _shellTabs.CloseTabAsync(tab.TabId, ShellTabCloseReason.SessionDeleted).GetAwaiter().GetResult();
            workspaceView.RemoveTabPage(tab.TabId.Value);
        }
    }

    private void RestoreSessionTabsFromViewState(SessionWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        if (_restoredSessionTabsFromViewState)
        {
            return;
        }

        if (_sessionSelection.OpenSessionIds.Count == 0)
        {
            return;
        }

        var restoredAny = false;

        foreach (var sessionId in _sessionSelection.OpenSessionIds)
        {
            if (_sessionSelection.FindSession(sessionId) is { } session)
            {
                EnsureSessionShellTab(workspaceView, session);
                restoredAny = true;
            }
        }

        if (!restoredAny)
        {
            return;
        }

        _restoredSessionTabsFromViewState = true;

        if (_sessionSelection.Selection.Target is WorkspaceTarget.Draft)
        {
            EnsureDraftShellTab(workspaceView);
        }

        EnsureCurrentSessionSurfaceShellTabSelected();
    }

    private void EnsureCurrentSessionSurfaceShellTabSelected()
    {
        var selectedTabId = ResolveSessionSurfaceSelectedTabId();
        if (string.IsNullOrWhiteSpace(selectedTabId) ||
            !_shellTabs.TryGetTab(new ShellTabId(selectedTabId), out var selectedTab) ||
            selectedTab.IsSelected)
        {
            return;
        }

        var currentSelectedTab = _shellTabs.GetTabs().FirstOrDefault(static tab => tab.IsSelected);
        if (currentSelectedTab is not null &&
            currentSelectedTab.Kind is not (ShellTabKind.PromptDraft or ShellTabKind.Session))
        {
            return;
        }

        _shellTabs.SelectTabAsync(selectedTab.TabId).GetAwaiter().GetResult();
    }

    private void EnsureSelectedSessionSurfaceShellTab(SessionWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        var selectedTabId = ResolveSessionSurfaceSelectedTabId();
        if (string.Equals(selectedTabId, CodeAltaApp.DraftTabId, StringComparison.Ordinal))
        {
            EnsureDraftShellTab(workspaceView);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedTabId) && _sessionSelection.FindSession(selectedTabId) is { } session)
        {
            EnsureSessionShellTab(workspaceView, session);
        }
    }

    private void EnsureSelectedShellTab()
    {
        var visibleTabs = _shellTabs.GetTabs()
            .Where(static tab => tab.Kind is ShellTabKind.PromptDraft or ShellTabKind.Session or ShellTabKind.Editor or ShellTabKind.Plugin)
            .ToArray();
        if (visibleTabs.Any(static tab => tab.IsSelected))
        {
            return;
        }

        var selectedTabId = ResolveSessionSurfaceSelectedTabId();
        if (!string.IsNullOrWhiteSpace(selectedTabId) &&
            _shellTabs.TryGetTab(new ShellTabId(selectedTabId), out var selectedTab))
        {
            _shellTabs.SelectTabAsync(selectedTab.TabId).GetAwaiter().GetResult();
            return;
        }

        if (visibleTabs.FirstOrDefault() is { } firstTab)
        {
            _shellTabs.SelectTabAsync(firstTab.TabId).GetAwaiter().GetResult();
        }
    }

    private string? ResolveSessionSurfaceSelectedTabId()
    {
        var selection = _sessionSelection.Selection;
        return selection.Target is WorkspaceTarget.Draft
            ? CodeAltaApp.DraftTabId
            : selection.SelectedSessionId;
    }

    private List<TabPage> BuildDesiredPages(SessionTabStripProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var pages = new List<TabPage>(projection.Tabs.Count);
        foreach (var tab in projection.Tabs)
        {
            pages.Add(tab.Kind switch
            {
                ShellTabKind.PromptDraft => EnsureDraftPage(CanCloseTab(tab, projection.Tabs.Count)),
                ShellTabKind.Editor => EnsureFilePage(tab.TabId),
                ShellTabKind.Plugin => EnsureGenericShellPage(tab.TabId, canClose: CanCloseTab(tab, projection.Tabs.Count)),
                _ => EnsureSessionPage(tab.TabId, CanCloseTab(tab, projection.Tabs.Count)),
            });
        }

        return pages;
    }

    internal static bool CanCloseTab(SessionTabStripItemProjection tab, int totalTabCount)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalTabCount);
        return tab.CanClose && (!tab.IsDraft || totalTabCount > 1);
    }

    private TabPage EnsureSessionPage(string sessionId, bool canClose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var workspaceView = _sessionTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Session workspace view is not initialized.");

        var session = _sessionSelection.FindSession(sessionId);
        if (session is null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' was not found when creating a tab page.");
        }

        if (workspaceView.TryGetTabPage(sessionId, out var existingPage))
        {
            existingPage.Data = CreateSessionPageData(sessionId);
            existingPage.ShowCloseButton = canClose;
            return existingPage;
        }

        var shellTab = EnsureSessionShellTab(workspaceView, session);

        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new SessionTabPageData(session.SessionId, shellTab.Kind, shellTab.ViewModel),
            ShowCloseButton = canClose,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !TryGetTabId(e.Page, out var currentSessionId))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseTabFromViewAsync(currentSessionId, ShellTabCloseReason.UserDetached);
        };

        workspaceView.RememberTabPage(session.SessionId, page);
        return page;
    }

    private ShellTabSnapshot EnsureSessionShellTab(SessionWorkspaceView workspaceView, SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(session);

        if (_shellTabs.TryGetTab(new ShellTabId(session.SessionId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        var header = _sessionTabs.CreateComputedVisual(
            () =>
            {
                return new HStack(
                [
                    SessionTabVisualFactory.CreateIndicator(IsSessionTabBusy(tab), tab.HasPromptDraft, tab.ViewModel.StatusTone),
                    SessionTabVisualFactory.CreateTitle(SessionTabVisualFactory.CompactTitle(tab.ViewModel.Title)),
                ])
                {
                    Spacing = 1,
                };
            });

        return OpenSessionShellTab(workspaceView, session, tab, header);
    }

    private bool IsSessionTabBusy(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.ViewModel.StatusBusy || _isRuntimeSessionRunning(tab.SessionView.SessionId);
    }

    private TabPage EnsureFilePage(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        var workspaceView = _sessionTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Session workspace view is not initialized.");

        if (workspaceView.TryGetTabPage(tabId, out var existingPage))
        {
            existingPage.Data = CreateFilePageData(tabId);
            existingPage.ShowCloseButton = true;
            return existingPage;
        }

        if (_shellTabs.TryGetTab(new ShellTabId(tabId), out var existingShellTab))
        {
            var existingShellPage = CreateFilePage(tabId, existingShellTab);
            workspaceView.RememberTabPage(tabId, existingShellPage);
            return existingShellPage;
        }

        throw new InvalidOperationException($"File shell tab '{tabId}' was not found when creating a tab page.");
    }

    private TabPage EnsureDraftPage(bool canClose)
    {
        var workspaceView = _sessionTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Session workspace view is not initialized.");
        if (workspaceView.TryGetTabPage(CodeAltaApp.DraftTabId, out var existingPage))
        {
            existingPage.Data = CreateDraftPageData();
            existingPage.ShowCloseButton = canClose;
            return existingPage;
        }

        if (_shellTabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var existingShellTab))
        {
            var existingShellPage = CreateDraftPage(existingShellTab, canClose);
            workspaceView.RememberTabPage(CodeAltaApp.DraftTabId, existingShellPage);
            return existingShellPage;
        }

        var shellTab = EnsureDraftShellTab(workspaceView);

        var page = CreateDraftPage(shellTab, canClose);

        workspaceView.RememberTabPage(CodeAltaApp.DraftTabId, page);
        return page;
    }

    private ShellTabSnapshot EnsureDraftShellTab(SessionWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);

        if (_shellTabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var header = _sessionTabs.CreateComputedVisual(
            () => new HStack(
            [
                SessionTabVisualFactory.CreateIndicator(isBusy: false, _hasDraftPrompt(), StatusTone.Info),
                SessionTabVisualFactory.CreateTitle(ShellTextFormatter.BuildDraftTabTitle(
                    _sessionSelection.GetSelectedProject(),
                    _sessionSelection.IsGlobalDraftSelected())),
            ])
            {
                Spacing = 1,
            });
        return _shellTabs.OpenOrGetTab(new ShellTabDescriptor
        {
            TabId = new ShellTabId(CodeAltaApp.DraftTabId),
            Kind = ShellTabKind.PromptDraft,
            Association = new ShellTabAssociation.PromptDraft(new PromptSessionBinding(
                new PromptSessionId(CodeAltaApp.DraftTabId),
                ProjectId.NewVersion7(),
                new ShellSessionRef.Draft(new SessionDraftId(CodeAltaApp.DraftTabId)),
                new CodeAlta.Agent.ModelProviderId("legacy-selected-provider"))),
            Header = header,
            Content = workspaceView.CreateSessionTabContent(
                CodeAltaApp.DraftTabId,
                _sessionTabs.CreateComputedVisual(
                    () => WelcomePaneFactory.Build(
                        _sessionSelection.GetSelectedProject(),
                        _sessionSelection.IsGlobalDraftSelected(),
                        _welcomeAnimationPhase01)),
                session: null),
            ViewModel = new DraftTabProjectionHandle(CodeAltaApp.DraftTabId),
        });
    }

    private void SyncSelection(SessionTabStripProjection projection, TabControl tabControl)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(tabControl);

        if (tabControl.Tabs.Count == 0 || string.IsNullOrWhiteSpace(projection.SelectedTabId))
        {
            return;
        }

        var selectedIndex = -1;
        for (var i = 0; i < tabControl.Tabs.Count; i++)
        {
            if (TryGetTabId(tabControl.Tabs[i], out var tabId) &&
                string.Equals(tabId, projection.SelectedTabId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex < 0 || tabControl.SelectedIndex == selectedIndex)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            tabControl.SelectedIndex = selectedIndex;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private int ResolveSelectedIndex(TabControl tabControl)
    {
        ArgumentNullException.ThrowIfNull(tabControl);

        if (tabControl.SelectedIndex >= 0 && tabControl.SelectedIndex < tabControl.Tabs.Count)
        {
            return tabControl.SelectedIndex;
        }

        var selectedTabId = _shellTabs.GetTabs()
            .FirstOrDefault(static tab => tab.IsSelected)
            ?.TabId.Value ?? ResolveSessionSurfaceSelectedTabId();

        if (!string.IsNullOrWhiteSpace(selectedTabId))
        {
            for (var i = 0; i < tabControl.Tabs.Count; i++)
            {
                if (TryGetTabId(tabControl.Tabs[i], out var tabId) &&
                    string.Equals(tabId, selectedTabId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static SessionTabPageData CreateDraftPageData()
        => new(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, new DraftTabProjectionHandle(CodeAltaApp.DraftTabId));

    private TabPage CreateDraftPage(ShellTabSnapshot shellTab, bool canClose)
    {
        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new SessionTabPageData(CodeAltaApp.DraftTabId, shellTab.Kind, shellTab.ViewModel),
            ShowCloseButton = canClose,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !IsDraftTab(e.Page))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseTabFromViewAsync(CodeAltaApp.DraftTabId, ShellTabCloseReason.UserDetached);
        };

        return page;
    }

    private SessionTabPageData CreateSessionPageData(string sessionId)
    {
        var session = _sessionSelection.FindSession(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' was not found when updating a tab page.");
        return new SessionTabPageData(sessionId, ShellTabKind.Session, _sessionSelection.EnsureSessionTab(session).ViewModel);
    }

    private SessionTabPageData CreateFilePageData(string tabId)
    {
        var fileTab = _sessionTabs.GetFileTab(tabId)
            ?? throw new InvalidOperationException($"File tab '{tabId}' was not found when updating a tab page.");
        return new SessionTabPageData(tabId, ShellTabKind.Editor, fileTab);
    }

    private TabPage CreateFilePage(string tabId, ShellTabSnapshot shellTab)
    {
        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new SessionTabPageData(tabId, shellTab.Kind, shellTab.ViewModel),
            ShowCloseButton = true,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !TryGetTabId(e.Page, out var currentTabId))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseTabFromViewAsync(currentTabId, ShellTabCloseReason.FileEditorClosed);
        };

        return page;
    }

    private TabPage EnsureGenericShellPage(string tabId, bool canClose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        var workspaceView = _sessionTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Session workspace view is not initialized.");

        if (workspaceView.TryGetTabPage(tabId, out var existingPage))
        {
            if (!_shellTabs.TryGetTab(new ShellTabId(tabId), out var existingShellTab))
            {
                throw new InvalidOperationException($"Shell tab '{tabId}' was not found when updating a tab page.");
            }

            existingPage.Data = new SessionTabPageData(tabId, existingShellTab.Kind, existingShellTab.ViewModel);
            existingPage.ShowCloseButton = canClose;
            return existingPage;
        }

        if (!_shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab))
        {
            throw new InvalidOperationException($"Shell tab '{tabId}' was not found when creating a tab page.");
        }

        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new SessionTabPageData(tabId, shellTab.Kind, shellTab.ViewModel),
            ShowCloseButton = canClose,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !TryGetTabId(e.Page, out var currentTabId))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseTabFromViewAsync(currentTabId, ShellTabCloseReason.UserDetached);
        };

        workspaceView.RememberTabPage(tabId, page);
        return page;
    }

    private static bool IsDraftTab(TabPage page)
        => TryGetTabId(page, out var tabId) &&
           string.Equals(tabId, CodeAltaApp.DraftTabId, StringComparison.Ordinal);

    private Task<bool> CloseTabFromViewAsync(string tabId, ShellTabCloseReason reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return _shellTabs.TryGetTab(new ShellTabId(tabId), out var tab)
            ? CloseTabAsync(tab, reason, cancellationToken)
            : Task.FromResult(false);
    }

    private async Task<bool> CloseTabAsync(ShellTabSnapshot tab, ShellTabCloseReason reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tab.CanClose)
        {
            return false;
        }

        switch (tab.Kind)
        {
            case ShellTabKind.Editor:
                _sessionTabs.CloseFileTab(tab.TabId.Value);
                return true;
            case ShellTabKind.PromptDraft:
                _sessionTabs.CloseDraftTab();
                return true;
            case ShellTabKind.Session:
                _sessionTabs.CloseSessionTab(tab.TabId.Value);
                return true;
            default:
                return await _shellTabs.CloseTabAsync(tab.TabId, reason, cancellationToken);
        }
    }

    private void SyncActiveSessionTabContent(SessionWorkspaceView workspaceView, TabControl tabControl)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(tabControl);

        if (tabControl.SelectedIndex < 0 || tabControl.SelectedIndex >= tabControl.Tabs.Count ||
            !TryGetTabData(tabControl.Tabs[tabControl.SelectedIndex], out var selectedTab))
        {
            workspaceView.ActivateSessionTabContent(null);
            return;
        }

        if (selectedTab.Kind is ShellTabKind.Editor or ShellTabKind.Plugin)
        {
            workspaceView.ActivateSessionTabContent(null);
            return;
        }

        workspaceView.ActivateSessionTabContent(selectedTab.TabId);
    }

    private static bool TryGetTabId(TabPage page, out string tabId)
    {
        if (TryGetTabData(page, out var data))
        {
            tabId = data.TabId;
            return true;
        }

        tabId = string.Empty;
        return false;
    }

    private static bool TryGetTabData(TabPage page, out SessionTabPageData data)
    {
        switch (page.Data)
        {
            case SessionTabPageData current:
                data = current;
                return true;
            case string legacyTabId:
                data = new SessionTabPageData(legacyTabId, ShellTabKind.Session, legacyTabId);
                return true;
            default:
                data = default!;
                return false;
        }
    }

    private sealed record SessionTabPageData(string TabId, ShellTabKind Kind, object ViewModel);

    private sealed record DraftTabProjectionHandle(string TabId);

    private ShellTabSnapshot OpenSessionShellTab(
        SessionWorkspaceView workspaceView,
        SessionViewDescriptor session,
        OpenSessionState tab,
        Visual header)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(header);

        if (_shellTabs.TryGetTab(new ShellTabId(session.SessionId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var projectId = ProjectId.TryParse(session.ProjectRef, out var parsedProjectId)
            ? parsedProjectId
            : ProjectId.NewVersion7();
        return _shellTabs.OpenOrGetTab(new ShellTabDescriptor
        {
            TabId = new ShellTabId(session.SessionId),
            Kind = ShellTabKind.Session,
            Association = new ShellTabAssociation.Session(
                session.SessionId,
                new PromptSessionId(session.SessionId),
                projectId,
                new CodeAlta.Agent.ModelProviderId(session.ProviderKey ?? session.ProviderId)),
            Header = header,
            Content = workspaceView.CreateSessionTabContent(session.SessionId, tab.Timeline.Flow, tab.Session),
            ViewModel = tab.ViewModel,
        });
    }
}
