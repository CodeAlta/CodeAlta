using CodeAlta.Presentation.Styling;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Prompting;

internal sealed class ProjectFileReferencePopupController : IAsyncDisposable
{
    private const int DialogMaximumResults = 64;
    private const int DialogProjectNameMaxLength = 28;
    private readonly IProjectFileReferencePopupHost _host;
    private readonly IProjectFileSearchService _searchService;
    private readonly IProjectFileAppearanceRegistry _appearanceRegistry;
    private readonly Func<string?> _getProjectRoot;
    private readonly ProjectFilePickerDialog _dialog;
    private readonly object _stateGate = new();
    private readonly IReadOnlyList<ProjectFileReferencePopupItem> _emptyItems = [];
    private IReadOnlyList<ProjectFileReferencePopupItem> _items = [];
    private ProjectFileSearchState? _pendingSessionState;
    private ProjectFilePromptActiveReference? _activeReference;
    private IProjectFileSearchSession? _session;
    private string? _projectRoot;
    private string _activeQuery = string.Empty;
    private string? _selectedRelativePath;
    private int _selectedIndex = -1;
    private int _candidateCount;
    private bool _isRefreshing;
    private int _sessionStateDispatchQueued;
    private long _updateGeneration;

    public ProjectFileReferencePopupController(
        IProjectFileReferencePopupHost host,
        IProjectFileSearchService searchService,
        IProjectFileAppearanceRegistry appearanceRegistry,
        Func<string?> getProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(appearanceRegistry);
        ArgumentNullException.ThrowIfNull(getProjectRoot);

        _host = host;
        _searchService = searchService;
        _appearanceRegistry = appearanceRegistry;
        _getProjectRoot = getProjectRoot;
        _dialog = new ProjectFilePickerDialog();
        _dialog.QueryChanged += (_, queryText) => _ = OnDialogQueryChangedAsync(queryText);
        _dialog.SelectionChanged += (_, selectedIndex) => OnSelectionChanged(selectedIndex);
        _dialog.AcceptRequested += (_, _) => AcceptSelected();
        _dialog.DismissRequested += (_, _) => _ = CloseAsync();

        RefreshDialogLabels();
    }

    public bool IsOpen => _dialog.IsOpen;

    internal IReadOnlyList<ProjectFileReferencePopupItem> Items
    {
        get
        {
            lock (_stateGate)
            {
                return _items;
            }
        }
    }

    internal int SelectedIndex
    {
        get
        {
            lock (_stateGate)
            {
                return _selectedIndex;
            }
        }
    }

    internal string QueryText => _dialog.QueryText;

