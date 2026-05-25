using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.GitHub;

internal sealed class GitHubIssuePromptAttachment : IAsyncDisposable
{
    private const int MaximumResults = 50;
    private readonly GitHubPlugin _plugin;
    private readonly IPluginPromptEditorHost _host;
    private readonly GitHubIssuePickerDialog _dialog;
    private readonly object _stateGate = new();
    private IReadOnlyList<GitHubIssueReferenceItem> _allItems = [];
    private IReadOnlyList<GitHubIssueReferenceItem> _items = [];
    private GitHubIssueReferenceSpan? _activeReference;
    private string _activeQuery = string.Empty;
    private int _selectedIndex = -1;
    private long _updateGeneration;
    private CancellationTokenSource? _queryCancellation;

    public GitHubIssuePromptAttachment(GitHubPlugin plugin, IPluginPromptEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(host);
        _plugin = plugin;
        _host = host;
        _dialog = new GitHubIssuePickerDialog(OpenUrl);
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
            if (!GitHubIssueReferenceParser.TryGetActiveIssueReference(text, caretIndex, out var activeReference) ||
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
            var issues = await _plugin.QueryIssueReferencesAsync(GetPromptProjectPath(), queryText, MaximumResults, _queryCancellation.Token).ConfigureAwait(false);
            TryDispatchToHost(() =>
            {
                if (generation != _updateGeneration)
                {
                    return;
                }

                if (issues is null)
                {
                    ApplyQueryUnavailable(queryText, "GitHub issue lookup is unavailable for this prompt folder");
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

                ApplyQueryUnavailable(queryText, "GitHub issue lookup failed");
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

    private void ApplyResult(IReadOnlyList<GitHubIssueReferenceItem> issues)
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
        GitHubIssueReferenceItem? selectedItem = null;
        IReadOnlyList<GitHubIssueReferenceItem> allItems;
        lock (_stateGate)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
            {
                selectedItem = _items[_selectedIndex];
            }

            allItems = _allItems;
        }

        var visibleItems = _dialog.IncludeClosed
            ? allItems as GitHubIssueReferenceItem[] ?? allItems.ToArray()
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
        _dialog.SetChrome("Loading…", "Loading GitHub issues…");
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
        IReadOnlyList<GitHubIssueReferenceItem> items;
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

    private static string BuildStatisticsText(int visibleCount, int totalCount, bool includeClosed)
        => includeClosed
            ? totalCount == 0
                ? "0 matches"
                : FormattableString.Invariant($"{totalCount} matches")
            : FormattableString.Invariant($"{visibleCount} open / {totalCount} matches");

    private static string BuildStatusText(int visibleCount, bool includeClosed, string queryText)
        => visibleCount == 0
            ? includeClosed
                ? "No GitHub issues match the current search"
                : "No open GitHub issues match the current search"
            : string.IsNullOrWhiteSpace(queryText) ? "Recent GitHub issues · Enter inserts the selected issue link"
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
