using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Tabs;

internal sealed class ThreadTabStripCoordinator
{
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ThreadTabContext _threadTabs;
    private readonly IShellTabService _shellTabs;
    private bool _syncingSelection;
    private bool _syncingPages;
    private int _lastObservedSelectedIndex = -1;
    private string? _pendingThreadSelectionThreadId;
    private bool _restoredThreadTabsFromViewState;

    public ThreadTabStripCoordinator(
        ThreadSelectionContext threadSelection,
        ThreadTabContext threadTabs,
        IShellTabService shellTabs)
    {
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(threadTabs);
        ArgumentNullException.ThrowIfNull(shellTabs);

        _threadSelection = threadSelection;
        _threadTabs = threadTabs;
        _shellTabs = shellTabs;
    }

    public void SyncControl()
    {
        var tabControl = _threadTabs.GetTabControl();
        if (tabControl is null)
        {
            return;
        }

        var workspaceView = _threadTabs.GetWorkspaceView();
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
            SyncActiveThreadTabContent(workspaceView, tabControl);
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
        var tabControl = _threadTabs.GetTabControl();
        if (_syncingSelection || _syncingPages || tabControl is null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= tabControl.Tabs.Count)
        {
            return;
        }

        var workspaceView = _threadTabs.GetWorkspaceView();
        if (workspaceView is not null)
        {
            SyncActiveThreadTabContent(workspaceView, tabControl);
        }

        var selection = _threadSelection.Selection;
        if (IsDraftTab(tabControl.Tabs[selectedIndex]))
        {
            SelectShellTab(CodeAltaApp.DraftTabId);
            if (selection.Target is WorkspaceTarget.Draft)
            {
                _threadTabs.ActivateThreadSurface();
                return;
            }

            _threadTabs.ActivateDraftTab();
            return;
        }

        if (!TryGetTabId(tabControl.Tabs[selectedIndex], out var tabId))
        {
            return;
        }

        if (_threadTabs.GetFileTab(tabId) is not null)
        {
            _threadTabs.SelectFileTab(tabId);
            return;
        }

        if (_shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab) && shellTab.Kind == ShellTabKind.Plugin)
        {
            SelectShellTab(tabId);
            return;
        }