    public void HandleEditorStateChanged()
        => _ = UpdateForEditorStateAsync();

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await DisposeSessionAsync(_session);
        _session = null;
    }

    private async Task UpdateForEditorStateAsync()
    {
        try
        {
            var generation = Interlocked.Increment(ref _updateGeneration);
            var text = _host.Text ?? string.Empty;
            var caretIndex = _host.CaretIndex;
            var projectRoot = _getProjectRoot();
            if (!ProjectFilePromptReferenceParser.TryGetActiveReference(text, caretIndex, out var activeReference) ||
                string.IsNullOrWhiteSpace(projectRoot))
            {
                await CloseAsync();
                return;
            }

            var previousProjectRoot = _projectRoot;
            var previousStartIndex = _activeReference?.StartIndex;
            var needsNewSession = _session is null ||
                !_dialog.IsOpen ||
                !string.Equals(previousProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase) ||
                previousStartIndex != activeReference.StartIndex;

            _activeReference = activeReference;
            _projectRoot = projectRoot;

            if (needsNewSession)
            {
                await OpenSessionAsync(projectRoot, activeReference.QueryText, generation);
                return;
            }

            if (string.Equals(_activeQuery, activeReference.QueryText, StringComparison.Ordinal))
            {
                EnsureDialogVisible(activeReference.QueryText);
                return;
            }

            _dialog.SetQueryText(activeReference.QueryText);
            await UpdateSessionQueryAsync(activeReference.QueryText);
            EnsureDialogVisible(activeReference.QueryText);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            await CloseAsync();
        }
    }

    private async Task OpenSessionAsync(string projectRoot, string queryText, long generation)
    {
        var previous = _session;
        _session = null;
        await DisposeSessionAsync(previous);

        ShowLoadingState(queryText);
        EnsureDialogVisible(queryText);

        var sessionCreateTask = Task.Run(
            () => _searchService.CreateSessionAsync(
                    new ProjectFileSearchSessionOptions
                    {
                        ProjectRoot = projectRoot,
                        Query = queryText,
                        MaximumResults = DialogMaximumResults,
                        RecentItemLimit = 5,
                        RefreshBatchSize = 256,
                    })
                .AsTask());
        _ = sessionCreateTask.ContinueWith(
            static (task, state) =>
            {
                var context = ((ProjectFileReferencePopupController Controller, long Generation))state!;
                context.Controller.OnSessionCreated(task, context.Generation);
            },
            (this, generation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnSessionCreated(Task<IProjectFileSearchSession> task, long generation)
    {
        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            _ = task.Exception;
            TryDispatchToHost(() => _ = CloseAsync());
            return;
        }

        var session = task.Result;
        TryDispatchToHost(() => _ = AttachSessionAsync(session, generation));
    }

    private async Task AttachSessionAsync(IProjectFileSearchSession session, long generation)
    {
        if (generation != _updateGeneration || !_dialog.IsOpen)
        {
            await session.DisposeAsync();
            return;
        }

        _session = session;
        session.Updated += OnSessionUpdated;

        var currentQuery = _dialog.QueryText;
        _activeQuery = currentQuery;

        if (string.Equals(session.Current.Query, currentQuery, StringComparison.Ordinal))
        {
            ApplyState(session.Current);
        }
        else
        {
            await session.SetQueryAsync(currentQuery);
        }
    }

    private void OnSessionUpdated(object? sender, ProjectFileSearchStateChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        if (_host.Visual.Dispatcher.CheckAccess())
        {
            ApplySessionStateIfCurrent(e.State);
            return;
        }

        lock (_stateGate)
        {
            _pendingSessionState = e.State;
        }

        SchedulePendingSessionStateDispatch(sender);
    }

    private async Task OnDialogQueryChangedAsync(string queryText)
    {
        try
        {
            if (!_dialog.IsOpen)
            {
                return;
            }

            await UpdateSessionQueryAsync(queryText);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Task UpdateSessionQueryAsync(string queryText)
    {
        _activeQuery = queryText;
        RefreshDialogLabels();

        if (_session is null)
        {
            return Task.CompletedTask;
        }

        return _session.SetQueryAsync(queryText).AsTask();
    }

    private void ApplyState(ProjectFileSearchState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var mappedItems = state.Results
            .Select(result => new ProjectFileReferencePopupItem(result, _appearanceRegistry.GetAppearance(result.Item)))
            .ToArray();
        var selectedKey = _selectedRelativePath;
        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var index = Array.FindIndex(
                mappedItems,
                item => string.Equals(item.Result.Item.RelativePath, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                selectedIndex = index;
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

        lock (_stateGate)
        {
            _items = mappedItems;
            _selectedIndex = selectedIndex;
            _selectedRelativePath = selectedKey;
            _candidateCount = state.CandidateCount;
            _isRefreshing = state.IsRefreshing;
        }

        ReplaceListItems(mappedItems, selectedIndex);
        RefreshDialogLabels();
    }

    private void ReplaceListItems(IReadOnlyList<ProjectFileReferencePopupItem> items, int selectedIndex)
        => _dialog.SetResults(items, selectedIndex);

    private void ShowLoadingState(string queryText)
    {
        _activeQuery = queryText;
        lock (_stateGate)
        {
            _items = [];
            _selectedIndex = -1;
            _selectedRelativePath = null;
            _candidateCount = 0;
            _isRefreshing = true;
            _pendingSessionState = null;
        }

        _dialog.SetQueryText(queryText);
        ReplaceListItems(_emptyItems, -1);
        RefreshDialogLabels();
    }

    private void EnsureDialogVisible(string queryText)
    {
        var app = _host.Visual.App;
        if (app is null)
        {
            return;
        }

        _dialog.SetQueryText(queryText);
        _dialog.Show(app);
    }

    private void OnSelectionChanged(int newIndex)
    {
        lock (_stateGate)
        {
            _selectedIndex = newIndex;
            _selectedRelativePath = newIndex >= 0 && newIndex < _items.Count
                ? _items[newIndex].Result.Item.RelativePath
                : null;
        }

        RefreshDialogLabels();
    }

    private bool AcceptSelected()
    {
        var items = Items;
        var selectedIndex = SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= items.Count || _activeReference is null)
        {
            return false;
        }

        var selected = items[selectedIndex].Result.Item;
        var currentText = _host.Text ?? string.Empty;
        var replacement = ProjectFilePromptReferenceFormatter.BuildMarkdownLink(selected);
        var updatedText = currentText.Substring(0, _activeReference.StartIndex) +
            replacement +
            currentText.Substring(_activeReference.StartIndex + _activeReference.Length);
        _host.Text = updatedText;
        _host.CaretIndex = _activeReference.StartIndex + replacement.Length;
        _ = CloseAsync();
        _ = RecordUsageAsync(selected);
        return true;
    }

    private async Task RecordUsageAsync(ProjectFileSearchItem item)
    {
        try
        {
            await _searchService.RecordUsageAsync(
                new ProjectFileUsageEvent(
                    item.ProjectRoot,
                    item.RelativePath,
                    item.Kind,
                    DateTimeOffset.UtcNow,
                    ProjectFileUsageAccessKind.PopupAccepted));
        }
        catch
        {
        }
    }

    private async Task CloseAsync()
    {
        if (_dialog.IsOpen)
        {
            _dialog.Close();
            _host.FocusPromptEditor();
        }

        ReplaceListItems(_emptyItems, -1);
        _dialog.SetQueryText(string.Empty);
        _activeReference = null;
        _activeQuery = string.Empty;
        _projectRoot = null;
        lock (_stateGate)
        {
            _items = [];
            _selectedIndex = -1;
            _selectedRelativePath = null;
            _candidateCount = 0;
            _isRefreshing = false;
            _pendingSessionState = null;
        }

        Interlocked.Exchange(ref _sessionStateDispatchQueued, 0);
        RefreshDialogLabels();

        var session = _session;
        _session = null;
        await DisposeSessionAsync(session);
    }

    private async ValueTask DisposeSessionAsync(IProjectFileSearchSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.Updated -= OnSessionUpdated;
        await session.DisposeAsync();
    }

    private void ApplyPendingSessionState(object? sender)
    {
        ProjectFileSearchState? pendingState;
        lock (_stateGate)
        {
            pendingState = _pendingSessionState;
            _pendingSessionState = null;
        }

        Interlocked.Exchange(ref _sessionStateDispatchQueued, 0);

        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        if (pendingState is not null)
        {
            ApplySessionStateIfCurrent(pendingState);
        }

        lock (_stateGate)
        {
            if (_pendingSessionState is null)
            {
                return;
            }
        }

        SchedulePendingSessionStateDispatch(sender);
    }

    private void ApplySessionStateIfCurrent(ProjectFileSearchState state)
    {
        if (_session is null ||
            !_dialog.IsOpen ||
            !string.Equals(state.Query, _activeQuery, StringComparison.Ordinal))
        {
            return;
        }

        ApplyState(state);
    }

    private void SchedulePendingSessionStateDispatch(object? sender)
    {
        if (Interlocked.Exchange(ref _sessionStateDispatchQueued, 1) != 0)
        {
            return;
        }

        TryDispatchToHost(() => ApplyPendingSessionState(sender));
    }

    private void TryDispatchToHost(Action action)
    {
        try
        {
            _host.Visual.Dispatcher.Post(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private string BuildHeaderText()
    {
        var projectRoot = _projectRoot;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return SR.T("Project files");
        }

        var projectName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (projectName.Length > DialogProjectNameMaxLength)
        {
            projectName = "..." + projectName[^Math.Max(0, DialogProjectNameMaxLength - 3)..];
        }

        return SR.T("Project files · {0}", projectName);
    }

    private string BuildStatisticsText()
    {
        lock (_stateGate)
        {
            var indexedCount = Math.Max(_candidateCount, _items.Count);
            var visibleCount = Math.Max(0, _items.Count);
            var hasQuery = !string.IsNullOrWhiteSpace(_activeQuery);

            if (indexedCount == 0)
            {
                return _isRefreshing ? SR.T("Indexing project...") : SR.T("0 indexed");
            }

            if (!hasQuery)
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
    }

    private string BuildStatusText()
    {
        lock (_stateGate)
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
                ? SR.T("No files or folders match the current search")
                : SR.T("Enter inserts the selected link");
        }
    }

    private void RefreshDialogLabels()
        => _dialog.SetChrome(BuildHeaderText(), BuildStatisticsText(), BuildStatusText());
}
