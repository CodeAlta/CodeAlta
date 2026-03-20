using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Tabs;

internal sealed class ThreadTabStripCoordinator
{
    private readonly Func<TabControl?> _getTabControl;
    private readonly Func<ThreadWorkspaceView?> _getWorkspaceView;
    private readonly Func<IReadOnlyList<string>> _getOpenThreadIds;
    private readonly Func<bool> _getDraftTabOpen;
    private readonly Func<string?> _getSelectedThreadId;
    private readonly Func<bool> _getGlobalScopeSelected;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<string, WorkThreadDescriptor?> _findThread;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Func<Func<Visual>, ComputedVisual> _createComputedVisual;
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private readonly Action _activateDraftTab;
    private readonly Action<string> _closeThread;
    private readonly Action _closeDraftTab;
    private readonly Action<string> _openThread;
    private bool _syncingSelection;
    private bool _syncingPages;
    private string? _pendingThreadSelectionThreadId;

    public ThreadTabStripCoordinator(
        Func<TabControl?> getTabControl,
        Func<ThreadWorkspaceView?> getWorkspaceView,
        Func<IReadOnlyList<string>> getOpenThreadIds,
        Func<bool> getDraftTabOpen,
        Func<string?> getSelectedThreadId,
        Func<bool> getGlobalScopeSelected,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<string, WorkThreadDescriptor?> findThread,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Func<Func<Visual>, ComputedVisual> createComputedVisual,
        Func<IUiDispatcher> getUiDispatcher,
        Action activateDraftTab,
        Action<string> closeThread,
        Action closeDraftTab,
        Action<string> openThread)
    {
        ArgumentNullException.ThrowIfNull(getTabControl);
        ArgumentNullException.ThrowIfNull(getWorkspaceView);
        ArgumentNullException.ThrowIfNull(getOpenThreadIds);
        ArgumentNullException.ThrowIfNull(getDraftTabOpen);
        ArgumentNullException.ThrowIfNull(getSelectedThreadId);
        ArgumentNullException.ThrowIfNull(getGlobalScopeSelected);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(findThread);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);
        ArgumentNullException.ThrowIfNull(activateDraftTab);
        ArgumentNullException.ThrowIfNull(closeThread);
        ArgumentNullException.ThrowIfNull(closeDraftTab);
        ArgumentNullException.ThrowIfNull(openThread);

