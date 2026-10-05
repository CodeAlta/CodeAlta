using CodeAlta.Plugins.Tui;

namespace CodeAlta.Plugin.Git;

internal sealed class GitIssuePromptAttachment : IAsyncDisposable
{
    private const int MaximumResults = 50;
    private readonly GitPlugin _plugin;
    private readonly IPluginTerminalPromptEditorHost _host;
    private readonly GitIssuePickerDialog _dialog;
    private readonly object _stateGate = new();
    private IReadOnlyList<GitIssueReferenceItem> _allItems = [];
    private IReadOnlyList<GitIssueReferenceItem> _items = [];
    private GitIssueReferenceSpan? _activeReference;
    private string _activeQuery = string.Empty;
    private int _selectedIndex = -1;
    private long _updateGeneration;
    private CancellationTokenSource? _queryCancellation;

    public GitIssuePromptAttachment(GitPlugin plugin, IPluginTerminalPromptEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(host);
        _plugin = plugin;
        _host = host;
        _dialog = new GitIssuePickerDialog(OpenUrl);
        _dialog.QueryChanged += OnDialogQueryChanged;
        _dialog.SelectionChanged += OnSelectionChanged;
        _dialog.AcceptRequested += OnAcceptRequested;
        _dialog.DismissRequested += OnDismissRequested;
        _dialog.IncludeClosedChanged += OnIncludeClosedChanged;
        _host.EditorStateChanged += OnEditorStateChanged;
        _host.Accepted += OnHostAccepted;
    }

    public bool IsOpen => _dialog.IsOpen;

    public ValueTask DisposeAsync()
    {
        _host.EditorStateChanged -= OnEditorStateChanged;
        _host.Accepted -= OnHostAccepted;
        _dialog.QueryChanged -= OnDialogQueryChanged;
        _dialog.SelectionChanged -= OnSelectionChanged;
        _dialog.AcceptRequested -= OnAcceptRequested;
        _dialog.DismissRequested -= OnDismissRequested;
        _dialog.IncludeClosedChanged -= OnIncludeClosedChanged;
        CloseOnHost();
        return ValueTask.CompletedTask;
    }

    private void OnEditorStateChanged(object? sender, EventArgs e)
        => _ = UpdateForEditorStateAsync();

    private void OnDialogQueryChanged(object? sender, string queryText)
        => _ = QueryIssuesAsync(queryText, Interlocked.Increment(ref _updateGeneration));

    private void OnSelectionChanged(object? sender, int selectedIndex)
    {
        lock (_stateGate)
        {
            _selectedIndex = selectedIndex;
        }
    }

    private void OnAcceptRequested(object? sender, EventArgs e)
        => AcceptSelected();

    private void OnDismissRequested(object? sender, EventArgs e)
        => CloseOnHost();

    private void OnIncludeClosedChanged(object? sender, bool includeClosed)
        => ApplyVisibleItems();

    private void OnHostAccepted(object? sender, EventArgs e)
        => CloseOnHost();

    private string? GetPromptProjectPath()
        => _host.ProjectPath ?? _plugin.GetSelectedProjectPath();

