using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Editing;

internal sealed class ProjectFileOpenDialogController : IAsyncDisposable
{
    private const int DialogMaximumResults = 64;
    private const int DialogProjectNameMaxLength = 28;
    private readonly IProjectFileSearchService _searchService;
    private readonly IProjectFileAppearanceRegistry _appearanceRegistry;
    private readonly Func<string?> _getProjectRoot;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action<ProjectFileSearchItem, ProjectFileAppearance> _openFile;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly ProjectFilePickerDialog _dialog;
    private readonly IReadOnlyList<ProjectFileReferencePopupItem> _emptyItems = [];
    private IReadOnlyList<ProjectFileReferencePopupItem> _items = [];
    private IProjectFileSearchSession? _session;
    private string? _projectRoot;
    private string _activeQuery = string.Empty;
    private string? _selectedRelativePath;
    private int _selectedIndex = -1;
    private int _candidateCount;
    private bool _isRefreshing;

    public ProjectFileOpenDialogController(
        IProjectFileSearchService searchService,
        IProjectFileAppearanceRegistry appearanceRegistry,
        Func<string?> getProjectRoot,
        Func<Visual?> getFocusTarget,
        Action<ProjectFileSearchItem, ProjectFileAppearance> openFile,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(appearanceRegistry);
        ArgumentNullException.ThrowIfNull(getProjectRoot);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(openFile);
        ArgumentNullException.ThrowIfNull(setStatus);

        _searchService = searchService;
        _appearanceRegistry = appearanceRegistry;
        _getProjectRoot = getProjectRoot;
        _getFocusTarget = getFocusTarget;
        _openFile = openFile;
        _setStatus = setStatus;
        _dialog = new ProjectFilePickerDialog(SR.T("Arrows move · Enter open · Esc close"));
        _dialog.QueryChanged += (_, queryText) => _ = OnDialogQueryChangedAsync(queryText);
        _dialog.SelectionChanged += (_, selectedIndex) => OnSelectionChanged(selectedIndex);
        _dialog.AcceptRequested += (_, _) => AcceptSelected();
        _dialog.DismissRequested += (_, _) => _ = CloseAsync();
        RefreshDialogLabels();
    }

    public bool IsOpen => _dialog.IsOpen;

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        var focusTarget = _getFocusTarget();
        var app = focusTarget?.App;
        if (app is null)
        {
            _setStatus(SR.T("The file picker is unavailable until the workspace is initialized."), false, StatusTone.Warning);
            return;
        }

