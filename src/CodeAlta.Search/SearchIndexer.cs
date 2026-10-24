using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Search;

/// <summary>Where the indexer is.</summary>
public sealed record SearchIndexerStatus
{
    /// <summary>Gets whether it works now.</summary>
    public bool Busy { get; init; }

    /// <summary>Gets whether its first pass is done.</summary>
    public bool Ready { get; init; }

    /// <summary>Gets the journals of the pass in progress already read.</summary>
    public int JournalsDone { get; init; }

    /// <summary>Gets the journals of the pass in progress.</summary>
    public int JournalsTotal { get; init; }

    /// <summary>Gets when the last pass ended.</summary>
    public DateTimeOffset? LastPass { get; init; }

    /// <summary>Gets the last error, or null.</summary>
    public string? LastError { get; init; }
}

/// <summary>Options of the indexer, for tests.</summary>
public sealed record SearchIndexerOptions
{
    /// <summary>Gets how long after it starts the first pass begins: the application starts first.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets how long after it starts the indexer reads no source for its signals (a plugin loaded, the commands of the
    /// window, a turn): they wait, so that the window is shown first. A query still reads at once the sources it needs.
    /// </summary>
    public TimeSpan QuietStart { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the time between two passes over every source.</summary>
    public TimeSpan PassInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long a signal about a file waits for quiet, so that a burst of saves makes one update.</summary>
    public TimeSpan FileQuiet { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the bytes of a journal read in one transaction.</summary>
    public long JournalChunkBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Gets how old a check of a catalog source may be before a query of its kinds checks it again.</summary>
    public TimeSpan FreshnessBeforeQuery { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The one writer of the search index. It reads the sources whose key a signal names, and passes over every
/// source at start and every few minutes: the catalog sources first (they are small and tool search needs them),
/// then the journals, the newest first. Signals say what changed; the journals and the files stay the authority.
/// </summary>
public sealed class SearchIndexer : IAsyncDisposable
{
    private const string PassKey = "pass";

    private readonly SearchIndexStore _store;
    private readonly ISessionJournalCatalog _journals;
    private readonly ISearchHost _host;
    private readonly Func<IReadOnlyList<ISearchSource>> _sources;
    private readonly TimeProvider _time;
    private readonly SearchIndexerOptions _options;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _high = new();
    private readonly LinkedList<string> _low = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _checked = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SessionJournalReader _reader = new();
    private readonly SemaphoreSlim _writeTurn = new(1, 1);
    private Task _loop = Task.CompletedTask;
    private ITimer? _timer;
    private SearchIndexerStatus _status = new();
    private long _passStarted;

    /// <summary>Creates the indexer.</summary>
    /// <param name="store">The index.</param>
    /// <param name="journals">The journals of the sessions.</param>
    /// <param name="host">The settings, projects and sessions of the application.</param>
    /// <param name="sources">Gives the catalog sources now (they change with the projects and the plugins).</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <param name="log">Gets what is worth a line in the log, or null.</param>
    public SearchIndexer(SearchIndexStore store, ISessionJournalCatalog journals, ISearchHost host, Func<IReadOnlyList<ISearchSource>> sources,
        TimeProvider time, SearchIndexerOptions? options = null, Action<string>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new SearchIndexerOptions();
        _log = log;
    }

    /// <summary>Raised after a write changed texts of the index: vectors may be missing now.</summary>
    public event Action? Changed;

    /// <summary>Raised when <see cref="Status"/> changed: work started or ended, journals read, a pass done.</summary>
    public event Action? StatusChanged;

    /// <summary>Gets where the indexer is.</summary>
    public SearchIndexerStatus Status { get { lock (_gate) return _status; } }

    /// <summary>
    /// Starts the indexer: it handles its signals after <see cref="SearchIndexerOptions.QuietStart"/>, and its first pass
    /// begins after <see cref="SearchIndexerOptions.StartDelay"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (!_loop.IsCompleted) return;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
            _timer = _time.CreateTimer(_ => Signal(PassKey), null, _options.StartDelay, _options.PassInterval);
        }
    }

    /// <summary>Asks for a source to be read again: a key of <see cref="ISearchSource"/>, or <c>journal:&lt;session&gt;</c>.</summary>
    /// <param name="key">The key.</param>
    public void Signal(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!_queued.Add(key)) return;
            (key.StartsWith("journal:", StringComparison.Ordinal) ? _low : _high).AddLast(key);
        }

        _wake.Release();
    }