    private async Task UpdateForEditorStateAsync()
    {
        long generation = 0;
        try
        {
            generation = Interlocked.Increment(ref _updateGeneration);
            var text = _host.Text ?? string.Empty;
            var caretIndex = _host.CaretIndex;
            if (!GitIssueReferenceParser.TryGetActiveIssueReference(text, caretIndex, out var activeReference) ||
                !await _plugin.CanResolveIssueReferencesAsync(GetPromptProjectPath(), CancellationToken.None).ConfigureAwait(false))
            {
                CloseOnHost();
                return;
            }

            var needsQuery = _activeReference?.StartIndex != activeReference.StartIndex ||
                !_dialog.IsOpen ||
                !string.Equals(_activeQuery, activeReference.QueryText, StringComparison.Ordinal);
            _activeReference = activeReference;
            if (!needsQuery)
            {
                TryDispatchToHost(() => EnsureDialogVisible(activeReference.QueryText));
                return;
            }

            await QueryIssuesAsync(activeReference.QueryText, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            if (generation == _updateGeneration)
            {
                CloseOnHost();
            }
        }
    }

    private async Task QueryIssuesAsync(string queryText, long generation)
    {
        try
        {
            _activeQuery = queryText;
            TryDispatchToHost(() =>
            {
                ShowLoadingState(queryText);
                EnsureDialogVisible(queryText);
            });

            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            _queryCancellation = new CancellationTokenSource();
            var repository = await _plugin.ResolveIssueRepositoryAsync(GetPromptProjectPath(), _queryCancellation.Token).ConfigureAwait(false);
            var issues = await _plugin.QueryIssueReferencesAsync(GetPromptProjectPath(), queryText, MaximumResults, _queryCancellation.Token).ConfigureAwait(false);
            TryDispatchToHost(() =>
            {
                if (generation != _updateGeneration)
                {
                    return;
                }

                _dialog.SetTitle(BuildTitle(repository));

                if (issues is null)
                {
                    ApplyQueryUnavailable(queryText, "Issue lookup is unavailable for this prompt folder");
                    EnsureDialogVisible(queryText);
                    return;
                }

                ApplyResult(issues);
                EnsureDialogVisible(queryText);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            TryDispatchToHost(() =>
            {
                if (generation != _updateGeneration)
                {
                    return;
                }

                ApplyQueryUnavailable(queryText, "Issue lookup failed");
                EnsureDialogVisible(queryText);
            });
        }
    }

    private void ApplyQueryUnavailable(string queryText, string statusText)
    {
        lock (_stateGate)
        {
            _allItems = [];
            _items = [];
            _selectedIndex = -1;
        }

        _dialog.SetQueryText(queryText);
        _dialog.SetResults([], -1);
        _dialog.SetChrome("0 matches", statusText);
    }

    private void ApplyResult(IReadOnlyList<GitIssueReferenceItem> issues)
    {
        var mappedItems = issues.OrderByDescending(static issue => issue.UpdatedAt).ToArray();
        lock (_stateGate)
        {
            _allItems = mappedItems;
        }

        ApplyVisibleItems();
    }

    private void ApplyVisibleItems()
    {
        GitIssueReferenceItem? selectedItem = null;
        IReadOnlyList<GitIssueReferenceItem> allItems;
        lock (_stateGate)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
            {
                selectedItem = _items[_selectedIndex];
            }

            allItems = _allItems;
        }

        var visibleItems = _dialog.IncludeClosed
            ? allItems as GitIssueReferenceItem[] ?? allItems.ToArray()
            : allItems.Where(static issue => issue.IsOpen).ToArray();
        var selectedIndex = 0;
        if (visibleItems.Length == 0)
        {
            selectedIndex = -1;
        }
        else if (selectedItem is not null)
        {
            var preservedIndex = Array.FindIndex(visibleItems, item => item.Number == selectedItem.Number);
            selectedIndex = preservedIndex >= 0 ? preservedIndex : 0;
        }

        lock (_stateGate)
        {
            _items = visibleItems;
            _selectedIndex = selectedIndex;
        }

        _dialog.SetResults(visibleItems, selectedIndex);
        _dialog.SetChrome(
            BuildStatisticsText(visibleItems.Length, allItems.Count, _dialog.IncludeClosed),
            BuildStatusText(visibleItems.Length, _dialog.IncludeClosed, _activeQuery));
    }

    private void ShowLoadingState(string queryText)
    {
        lock (_stateGate)
        {
            _allItems = [];
            _items = [];
            _selectedIndex = -1;
        }

        _dialog.SetQueryText(queryText);
        _dialog.SetResults([], -1);
        _dialog.SetChrome("Loading…", "Loading issues…");
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

    private bool AcceptSelected()
    {
        IReadOnlyList<GitIssueReferenceItem> items;
        int selectedIndex;
        lock (_stateGate)
        {
            items = _items;
            selectedIndex = _selectedIndex;
        }

        if (selectedIndex < 0 || selectedIndex >= items.Count || _activeReference is not { } activeReference)
        {
            return false;
        }

        var currentText = _host.Text ?? string.Empty;
        var replacement = items[selectedIndex].Markdown;
        var updatedText = currentText.Substring(0, activeReference.StartIndex) +
            replacement +
            currentText.Substring(activeReference.StartIndex + activeReference.Length);
        _host.Text = updatedText;
        _host.CaretIndex = activeReference.StartIndex + replacement.Length;
        Close();
        return true;
    }

    private void Close()
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
        if (_dialog.IsOpen)
        {
            _dialog.Close();
            _host.FocusPromptEditor();
        }

        _dialog.SetResults([], -1);
        _dialog.SetQueryText(string.Empty);
        _activeReference = null;
        _activeQuery = string.Empty;
        lock (_stateGate)
        {
            _allItems = [];
            _items = [];
            _selectedIndex = -1;
        }

        _dialog.SetChrome(string.Empty, string.Empty);
    }

    private void CloseOnHost()
        => TryDispatchToHost(Close);

    // "GitLab issues · group/project", "Azure DevOps work items · organization/project/repository".
    private static string BuildTitle(GitRepositoryReference? repository)
        => repository is null
            ? "Issues"
            : FormattableString.Invariant($"{repository.Provider.GetDisplayName()} {repository.Provider.GetIssueNoun()} · {repository.FullName}");

    private static string BuildStatisticsText(int visibleCount, int totalCount, bool includeClosed)
        => includeClosed
            ? totalCount == 0
                ? "0 matches"
                : FormattableString.Invariant($"{totalCount} matches")
            : FormattableString.Invariant($"{visibleCount} open / {totalCount} matches");

    private static string BuildStatusText(int visibleCount, bool includeClosed, string queryText)
        => visibleCount == 0
            ? includeClosed
                ? "No issues match the current search"
                : "No open issues match the current search"
            : string.IsNullOrWhiteSpace(queryText) ? "Recent issues · Enter inserts the selected issue link"
            : "Enter inserts the selected issue link";

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void TryDispatchToHost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            if (_host.Visual.Dispatcher.CheckAccess())
            {
                TryRunHostAction(action);
                return;
            }

            _host.Visual.Dispatcher.Post(() => TryRunHostAction(action));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static void TryRunHostAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