        var projectRoot = _getProjectRoot();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            _setStatus(SR.T("Select a project or a project-backed session before opening a file."), false, StatusTone.Warning);
            return;
        }

        if (_dialog.IsOpen && string.Equals(_projectRoot, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            _dialog.Show(app);
            return;
        }

        await CloseAsync();

        _projectRoot = projectRoot;
        _activeQuery = string.Empty;
        _selectedRelativePath = null;
        _selectedIndex = -1;
        _candidateCount = 0;
        _isRefreshing = true;
        _dialog.SetQueryText(string.Empty);
        _dialog.SetResults(_emptyItems, -1);
        RefreshDialogLabels();
        _dialog.Show(app);

        _session = await _searchService.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = projectRoot,
                Query = string.Empty,
                MaximumResults = DialogMaximumResults,
                RecentItemLimit = 8,
                RefreshBatchSize = 256,
            },
            cancellationToken);
        _session.Updated += OnSessionUpdated;
        ApplyState(_session.Current);
    }

    public async ValueTask DisposeAsync()
        => await CloseAsync();

    private async Task OnDialogQueryChangedAsync(string queryText)
    {
        _activeQuery = queryText;
        RefreshDialogLabels();

        if (_session is not null)
        {
            await _session.SetQueryAsync(queryText);
        }
    }

    private void OnSessionUpdated(object? sender, ProjectFileSearchStateChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        var dispatcher = _getFocusTarget()?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyStateIfCurrent(e.State);
            return;
        }

        dispatcher.Post(() => ApplyStateIfCurrent(e.State));
    }

    private void ApplyStateIfCurrent(ProjectFileSearchState state)
    {
        if (!_dialog.IsOpen ||
            !string.Equals(state.Query, _activeQuery, StringComparison.Ordinal) ||
            !string.Equals(_projectRoot, state.Results.FirstOrDefault()?.Item.ProjectRoot ?? _projectRoot, StringComparison.OrdinalIgnoreCase) && state.Results.Count > 0)
        {
            return;
        }

        ApplyState(state);
    }

    private void ApplyState(ProjectFileSearchState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var mappedItems = state.Results
            .Where(static result => result.Item.Kind == ProjectFileSearchItemKind.File)
            .Select(result => new ProjectFileReferencePopupItem(result, _appearanceRegistry.GetAppearance(result.Item)))
            .ToArray();

        var selectedKey = _selectedRelativePath;
        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var existingIndex = Array.FindIndex(
                mappedItems,
                item => string.Equals(item.Result.Item.RelativePath, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                selectedIndex = existingIndex;
            }
        }

        if (mappedItems.Length == 0)
        {
            selectedIndex = -1;
            selectedKey = null;
        }
        else if (selectedKey is null)
        {
            selectedKey = mappedItems[selectedIndex].Result.Item.RelativePath;
        }

        _items = mappedItems;
        _selectedIndex = selectedIndex;
        _selectedRelativePath = selectedKey;
        _candidateCount = state.CandidateCount;
        _isRefreshing = state.IsRefreshing;

        _dialog.SetResults(mappedItems, selectedIndex);
        RefreshDialogLabels();
    }

    private void OnSelectionChanged(int selectedIndex)
    {
        _selectedIndex = selectedIndex;
        _selectedRelativePath = selectedIndex >= 0 && selectedIndex < _items.Count
            ? _items[selectedIndex].Result.Item.RelativePath
            : null;
        RefreshDialogLabels();
    }

    private bool AcceptSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _items.Count)
        {
            return false;
        }

        var selected = _items[_selectedIndex];
        _ = AcceptSelectedAsync(selected);
        return true;
    }

    private async Task AcceptSelectedAsync(ProjectFileReferencePopupItem selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var dispatcher = _getFocusTarget()?.Dispatcher;
        await CloseAsync(restoreFocus: false);
        if (dispatcher is not null)
        {
            dispatcher.Post(() => _openFile(selected.Result.Item, selected.Appearance));
        }
        else
        {
            _openFile(selected.Result.Item, selected.Appearance);
        }

        _ = _searchService.RecordUsageAsync(
            new ProjectFileUsageEvent(
                selected.Result.Item.ProjectRoot,
                selected.Result.Item.RelativePath,
                selected.Result.Item.Kind,
                DateTimeOffset.UtcNow,
                ProjectFileUsageAccessKind.PopupAccepted));
    }

    private async Task CloseAsync(bool restoreFocus = true)
    {
        if (_dialog.IsOpen)
        {
            var app = _dialogIsApp();
            _dialog.Close();
            if (restoreFocus && _getFocusTarget() is { } focusTarget)
            {
                app?.Focus(focusTarget);
            }
        }

        if (_session is not null)
        {
            _session.Updated -= OnSessionUpdated;
            await _session.DisposeAsync();
            _session = null;
        }

        _items = [];
        _activeQuery = string.Empty;
        _selectedRelativePath = null;
        _selectedIndex = -1;
        _candidateCount = 0;
        _isRefreshing = false;
        _projectRoot = null;
        _dialog.SetQueryText(string.Empty);
        _dialog.SetResults(_emptyItems, -1);
        RefreshDialogLabels();
    }

    private TerminalApp? _dialogIsApp()
        => _getFocusTarget()?.App;

    private void RefreshDialogLabels()
        => _dialog.SetChrome(BuildHeaderText(), BuildStatisticsText(), BuildStatusText());

    private string BuildHeaderText()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            return SR.T("Open file");
        }

        var projectName = Path.GetFileName(_projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (projectName.Length > DialogProjectNameMaxLength)
        {
            projectName = "..." + projectName[^Math.Max(0, DialogProjectNameMaxLength - 3)..];
        }

        return SR.T("Open file · {0}", projectName);
    }

    private string BuildStatisticsText()
    {
        var indexedCount = Math.Max(_candidateCount, _items.Count);
        var visibleCount = Math.Max(0, _items.Count);

        if (indexedCount == 0)
        {
            return _isRefreshing ? SR.T("Indexing project...") : SR.T("0 indexed");
        }

        if (string.IsNullOrWhiteSpace(_activeQuery))
        {
            return SR.T("{0} indexed", indexedCount);
        }

        if (visibleCount == 0)
        {
            return SR.T("0 matches · {0} indexed", indexedCount);
        }

        return indexedCount > visibleCount
            ? SR.T("Top {0} matches · {1} indexed", visibleCount, indexedCount)
            : SR.T("{0} matches · {1} indexed", visibleCount, indexedCount);
    }

    private string BuildStatusText()
    {
        var indexedCount = Math.Max(_candidateCount, _items.Count);
        if (_isRefreshing)
        {
            return indexedCount <= 0
                ? SR.T("Loading project files...")
                : SR.T("Refreshing results...");
        }

        if (indexedCount == 0)
        {
            return SR.T("No project files available");
        }

        return _items.Count == 0
            ? SR.T("No files match the current search")
            : SR.T("Enter opens the selected file");
    }
}
