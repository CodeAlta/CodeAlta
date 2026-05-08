using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Editing;
using CodeAlta.Presentation.Prompting;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class FileEditorWorkspaceCoordinator : IAsyncDisposable
{
    private readonly Func<ThreadWorkspaceView?> _getWorkspaceView;
    private readonly Func<Visual?> _getThreadFocusTarget;
    private readonly Action<Action> _dispatchToUiDeferred;
    private readonly Action _syncThreadTabControl;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly IShellTabService _shellTabs;
    private readonly Func<Func<Visual>, ComputedVisual> _createComputedVisual;
    private readonly ProjectFileOpenDialogController _filePickerController;
    private readonly Dictionary<string, FileEditorTab> _fileTabsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileEditorTab> _fileTabsByPath = new(StringComparer.OrdinalIgnoreCase);

    public FileEditorWorkspaceCoordinator(
        IProjectFileSearchService projectFileSearchService,
        IShellTabService shellTabs,
        Func<string?> resolveProjectRoot,
        Func<Visual?> getThreadFocusTarget,
        Func<ThreadWorkspaceView?> getWorkspaceView,
        Func<Func<Visual>, ComputedVisual> createComputedVisual,
        Action<Action> dispatchToUiDeferred,
        Action syncThreadTabControl,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(shellTabs);
        ArgumentNullException.ThrowIfNull(resolveProjectRoot);
        ArgumentNullException.ThrowIfNull(getThreadFocusTarget);
        ArgumentNullException.ThrowIfNull(getWorkspaceView);
        ArgumentNullException.ThrowIfNull(createComputedVisual);
        ArgumentNullException.ThrowIfNull(dispatchToUiDeferred);
        ArgumentNullException.ThrowIfNull(syncThreadTabControl);
        ArgumentNullException.ThrowIfNull(setStatus);

        _shellTabs = shellTabs;
        _getWorkspaceView = getWorkspaceView;
        _getThreadFocusTarget = getThreadFocusTarget;
        _createComputedVisual = createComputedVisual;
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

    public IReadOnlyList<string> OpenTabIds => GetOpenEditorTabIds();

    public string? SelectedTabId
        => _shellTabs.GetTabs()
            .FirstOrDefault(static shellTab => shellTab is { IsSelected: true, Kind: ShellTabKind.Editor })
            ?.TabId.Value;

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

    public Task OpenFilePathAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var resolvedPath = Path.GetFullPath(fullPath);
        if (!File.Exists(resolvedPath))
        {
            _setStatus($"Cannot open missing file '{resolvedPath}'.", false, StatusTone.Warning);
            return Task.CompletedTask;
        }

        var projectRoot = Path.GetDirectoryName(resolvedPath) ?? resolvedPath;
        var basename = Path.GetFileName(resolvedPath);
        var relativePath = basename;
        var extension = Path.GetExtension(resolvedPath);
        DateTimeOffset? lastWriteTimeUtc = null;
        try
        {
            lastWriteTimeUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(resolvedPath), TimeSpan.Zero);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var item = new ProjectFileSearchItem
        {
            Kind = ProjectFileSearchItemKind.File,
            ProjectRoot = projectRoot,
            RelativePath = relativePath,
            FullPath = resolvedPath,
            Basename = basename,
            ParentPath = projectRoot,
            Extension = extension,
            LastWriteTimeUtc = lastWriteTimeUtc,
            SearchFields = new ProjectFileSearchFields(
                basename.ToLowerInvariant(),
                relativePath.ToLowerInvariant(),
                [relativePath.ToLowerInvariant()],
                extension.ToLowerInvariant()),
        };

        return OpenFileTabAsync(item, ProjectFileAppearanceRegistry.Default.GetAppearance(item), cancellationToken);
    }

    public FileEditorTab? GetSelectedFileTab()
        => SelectedTabId is { Length: > 0 } selectedTabId && _fileTabsById.TryGetValue(selectedTabId, out var fileTab)
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

        _shellTabs.SelectTabAsync(new ShellTabId(tabId)).GetAwaiter().GetResult();
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

        var openTabIds = GetOpenEditorTabIds();
        var removedIndex = openTabIds.FindIndex(candidate => string.Equals(candidate, tabId, StringComparison.OrdinalIgnoreCase));
        var wasSelected = string.Equals(SelectedTabId, tabId, StringComparison.OrdinalIgnoreCase);
        await fileTab.RequestCloseAsync(
            async () =>
            {
                await fileTab.DisposeAsync();
                _fileTabsById.Remove(tabId);
                _fileTabsByPath.Remove(fileTab.FullPath);
                await _shellTabs.CloseTabAsync(new ShellTabId(tabId), ShellTabCloseReason.User);
                _getWorkspaceView()?.RemoveTabPage(tabId);
                if (wasSelected)
                {
                    SelectRemainingFileTabOrThreadSurface(removedIndex);
                }

                _syncThreadTabControl();
                _setStatus($"Closed '{fileTab.Item.Basename}'.", false, StatusTone.Info);
            });
    }

    public void ActivateThreadSurface()
    {
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
            _shellTabs.OpenOrGetTab(new ShellTabDescriptor
            {
                TabId = new ShellTabId(fileTab.TabId),
                Kind = ShellTabKind.Editor,
                Association = new ShellTabAssociation.Editor(ProjectId.NewVersion7(), fileTab.FullPath),
                Header = fileTab.CreateTabHeader(_createComputedVisual),
                Content = fileTab.Root,
                ViewModel = fileTab,
            });
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
        var openTabIds = GetOpenEditorTabIds();
        if (openTabIds.Count == 0)
        {
            ActivateThreadSurface();
            return;
        }

        var nextIndex = removedIndex <= 0
            ? 0
            : Math.Min(removedIndex - 1, openTabIds.Count - 1);
        SelectFileTab(openTabIds[nextIndex]);
    }

    private List<string> GetOpenEditorTabIds()
        => _shellTabs.GetTabs()
            .Where(static tab => tab.Kind == ShellTabKind.Editor)
            .Select(static tab => tab.TabId.Value)
            .ToList();
}
