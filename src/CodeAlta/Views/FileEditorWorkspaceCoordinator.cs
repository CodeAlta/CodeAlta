using CodeAlta.Models;
using CodeAlta.Presentation.Editing;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Search;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Views;

internal sealed class FileEditorWorkspaceCoordinator : IAsyncDisposable
{
    private readonly Func<ThreadWorkspaceView?> _getWorkspaceView;
    private readonly Func<Visual?> _getThreadFocusTarget;
    private readonly Action<Action> _dispatchToUiDeferred;
    private readonly Action _syncThreadTabControl;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly ProjectFileOpenDialogController _filePickerController;
    private readonly Dictionary<string, FileEditorTab> _fileTabsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileEditorTab> _fileTabsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _openFileTabIds = [];
    private string? _selectedTabId;

    public FileEditorWorkspaceCoordinator(
        IProjectFileSearchService projectFileSearchService,
        Func<string?> resolveProjectRoot,
        Func<Visual?> getThreadFocusTarget,
        Func<ThreadWorkspaceView?> getWorkspaceView,
        Action<Action> dispatchToUiDeferred,
        Action syncThreadTabControl,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(resolveProjectRoot);
        ArgumentNullException.ThrowIfNull(getThreadFocusTarget);
        ArgumentNullException.ThrowIfNull(getWorkspaceView);
        ArgumentNullException.ThrowIfNull(dispatchToUiDeferred);
        ArgumentNullException.ThrowIfNull(syncThreadTabControl);
        ArgumentNullException.ThrowIfNull(setStatus);

        _getWorkspaceView = getWorkspaceView;
        _getThreadFocusTarget = getThreadFocusTarget;
        _dispatchToUiDeferred = dispatchToUiDeferred;
        _syncThreadTabControl = syncThreadTabControl;
        _setStatus = setStatus;
        _filePickerController = new ProjectFileOpenDialogController(
            projectFileSearchService,
            ProjectFileAppearanceRegistry.Default,
            resolveProjectRoot,
            GetActiveWorkspaceFocusTarget,
            OpenFileTab,
            setStatus);
    }

    public IReadOnlyList<string> OpenTabIds => _openFileTabIds;

    public string? SelectedTabId => _selectedTabId;

    public async ValueTask DisposeAsync()
    {
        await _filePickerController.DisposeAsync();
        foreach (var fileTab in _fileTabsById.Values.ToArray())
        {
            await fileTab.DisposeAsync();
        }
    }

    public Task ShowOpenFilePickerAsync()
        => _filePickerController.ShowAsync();

    public FileEditorTab? GetSelectedFileTab()
        => !string.IsNullOrWhiteSpace(_selectedTabId) && _fileTabsById.TryGetValue(_selectedTabId, out var fileTab)
            ? fileTab
            : null;

    public FileEditorTab? GetFileTab(string tabId)
        => _fileTabsById.GetValueOrDefault(tabId);

    public void SelectFileTab(string tabId)
    {
        if (!_fileTabsById.ContainsKey(tabId))
        {
            return;
        }

        _selectedTabId = tabId;
        RefreshActiveContent();
        _syncThreadTabControl();
        if (_fileTabsById.TryGetValue(tabId, out var fileTab))
        {
            _dispatchToUiDeferred(fileTab.Focus);
        }
    }

    public async Task CloseFileTabAsync(string tabId)
    {
        if (!_fileTabsById.TryGetValue(tabId, out var fileTab))
        {
            return;
        }

        var removedIndex = _openFileTabIds.FindIndex(candidate => string.Equals(candidate, tabId, StringComparison.OrdinalIgnoreCase));
        await fileTab.RequestCloseAsync(
            async () =>
            {
                await fileTab.DisposeAsync();
                _fileTabsById.Remove(tabId);
                _fileTabsByPath.Remove(fileTab.FullPath);
                _openFileTabIds.Remove(tabId);
                _getWorkspaceView()?.RemoveTabPage(tabId);
                if (string.Equals(_selectedTabId, tabId, StringComparison.OrdinalIgnoreCase))
                {
                    SelectRemainingFileTabOrThreadSurface(removedIndex);
                }

                _syncThreadTabControl();
                _setStatus($"Closed '{fileTab.Item.Basename}'.", false, StatusTone.Info);
            });
    }

    public void ActivateThreadSurface()
    {
        _selectedTabId = null;
        RefreshActiveContent();
        _syncThreadTabControl();
    }

    public void RefreshActiveContent()
    {
        var workspaceView = _getWorkspaceView();
        if (workspaceView is null)
        {
            return;
        }

        workspaceView.SetActiveTabContent(GetSelectedFileTab()?.Root ?? workspaceView.ThreadBodySplitter);
    }

    public Visual? GetActiveWorkspaceFocusTarget()
        => GetSelectedFileTab()?.Editor as Visual ?? _getThreadFocusTarget();

    private void OpenFileTab(ProjectFileSearchItem item, ProjectFileAppearance appearance)
        => _ = OpenFileTabAsync(item, appearance);

    private async Task OpenFileTabAsync(
        ProjectFileSearchItem item,
        ProjectFileAppearance appearance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(appearance);

        if (_fileTabsByPath.TryGetValue(item.FullPath, out var existingTab))
        {
            SelectFileTab(existingTab.TabId);
            existingTab.Focus();
            return;
        }

        try
        {
            var fileTab = await FileEditorTab.CreateAsync(item, appearance, (message, showSpinner, tone) => _setStatus(message, showSpinner, tone), cancellationToken);
            _fileTabsById[fileTab.TabId] = fileTab;
            _fileTabsByPath[fileTab.FullPath] = fileTab;
            _openFileTabIds.Add(fileTab.TabId);
            SelectFileTab(fileTab.TabId);
            _dispatchToUiDeferred(fileTab.Focus);
            _setStatus($"Opened '{item.Basename}' for editing.", false, StatusTone.Ready);
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to open '{item.RelativePath}': {ex.Message}", false, StatusTone.Error);
        }
    }

    private void SelectRemainingFileTabOrThreadSurface(int removedIndex)
    {
        if (_openFileTabIds.Count == 0)
        {
            ActivateThreadSurface();
            return;
        }

        var nextIndex = removedIndex <= 0
            ? 0
            : Math.Min(removedIndex - 1, _openFileTabIds.Count - 1);
        SelectFileTab(_openFileTabIds[nextIndex]);
    }
}
