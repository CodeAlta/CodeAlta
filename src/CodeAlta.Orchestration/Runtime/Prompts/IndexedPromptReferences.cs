using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.Prompts;

/// <summary>
/// Project file search for the prompt reference picker, backed by the shared indexed search
/// (fuzzy ranking, ignore files, recently used items). One search session is kept per project
/// root so that typing only re-ranks the indexed candidates instead of walking the folder again.
/// </summary>
internal sealed class IndexedPromptReferences : IAsyncDisposable
{
    /// <summary>The picker shows at most this many rows, like the terminal picker.</summary>
    internal const int MaximumResults = 64;
    private const int MaximumRoots = 4;
    private static readonly TimeSpan RankingWait = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);

    private readonly IProjectFileSearchService _service;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private bool _disposed;

    internal IndexedPromptReferences(IProjectFileSearchService service, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Ranks the indexed files and folders of one project root against a query.</summary>
    /// <param name="projectRoot">A verified project root.</param><param name="query">The text typed after the at sign.</param>
    /// <param name="cancellationToken">Cancels the caller's wait; the shared index keeps building.</param>
    /// <returns>Up to 64 ranked matches. Status is "indexing" while the folder is still being read, otherwise "ok".</returns>
    internal async Task<OwnedReferenceSearchResult> SearchAsync(string projectRoot, string query, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (entry is null) return new("closed", [], false);
        // One query at a time per root: the session ranks its single current query.
        await entry.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = entry.Session;
            if (_clock.GetUtcNow() - entry.Refreshed > RefreshAfter && !session.Current.IsRefreshing)
            {
                entry.Refreshed = _clock.GetUtcNow();
                await session.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var ranked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Worth a short wait: the query is ranked and the first traversal has either finished or already matched.
            bool Ready()
            {
                var current = session.Current;
                return string.Equals(current.Query, query, StringComparison.Ordinal) && (!current.IsRefreshing || current.Results.Any());
            }
            void Updated(object? sender, ProjectFileSearchStateChangedEventArgs args)
            {
                if (Ready()) ranked.TrySetResult();
            }
            session.Updated += Updated;
            try
            {
                await session.SetQueryAsync(query, cancellationToken).ConfigureAwait(false);
                if (!Ready()) await Task.WhenAny(ranked.Task, Task.Delay(RankingWait, cancellationToken)).ConfigureAwait(false);
            }
            finally { session.Updated -= Updated; }
            cancellationToken.ThrowIfCancellationRequested();
            var state = session.Current;
            // A ranking that did not settle in time still belongs to an older query: report it as in progress.
            var settled = string.Equals(state.Query, query, StringComparison.Ordinal);
            var items = settled
                ? state.Results.Take(MaximumResults).Select(result => new OwnedReferenceMatch(
                    result.Item.RelativePath.Replace('\\', '/'), result.Item.Kind == ProjectFileSearchItemKind.Directory, result.IsRecent)).ToArray()
                : [];
            return new(state.IsRefreshing || !settled ? "indexing" : "ok", items, false, state.CandidateCount);
        }
        finally { entry.Turn.Release(); }
    }

    private async Task<Entry?> GetEntryAsync(string projectRoot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var existing = _entries.FirstOrDefault(entry => string.Equals(entry.Root, projectRoot, StringComparison.Ordinal));
            if (existing is not null)
            {
                _entries.Remove(existing);
                _entries.Add(existing);
                return existing;
            }
        }
        // Creating a session starts the first folder traversal in the background.
        var session = await _service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions { ProjectRoot = projectRoot, MaximumResults = MaximumResults, RecentItemLimit = 5 },
            cancellationToken).ConfigureAwait(false);
        var created = new Entry(projectRoot, session) { Refreshed = _clock.GetUtcNow() };
        Entry? evicted = null;
        Entry result;
        lock (_gate)
        {
            if (_disposed) { evicted = created; result = null!; }
            else if (_entries.FirstOrDefault(entry => string.Equals(entry.Root, projectRoot, StringComparison.Ordinal)) is { } raced)
            {
                evicted = created;
                result = raced;
            }
            else
            {
                _entries.Add(created);
                if (_entries.Count > MaximumRoots) { evicted = _entries[0]; _entries.RemoveAt(0); }
                result = created;
            }
        }
        if (evicted is not null) await evicted.Session.DisposeAsync().ConfigureAwait(false);
        return _disposed ? null : result;
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _entries];
            _entries.Clear();
        }
        foreach (var entry in entries) await entry.Session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Entry(string root, IProjectFileSearchSession session)
    {
        public string Root { get; } = root;
        public IProjectFileSearchSession Session { get; } = session;
        public SemaphoreSlim Turn { get; } = new(1, 1);
        public DateTimeOffset Refreshed { get; set; }
    }
}