    /// <summary>Asks for the journal of a session to be read again: a turn started or ended.</summary>
    /// <param name="sessionId">The session.</param>
    public void SignalJournal(string sessionId) => Signal("journal:" + sessionId);

    /// <summary>Asks for a file source to be read again once its files are quiet.</summary>
    /// <param name="key">The key.</param>
    public void SignalFile(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _ = Task.Delay(_options.FileQuiet, _time, _lifetime.Token).ContinueWith(_ => Signal(key), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Asks for a pass over every source now.</summary>
    public void SignalPass() => Signal(PassKey);

    /// <summary>Removes a deleted session from the index at once.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task ForgetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        await _writeTurn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteSourcesAsync(["journal:" + sessionId], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeTurn.Release();
        }
    }

    /// <summary>
    /// Checks the catalog sources of some kinds whose last check is older than <see cref="SearchIndexerOptions.FreshnessBeforeQuery"/>,
    /// for a query of those kinds: "I have just edited it" is found. Bounded by a budget; what is not done stays queued.
    /// </summary>
    /// <param name="kinds">The kinds of the query, or null for all.</param>
    /// <param name="budget">The most time to spend.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task EnsureFreshAsync(IReadOnlySet<SearchKind>? kinds, TimeSpan budget, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        // The sources of a project (its plans and tasks) follow the projects: they are read first.
        await _host.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in _sources())
        {
            if (kinds is not null && !source.Kinds.Any(kinds.Contains)) continue;
            bool stale;
            lock (_gate) stale = !_checked.TryGetValue(source.Key, out var at) || now - at > _options.FreshnessBeforeQuery;
            if (!stale) continue;
            if (Stopwatch.GetElapsedTime(started) > budget)
            {
                Signal(source.Key);
                continue;
            }

            if (!await _writeTurn.WaitAsync(budget, cancellationToken).ConfigureAwait(false))
            {
                Signal(source.Key);
                continue;
            }

            try
            {
                await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeTurn.Release();
            }
        }
    }

    /// <summary>Waits until every queued signal was handled, for tests.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    internal async Task WaitIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            bool idle;
            lock (_gate) idle = _queued.Count == 0 && !_status.Busy;
            if (idle) return;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // The window is shown first: what is signaled meanwhile is kept in the queues, once per source.
        try
        {
            if (_options.QuietStart > TimeSpan.Zero) await Task.Delay(_options.QuietStart, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string? key;
            lock (_gate)
            {
                var queue = _high.Count > 0 ? _high : _low;
                if (queue.Count == 0) continue;
                key = queue.First!.Value;
                queue.RemoveFirst();
                // Unmarked as it is taken: a signal that comes while it is read queues it again, since what it
                // signals may have been written after the reading began.
                _queued.Remove(key);
                _status = _status with { Busy = true };
            }

            try
            {
                await _writeTurn.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(key, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeTurn.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException or InvalidDataException)
            {
                _log?.Invoke($"Search could not read '{key}': {exception.Message}");
                lock (_gate) _status = _status with { LastError = exception.Message };
            }
            finally
            {
                lock (_gate) _status = _status with { Busy = _queued.Count > 0 };

                StatusChanged?.Invoke();
            }
        }
    }

    private async Task HandleAsync(string key, CancellationToken cancellationToken)
    {
        if (key == PassKey)
        {
            await PassAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (key.StartsWith("journal:", StringComparison.Ordinal))
        {
            var sessions = await _host.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
            var projects = await _host.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
            await ReadJournalAsync(key["journal:".Length..], sessions, Excluded(projects), projects, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_sources().FirstOrDefault(source => source.Key == key) is { } match) await ReadSourceAsync(match, cancellationToken).ConfigureAwait(false);
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        var settings = _host.GetSettings();
        if (!settings.Enabled) return;
        _passStarted = Stopwatch.GetTimestamp();
        var projects = await _host.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        var excluded = Excluded(projects);
        // Exclusions first: what is kept out is removed before anything is read.
        foreach (var project in excluded.Projects) await _store.DeleteContentAsync(project, null, cancellationToken).ConfigureAwait(false);
        foreach (var space in settings.ExcludedSpaces) await _store.DeleteContentAsync(null, space, cancellationToken).ConfigureAwait(false);

        var sources = _sources();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                _log?.Invoke($"Search could not read '{source.Key}': {exception.Message}");
            }
        }

        // Sources that are gone (a project removed, a plugin unloaded) take their items with them.
        var known = new HashSet<string>(sources.Select(static source => source.Key), StringComparer.Ordinal);
        var stored = await _store.ListSourcesAsync("", cancellationToken).ConfigureAwait(false);
        var gone = stored.Where(source => !source.Key.StartsWith("journal:", StringComparison.Ordinal) && !known.Contains(source.Key)).Select(static source => source.Key).ToList();
        await _store.DeleteSourcesAsync(gone, cancellationToken).ConfigureAwait(false);

        var sessions = await _host.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
        var journals = new List<SessionJournalFile>();
        await foreach (var journal in _journals.ListAsync(cancellationToken).ConfigureAwait(false)) journals.Add(journal);
        var cursors = stored.Where(static source => source.Key.StartsWith("journal:", StringComparison.Ordinal))
            .ToDictionary(static source => source.Key["journal:".Length..], StringComparer.Ordinal);
        var titles = await _store.GetSessionTitlesAsync(cancellationToken).ConfigureAwait(false);

        // Journals that are gone, and every journal when the sessions are not indexed.
        var present = new HashSet<string>(journals.Select(static journal => journal.SessionId), StringComparer.Ordinal);
        var removed = cursors.Keys.Where(id => !settings.Sessions || !present.Contains(id)).Select(static id => "journal:" + id).ToList();
        await _store.DeleteSourcesAsync(removed, cancellationToken).ConfigureAwait(false);
        if (!settings.Sessions)
        {
            Finish(0, 0);
            return;
        }

        var stale = journals.Where(journal => !cursors.TryGetValue(journal.SessionId, out var cursor) || cursor.Stamp != StampOf(journal)).ToList();
        var done = 0;
        lock (_gate) _status = _status with { JournalsDone = 0, JournalsTotal = stale.Count };
        foreach (var journal in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReadJournalAsync(journal.SessionId, sessions, excluded, projects, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _log?.Invoke($"Search could not read the journal of '{journal.SessionId}': {exception.Message}");
            }

            done++;
            lock (_gate) _status = _status with { JournalsDone = done };
            StatusChanged?.Invoke();
            // A higher priority signal (a catalog source, a query check) goes before the rest of the history.
            if (HasHighWork()) await DrainHighAsync(cancellationToken).ConfigureAwait(false);
        }

        // A session renamed since its items were written gets its new title.
        foreach (var (id, info) in sessions)
        {
            if (titles.TryGetValue(id, out var title) && title != info.Title && !stale.Any(journal => journal.SessionId == id))
            {
                await _store.RetitleSessionAsync(id, info.Title, cancellationToken).ConfigureAwait(false);
            }
        }

        Finish(done, stale.Count);
    }

    private void Finish(int done, int total)
    {
        bool first;
        lock (_gate)
        {
            first = !_status.Ready;
            _status = _status with { Ready = true, LastPass = _time.GetUtcNow(), JournalsDone = done, JournalsTotal = total };
        }

        // What a pass costs is worth a line once, and when it is slow.
        var elapsed = Stopwatch.GetElapsedTime(_passStarted);
        if (first || elapsed > TimeSpan.FromSeconds(1)) _log?.Invoke($"Search pass: {done} of {total} journals read in {elapsed.TotalMilliseconds:F0} ms{(first ? " (first pass)" : "")}.");
        StatusChanged?.Invoke();
    }

    private bool HasHighWork()
    {
        lock (_gate) return _high.Count > 0;
    }

    private async Task DrainHighAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string key;
            lock (_gate)
            {
                if (_high.Count == 0) return;
                key = _high.First!.Value;
                if (key == PassKey) return;
                _high.RemoveFirst();
                _queued.Remove(key);
            }

            await HandleAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string StampOf(SessionJournalFile journal) => $"{journal.Length}|{journal.LastWriteUtc.UtcTicks}";

    private (HashSet<string> Projects, IReadOnlyList<string> Spaces) Excluded(IReadOnlyList<SearchProjectInfo> projects)
    {
        var settings = _host.GetSettings();
        var excluded = new HashSet<string>(settings.ExcludedProjects, StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (_host.IsProjectExcludedByItself(project)) excluded.Add(project.ProjectId);
        }

        return (excluded, settings.ExcludedSpaces);
    }

    private async Task ReadSourceAsync(ISearchSource source, CancellationToken cancellationToken)
    {
        var projects = await _host.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        var excluded = Excluded(projects).Projects;
        // The stamp kept is the source's and the projects kept out when it was read: a project let in again reads it again.
        var exclusions = "|x:" + string.Join(",", excluded.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
        var previous = await _store.GetSourceAsync(source.Key, cancellationToken).ConfigureAwait(false);
        var previousStamp = previous?.Stamp is { } kept && kept.EndsWith(exclusions, StringComparison.Ordinal) ? kept[..^exclusions.Length] : null;
        var read = await source.ReadAsync(previousStamp, cancellationToken).ConfigureAwait(false);
        lock (_gate) _checked[source.Key] = _time.GetUtcNow();
        if (read is null) return;
        var documents = read.Documents.Where(document => document.ProjectId is null || !excluded.Contains(document.ProjectId)).ToList();
        var state = new SearchSourceState(source.Key, read.Stamp + exclusions, null, null, 0, null, _time.GetUtcNow());
        var written = await _store.ApplyAsync(new SearchSourceUpdate(state, documents) { Complete = true }, cancellationToken).ConfigureAwait(false);
        if (written > 0) Changed?.Invoke();
    }

    private async Task ReadJournalAsync(string sessionId, IReadOnlyDictionary<string, SearchSessionInfo> sessions, (HashSet<string> Projects, IReadOnlyList<string> Spaces) excluded,
        IReadOnlyList<SearchProjectInfo> projects, CancellationToken cancellationToken)
    {
        var settings = _host.GetSettings();
        var key = "journal:" + sessionId;
        var file = await _journals.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (file is null || !settings.Sessions)
        {
            await _store.DeleteSourcesAsync([key], cancellationToken).ConfigureAwait(false);
            return;
        }

        sessions.TryGetValue(sessionId, out var info);
        if (info is not null && (info.ProjectId is not null && excluded.Projects.Contains(info.ProjectId) || excluded.Spaces.Contains(info.SpaceId ?? "default", StringComparer.OrdinalIgnoreCase)))
        {
            await _store.DeleteSourcesAsync([key], cancellationToken).ConfigureAwait(false);
            return;
        }

        var projectName = info?.ProjectId is { } projectId ? projects.FirstOrDefault(project => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))?.Name : null;
        var context = new JournalContext(info?.Title ?? "", projectName);
        var previous = await _store.GetSourceAsync(key, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var stream = await _journals.OpenAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                await _store.DeleteSourcesAsync([key], cancellationToken).ConfigureAwait(false);
                return;
            }

            JournalReadResult result;
            bool complete;
            await using (stream.ConfigureAwait(false))
            {
                // A journal read from its start is read whole, so that what it no longer holds can be removed.
                var fromStart = previous is null || previous.Offset > stream.Length;
                result = _reader.Read(sessionId, stream, previous, context, _time.GetUtcNow(), fromStart ? long.MaxValue : _options.JournalChunkBytes, cancellationToken);
                complete = fromStart || result.Restarted;
                if (result.Restarted && !result.ReachedEnd) result = _reader.Read(sessionId, stream, null, context, _time.GetUtcNow(), long.MaxValue, cancellationToken);
            }

            // A session whose header names an excluded project or space is kept out, whatever the list of sessions says.
            if (result.State.ProjectId is { } headerProject && excluded.Projects.Contains(headerProject)
                || excluded.Spaces.Contains(result.State.SpaceId ?? "default", StringComparer.OrdinalIgnoreCase))
            {
                await _store.DeleteSourcesAsync([key], cancellationToken).ConfigureAwait(false);
                return;
            }

            var done = result.ReachedEnd;
            var state = new SearchSourceState(key, done ? StampOf(file) : null, result.Fingerprint, null, result.Offset,
                JsonSerializer.Serialize(result.State, SearchJsonContext.Default.JournalReadState), _time.GetUtcNow());
            var update = new SearchSourceUpdate(state, result.Documents) { Complete = complete && result.ReachedEnd, Removed = result.Removed };
            var written = await _store.ApplyAsync(update, cancellationToken).ConfigureAwait(false);
            if (written > 0) Changed?.Invoke();
            if (done || cancellationToken.IsCancellationRequested) return;
            previous = state;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }
}