        SelectShellTab(tabId);
        if (string.Equals(tabId, selection.SelectedThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _threadTabs.ActivateThreadSurface();
            return;
        }

        if (string.Equals(tabId, _pendingThreadSelectionThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingThreadSelectionThreadId = tabId;
        _threadTabs.GetUiDispatcher().Post(
            () =>
            {
                if (!string.Equals(tabId, _pendingThreadSelectionThreadId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pendingThreadSelectionThreadId = null;
                _threadTabs.OpenThread(tabId);
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
        _pendingThreadSelectionThreadId = null;
    }

    public void SelectCurrentThreadSurfaceTab()
    {
        var workspaceView = _threadTabs.GetWorkspaceView();
        if (workspaceView is null)
        {
            return;
        }

        EnsureSelectedThreadSurfaceShellTab(workspaceView);
        var selectedTabId = ResolveThreadSurfaceSelectedTabId();
        if (string.IsNullOrWhiteSpace(selectedTabId) ||
            !_shellTabs.TryGetTab(new ShellTabId(selectedTabId), out var selectedTab) ||
            selectedTab.IsSelected)
        {
            return;
        }

        _shellTabs.SelectTabAsync(selectedTab.TabId).GetAwaiter().GetResult();
    }

    public bool ReplaceDraftTabWithThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        ResetPendingSelection();
        if (_threadTabs.GetWorkspaceView() is { } workspaceView && _threadSelection.FindThread(threadId) is { } thread)
        {
            EnsureThreadShellTab(workspaceView, thread);
        }

        if (_shellTabs.TryGetTab(new ShellTabId(threadId), out var threadTab) && !threadTab.IsSelected)
        {
            _shellTabs.SelectTabAsync(threadTab.TabId).GetAwaiter().GetResult();
        }

        var replaced = _shellTabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var draftTab) &&
            _shellTabs.CloseTabAsync(draftTab.TabId, ShellTabCloseReason.Replaced).GetAwaiter().GetResult();
        SyncControl();
        return replaced;
    }

    public bool TrySelectRelativeTab(int delta)
    {
        var tabControl = _threadTabs.GetTabControl();
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

    private ThreadTabStripProjection BuildProjection()
    {
        var workspaceView = _threadTabs.GetWorkspaceView()
            ?? throw new InvalidOperationException("Thread workspace view is not initialized.");

        RestoreThreadTabsFromViewState(workspaceView);
        EnsureSelectedThreadSurfaceShellTab(workspaceView);
        EnsureCurrentThreadSurfaceShellTabSelected();
        EnsureSelectedShellTab();
        return ThreadTabStripProjectionBuilder.Build(_shellTabs.GetTabs());
    }

    private void RestoreThreadTabsFromViewState(ThreadWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        if (_restoredThreadTabsFromViewState)
        {
            return;
        }

        if (_threadSelection.OpenThreadIds.Count == 0)
        {
            return;
        }

        var restoredAny = false;

        foreach (var threadId in _threadSelection.OpenThreadIds)
        {
            if (_threadSelection.FindThread(threadId) is { } thread)
            {
                EnsureThreadShellTab(workspaceView, thread);
                restoredAny = true;
            }
        }

        if (!restoredAny)
        {
            return;
        }

        _restoredThreadTabsFromViewState = true;

        if (_threadSelection.Selection.Target is WorkspaceTarget.Draft)
        {
            EnsureDraftShellTab(workspaceView);
        }

        EnsureCurrentThreadSurfaceShellTabSelected();
    }

    private void EnsureCurrentThreadSurfaceShellTabSelected()
    {
        var selectedTabId = ResolveThreadSurfaceSelectedTabId();
        if (string.IsNullOrWhiteSpace(selectedTabId) ||
            !_shellTabs.TryGetTab(new ShellTabId(selectedTabId), out var selectedTab) ||
            selectedTab.IsSelected)
        {
            return;
        }

        var currentSelectedTab = _shellTabs.GetTabs().FirstOrDefault(static tab => tab.IsSelected);
        if (currentSelectedTab is not null &&
            currentSelectedTab.Kind is not (ShellTabKind.PromptDraft or ShellTabKind.Thread))
        {
            return;
        }

        _shellTabs.SelectTabAsync(selectedTab.TabId).GetAwaiter().GetResult();
    }

    private void EnsureSelectedThreadSurfaceShellTab(ThreadWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        var selectedTabId = ResolveThreadSurfaceSelectedTabId();
        if (string.Equals(selectedTabId, CodeAltaApp.DraftTabId, StringComparison.Ordinal))
        {
            EnsureDraftShellTab(workspaceView);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectedTabId) && _threadSelection.FindThread(selectedTabId) is { } thread)
        {
            EnsureThreadShellTab(workspaceView, thread);
        }
    }

    private void EnsureSelectedShellTab()
    {
        var visibleTabs = _shellTabs.GetTabs()
            .Where(static tab => tab.Kind is ShellTabKind.PromptDraft or ShellTabKind.Thread or ShellTabKind.Editor or ShellTabKind.Plugin)
            .ToArray();
        if (visibleTabs.Any(static tab => tab.IsSelected))
        {
            return;
        }

        var selectedTabId = ResolveThreadSurfaceSelectedTabId();
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

    private string? ResolveThreadSurfaceSelectedTabId()
    {
        var selection = _threadSelection.Selection;
        return selection.Target is WorkspaceTarget.Draft
            ? CodeAltaApp.DraftTabId
            : selection.SelectedThreadId;
    }

    private List<TabPage> BuildDesiredPages(ThreadTabStripProjection projection)
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
                _ => EnsureThreadPage(tab.TabId, CanCloseTab(tab, projection.Tabs.Count)),
            });
        }

        return pages;
    }

    internal static bool CanCloseTab(ThreadTabStripItemProjection tab, int totalTabCount)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalTabCount);
        return tab.CanClose && (!tab.IsDraft || totalTabCount > 1);
    }

    private TabPage EnsureThreadPage(string threadId, bool canClose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var workspaceView = _threadTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");

        var thread = _threadSelection.FindThread(threadId);
        if (thread is null)
        {
            throw new InvalidOperationException($"Thread '{threadId}' was not found when creating a tab page.");
        }

        if (workspaceView.TryGetTabPage(threadId, out var existingPage))
        {
            existingPage.Data = CreateThreadPageData(threadId);
            existingPage.ShowCloseButton = canClose;
            return existingPage;
        }

        var shellTab = EnsureThreadShellTab(workspaceView, thread);

        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new ThreadTabPageData(thread.ThreadId, shellTab.Kind, shellTab.ViewModel),
            ShowCloseButton = canClose,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !TryGetTabId(e.Page, out var currentThreadId))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseTabFromViewAsync(currentThreadId, ShellTabCloseReason.UserDetached);
        };

        workspaceView.RememberTabPage(thread.ThreadId, page);
        return page;
    }

    private ShellTabSnapshot EnsureThreadShellTab(ThreadWorkspaceView workspaceView, WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(thread);

        if (_shellTabs.TryGetTab(new ShellTabId(thread.ThreadId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var header = _threadTabs.CreateComputedVisual(
            () =>
            {
                return new HStack(
                [
                    ThreadTabVisualFactory.CreateIndicator(tab.ViewModel.StatusBusy, tab.HasPromptDraft, tab.ViewModel.StatusTone),
                    ThreadTabVisualFactory.CreateTitle(ThreadTabVisualFactory.CompactTitle(tab.ViewModel.Title)),
                ])
                {
                    Spacing = 1,
                };
            });

        return OpenThreadShellTab(workspaceView, thread, tab, header);
    }

    private TabPage EnsureFilePage(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        var workspaceView = _threadTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");

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
        var workspaceView = _threadTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");
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

    private ShellTabSnapshot EnsureDraftShellTab(ThreadWorkspaceView workspaceView)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);

        if (_shellTabs.TryGetTab(new ShellTabId(CodeAltaApp.DraftTabId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var header = _threadTabs.CreateComputedVisual(
            () => new HStack(
            [
                ThreadTabVisualFactory.CreateIndicator(isBusy: false, StatusTone.Info),
                ThreadTabVisualFactory.CreateTitle(ShellTextFormatter.BuildDraftTabTitle(
                    _threadSelection.GetSelectedProject(),
                    _threadSelection.IsGlobalDraftSelected())),
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
                new ShellThreadRef.Draft(new ThreadDraftId(CodeAltaApp.DraftTabId)),
                new ModelProviderId("legacy-selected-provider"))),
            Header = header,
            Content = workspaceView.CreateThreadTabContent(
                CodeAltaApp.DraftTabId,
                _threadTabs.CreateComputedVisual(
                    () => WelcomePaneFactory.Build(
                        _threadSelection.GetSelectedProject(),
                        _threadSelection.IsGlobalDraftSelected(),
                        new State<float>(0))),
                session: null),
            ViewModel = new DraftTabProjectionHandle(CodeAltaApp.DraftTabId),
        });
    }

    private void SyncSelection(ThreadTabStripProjection projection, TabControl tabControl)
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
            ?.TabId.Value ?? ResolveThreadSurfaceSelectedTabId();

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

    private static ThreadTabPageData CreateDraftPageData()
        => new(CodeAltaApp.DraftTabId, ShellTabKind.PromptDraft, new DraftTabProjectionHandle(CodeAltaApp.DraftTabId));

    private TabPage CreateDraftPage(ShellTabSnapshot shellTab, bool canClose)
    {
        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new ThreadTabPageData(CodeAltaApp.DraftTabId, shellTab.Kind, shellTab.ViewModel),
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

    private ThreadTabPageData CreateThreadPageData(string threadId)
    {
        var thread = _threadSelection.FindThread(threadId)
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found when updating a tab page.");
        return new ThreadTabPageData(threadId, ShellTabKind.Thread, _threadSelection.EnsureThreadTab(thread).ViewModel);
    }

    private ThreadTabPageData CreateFilePageData(string tabId)
    {
        var fileTab = _threadTabs.GetFileTab(tabId)
            ?? throw new InvalidOperationException($"File tab '{tabId}' was not found when updating a tab page.");
        return new ThreadTabPageData(tabId, ShellTabKind.Editor, fileTab);
    }

    private TabPage CreateFilePage(string tabId, ShellTabSnapshot shellTab)
    {
        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new ThreadTabPageData(tabId, shellTab.Kind, shellTab.ViewModel),
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
        var workspaceView = _threadTabs.GetWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");

        if (workspaceView.TryGetTabPage(tabId, out var existingPage))
        {
            if (!_shellTabs.TryGetTab(new ShellTabId(tabId), out var existingShellTab))
            {
                throw new InvalidOperationException($"Shell tab '{tabId}' was not found when updating a tab page.");
            }

            existingPage.Data = new ThreadTabPageData(tabId, existingShellTab.Kind, existingShellTab.ViewModel);
            existingPage.ShowCloseButton = canClose;
            return existingPage;
        }

        if (!_shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab))
        {
            throw new InvalidOperationException($"Shell tab '{tabId}' was not found when creating a tab page.");
        }

        var page = new TabPage(shellTab.Header, shellTab.Content)
        {
            Data = new ThreadTabPageData(tabId, shellTab.Kind, shellTab.ViewModel),
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
                _threadTabs.CloseFileTab(tab.TabId.Value);
                return true;
            case ShellTabKind.PromptDraft:
                _threadTabs.CloseDraftTab();
                return true;
            case ShellTabKind.Thread:
                _threadTabs.CloseThreadTab(tab.TabId.Value);
                return true;
            default:
                return await _shellTabs.CloseTabAsync(tab.TabId, reason, cancellationToken);
        }
    }

    private void SyncActiveThreadTabContent(ThreadWorkspaceView workspaceView, TabControl tabControl)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(tabControl);

        if (tabControl.SelectedIndex < 0 || tabControl.SelectedIndex >= tabControl.Tabs.Count ||
            !TryGetTabData(tabControl.Tabs[tabControl.SelectedIndex], out var selectedTab))
        {
            workspaceView.ActivateThreadTabContent(null);
            return;
        }

        if (selectedTab.Kind is ShellTabKind.Editor or ShellTabKind.Plugin)
        {
            workspaceView.ActivateThreadTabContent(null);
            return;
        }

        workspaceView.ActivateThreadTabContent(selectedTab.TabId);
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

    private static bool TryGetTabData(TabPage page, out ThreadTabPageData data)
    {
        switch (page.Data)
        {
            case ThreadTabPageData current:
                data = current;
                return true;
            case string legacyTabId:
                data = new ThreadTabPageData(legacyTabId, ShellTabKind.Thread, legacyTabId);
                return true;
            default:
                data = default!;
                return false;
        }
    }

    private sealed record ThreadTabPageData(string TabId, ShellTabKind Kind, object ViewModel);

    private sealed record DraftTabProjectionHandle(string TabId);

    private ShellTabSnapshot OpenThreadShellTab(
        ThreadWorkspaceView workspaceView,
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        Visual header)
    {
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(header);

        if (_shellTabs.TryGetTab(new ShellTabId(thread.ThreadId), out var existingShellTab))
        {
            return existingShellTab;
        }

        var projectId = ProjectId.TryParse(thread.ProjectRef, out var parsedProjectId)
            ? parsedProjectId
            : ProjectId.NewVersion7();
        return _shellTabs.OpenOrGetTab(new ShellTabDescriptor
        {
            TabId = new ShellTabId(thread.ThreadId),
            Kind = ShellTabKind.Thread,
            Association = new ShellTabAssociation.Thread(
                thread.ThreadId,
                new PromptSessionId(thread.ThreadId),
                projectId,
                new ModelProviderId(thread.ProviderKey ?? thread.BackendId)),
            Header = header,
            Content = workspaceView.CreateThreadTabContent(thread.ThreadId, tab.Timeline.Flow, tab.Session),
            ViewModel = tab.ViewModel,
        });
    }
}