        _getTabControl = getTabControl;
        _getWorkspaceView = getWorkspaceView;
        _getOpenThreadIds = getOpenThreadIds;
        _getDraftTabOpen = getDraftTabOpen;
        _getSelectedThreadId = getSelectedThreadId;
        _getGlobalScopeSelected = getGlobalScopeSelected;
        _getSelectedProject = getSelectedProject;
        _findThread = findThread;
        _ensureThreadTab = ensureThreadTab;
        _createComputedVisual = createComputedVisual;
        _getUiDispatcher = getUiDispatcher;
        _activateDraftTab = activateDraftTab;
        _closeThread = closeThread;
        _closeDraftTab = closeDraftTab;
        _openThread = openThread;
    }

    public void SyncControl()
    {
        var tabControl = _getTabControl();
        if (tabControl is null)
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
        }
        finally
        {
            _syncingPages = false;
        }
    }

    public void OnSelectionChanged(int selectedIndex)
    {
        var tabControl = _getTabControl();
        if (_syncingSelection || _syncingPages || tabControl is null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= tabControl.Tabs.Count)
        {
            return;
        }

        if (string.Equals(tabControl.Tabs[selectedIndex].Data as string, CodeAltaApp.DraftTabId, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(_getSelectedThreadId()))
            {
                return;
            }

            _activateDraftTab();
            return;
        }

        if (tabControl.Tabs[selectedIndex].Data is not string threadId ||
            string.Equals(threadId, _getSelectedThreadId(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(threadId, _pendingThreadSelectionThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingThreadSelectionThreadId = threadId;
        _getUiDispatcher().Post(
            () =>
            {
                if (!string.Equals(threadId, _pendingThreadSelectionThreadId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pendingThreadSelectionThreadId = null;
                _openThread(threadId);
            });
    }

    public void ResetPendingSelection()
    {
        _pendingThreadSelectionThreadId = null;
    }

    private ThreadTabStripProjection BuildProjection()
    {
        var availableThreadIds = _getOpenThreadIds()
            .Select(_findThread)
            .Where(static thread => thread is not null)
            .Select(thread =>
            {
                var current = thread!;
                _ensureThreadTab(current);
                return current.ThreadId;
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ThreadTabStripProjectionBuilder.Build(
            _getOpenThreadIds(),
            availableThreadIds,
            _getDraftTabOpen(),
            CodeAltaApp.DraftTabId,
            _getSelectedThreadId());
    }

    private List<TabPage> BuildDesiredPages(ThreadTabStripProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var pages = new List<TabPage>(projection.Tabs.Count);
        foreach (var tab in projection.Tabs)
        {
            pages.Add(tab.IsDraft
                ? EnsureDraftPage()
                : EnsureThreadPage(tab.TabId));
        }

        return pages;
    }

    private TabPage EnsureThreadPage(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var workspaceView = _getWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");

        if (workspaceView.TryGetTabPage(threadId, out var existingPage))
        {
            existingPage.Data = threadId;
            return existingPage;
        }

        var thread = _findThread(threadId);
        if (thread is null)
        {
            throw new InvalidOperationException($"Thread '{threadId}' was not found when creating a tab page.");
        }

        var tab = _ensureThreadTab(thread);
        var header = _createComputedVisual(
            () =>
            {
                var current = tab.Thread;
                return new HStack(
                [
                    ThreadTabVisualFactory.CreateIndicator(tab.ViewModel.StatusBusy, tab.ViewModel.StatusTone),
                    ThreadTabVisualFactory.CreateTitle(ThreadTabVisualFactory.CompactTitle(tab.ViewModel.Title)),
                ])
                {
                    Spacing = 1,
                }.Tooltip(current.Title);
            });

        var page = new TabPage(header, CodeAltaApp.CreateThreadTabPageContentPlaceholder())
        {
            Data = thread.ThreadId,
            ShowCloseButton = true,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || e.Page.Data is not string currentThreadId)
            {
                return;
            }

            e.Cancel = true;
            _closeThread(currentThreadId);
        };

        workspaceView.RememberTabPage(thread.ThreadId, page);
        return page;
    }

    private TabPage EnsureDraftPage()
    {
        var workspaceView = _getWorkspaceView() ?? throw new InvalidOperationException("Thread workspace view is not initialized.");
        if (workspaceView.TryGetTabPage(CodeAltaApp.DraftTabId, out var existingPage))
        {
            existingPage.Data = CodeAltaApp.DraftTabId;
            return existingPage;
        }

        var header = _createComputedVisual(
            () => new HStack(
            [
                ThreadTabVisualFactory.CreateIndicator(isBusy: false, StatusTone.Info),
                ThreadTabVisualFactory.CreateTitle(ShellTextFormatter.BuildDraftTabTitle(_getSelectedProject(), _getGlobalScopeSelected())),
            ])
            {
                Spacing = 1,
            });

        var page = new TabPage(header, CodeAltaApp.CreateThreadTabPageContentPlaceholder())
        {
            Data = CodeAltaApp.DraftTabId,
            ShowCloseButton = true,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !string.Equals(e.Page.Data as string, CodeAltaApp.DraftTabId, StringComparison.Ordinal))
            {
                return;
            }

            e.Cancel = true;
            _closeDraftTab();
        };

        workspaceView.RememberTabPage(CodeAltaApp.DraftTabId, page);
        return page;
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
            if (tabControl.Tabs[i].Data is string threadId &&
                string.Equals(threadId, projection.SelectedTabId, StringComparison.OrdinalIgnoreCase))
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
}