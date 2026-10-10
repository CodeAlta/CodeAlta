using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Statistics.History;

/// <summary>The settings of the engine; the defaults are the ones the application runs with.</summary>
internal sealed class StatisticsEngineOptions
{
    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets how long the engine waits after it is started before it reads anything: the start of the application is not slowed down.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long a session that wrote must be quiet before the flow catches it up; a session is caught up at most once in that time.</summary>
    public TimeSpan FlowDebounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the number of bytes of a journal read in one go; the engine looks at the flow and at a pause between two.</summary>
    public long ChunkBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Gets how long a journal must be unchanged before its open runs are considered interrupted: the session is not running.</summary>
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Gets how often the engine looks again for journals that changed without a signal (sessions of another application).</summary>
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the most skipped sessions the status lists.</summary>
    public int MaxSkippedListed { get; init; } = 50;

    /// <summary>Gets the logger; null for none.</summary>
    public Logger? Logger { get; init; }

    /// <summary>Gets what the queries know of the projects and the spaces; null when the host cannot tell.</summary>
    internal Query.IProjectDirectory? ProjectDirectory { get; init; }

    /// <summary>Gets a function that gives the names of the projects of the application by reference; null for none.</summary>
    public Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>? ResolveProjectNames { get; init; }
}

/// <summary>
/// The engine of the Statistics plugin: it keeps the facts up to date with the sessions. One operation does it all, catching a session
/// up from where its reading stopped, and two things ask for it: the history, which goes through the journals that changed since they were
/// last read (the most recent first, once the user chose how much to read), and the flow, which catches up a session a moment after it wrote.
/// </summary>
/// <remarks>
/// <para>
/// One reader thread at a time, below normal priority, so that a session is never slowed down: the flow goes first, between two
/// sessions and between two chunks of a large journal. A reading can be paused or stopped at a line end and goes on from the saved
/// cursor, also after a restart. The facts, the roll-ups and the cursor of a catch-up are saved in one transaction.
/// </para>
/// <para>All the members are thread-safe; <see cref="RunAsync"/> is the one loop and must be called once.</para>
/// </remarks>
internal sealed class StatisticsEngine : IStatisticsService, IAsyncDisposable
{
    private const string ChoiceKey = "history.choice";
    private const string FloorKey = "history.floor_day";
    private const string PausedKey = "history.paused";
    private const string StoppedKey = "history.stopped";
    private const string DoneKey = "history.done";
    private const string ReasonKey = "history.reason";
    private const string CompleteFromKey = "history.complete_from_day";
    private const int ProjectNameRefreshSeconds = 30;

    private readonly StatisticsStore _store;
    private readonly ISessionJournalCatalog _journals;
    private readonly StatisticsEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly SessionCatchUp _catchUp = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _flow = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly Dictionary<string, string> _projectNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SkippedSession> _skipped = [];

    // Journals that failed to be read, as they were: a journal that failed is tried again when it changes, and not at every look.
    private readonly Dictionary<string, (long Length, long StampTicks)> _failed = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private HistoryPhase _phase = HistoryPhase.Starting;
    private HistoryChoice? _choice;
    private int? _floorDay;
    private int _floorQuarter = int.MinValue;
    private bool _paused;
    private bool _stopped;
    private int? _completeFromDay;
    private string? _reason;
    private string? _error;
    private Queue<TodoItem>? _todo;
    private int _sessionsTotal;
    private int _sessionsDone;
    private long _bytesTotal;
    private long _bytesDone;
    private int? _oldestReached;
    private string? _currentSession;
    private int _skippedCount;
    private long _revision;
    private long _readingTicks;
    private long _readingStartedAt;
    private CancellationTokenSource? _historyCts;
    private CancellationToken _lifetime;
    private DateTimeOffset _lastRescan;
    private DateTimeOffset _lastProjectRefresh;
    private bool _projectNamesStale = true;
    private StatisticsStatus _status = new();
    private int _disposed;

    /// <summary>Initializes the engine.</summary>
    /// <param name="store">The store.</param>
    /// <param name="journals">The catalog of journals of the session store.</param>
    /// <param name="options">The settings; null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="journals"/> is null.</exception>
    public StatisticsEngine(StatisticsStore store, ISessionJournalCatalog journals, StatisticsEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(journals);
        _store = store;
        _journals = journals;
        _options = options ?? new StatisticsEngineOptions();
        _time = _options.Time;
        Queries = new StatisticsQueries(store, _options.ProjectDirectory, _time);
    }

    /// <inheritdoc />
    public StatisticsQueries Queries { get; }

    /// <inheritdoc />
    public event Action<StatisticsStatus>? StatusChanged;

    /// <inheritdoc />
    public event Action<StatisticsDataChange>? DataChanged;

    /// <summary>Gets the store the engine writes.</summary>
    public StatisticsStore Store => _store;

    /// <inheritdoc />
    public StatisticsStatus Status => Volatile.Read(ref _status);

    private enum HistoryPhase
    {
        Starting,
        NeedsChoice,
        Reading,
        Paused,
        Stopped,
        Done,
        Failed,
    }

    private sealed record TodoItem(SessionJournalFile File, bool Restart, long BytesToRead);

    /// <summary>Prepares the tables and reads what the user chose before. Called by <see cref="RunAsync"/>; tests call it themselves.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the preparation.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var meta = await _store.GetAllMetaAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _choice = meta.TryGetValue(ChoiceKey, out var text) && HistoryChoice.TryParse(text, out var choice) ? choice : null;
                _floorDay = meta.TryGetValue(FloorKey, out var floor) && int.TryParse(floor, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ? day : null;
                _floorQuarter = ComputeFloorQuarter(_floorDay);
                _reason = meta.GetValueOrDefault(ReasonKey) is "first-read" or "extended" ? meta[ReasonKey] : null;
                _paused = meta.GetValueOrDefault(PausedKey) == "1";
                _stopped = meta.GetValueOrDefault(StoppedKey) == "1";
                _completeFromDay = meta.TryGetValue(CompleteFromKey, out var complete) && int.TryParse(complete, NumberStyles.None, CultureInfo.InvariantCulture, out var from) ? from : null;
                _phase = _choice is null ? HistoryPhase.NeedsChoice : _stopped ? HistoryPhase.Stopped : _paused ? HistoryPhase.Paused : meta.GetValueOrDefault(DoneKey) == "1" ? HistoryPhase.Done : HistoryPhase.Reading;
                _initialized = true;
            }

            foreach (var (reference, name) in await _store.GetProjectNamesAsync(cancellationToken).ConfigureAwait(false))
            {
                _projectNames[reference] = name;
            }

            // The first step compares the journals with what was read: sessions that changed while the application was closed.
            _lastRescan = DateTimeOffset.MinValue;
            Publish();
        }
        finally
        {
            _initialization.Release();
        }
    }

    /// <summary>The loop of the engine: waits, prepares, then catches sessions up until it is canceled.</summary>
    /// <param name="cancellationToken">A token that ends the loop; the reading stops at a line end, and what was read is kept.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lifetime = cancellationToken;
        try
        {
            await Task.Delay(_options.StartDelay, _time, cancellationToken).ConfigureAwait(false);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _phase = HistoryPhase.Failed;
                _error = exception.Message;
            }

            _options.Logger?.Error($"Statistics could not start: {exception.Message}");
            Publish();
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await StepAsync(cancellationToken).ConfigureAwait(false))
                {
                    await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _options.Logger?.Error($"Statistics reading failed: {exception.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Publish();
    }

    /// <summary>
    /// Does everything that is due now, the flow first and the history after it, and returns when nothing is left to do. Tests drive the
    /// engine with it; the loop does the same one step at a time.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>A task representing the work.</returns>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        _lifetime = cancellationToken;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        while (await StepAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <inheritdoc />
    public void Signal(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // A session is caught up once in the debounce, counted from its first signal: a session that streams is not starved.
        _flow.TryAdd(sessionId, _time.GetUtcNow() + _options.FlowDebounce);
        Wake();
    }

    /// <inheritdoc />
    public async ValueTask<StatisticsStatus> ChooseHistoryAsync(HistoryChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var floor = FloorDayOf(choice);
        bool extend;
        lock (_gate)
        {
            // Reading more history than was chosen goes further back; asking for less than is read does nothing.
            extend = _choice is not null;
            if (extend && !GoesFurtherBack(floor))
            {
                return _status;
            }

            _choice = choice;
            var previousFloor = _floorDay;
            _floorDay = floor;
            _floorQuarter = ComputeFloorQuarter(floor);
            // Going further back keeps the numbers complete from the old floor until the older sessions are read.
            _completeFromDay = extend ? previousFloor : null;
            _paused = false;
            _stopped = false;
            _phase = HistoryPhase.Reading;
            _reason = extend ? "extended" : "first-read";
            _todo = null;
            ResetProgress();
            CancelHistoryReading();
        }

        await PersistHistoryAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        Wake();
        return Status;
    }

    /// <inheritdoc />
    public async ValueTask<StatisticsStatus> PauseAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_phase is not HistoryPhase.Reading)
            {
                return _status;
            }

            _paused = true;
            _phase = HistoryPhase.Paused;
            CancelHistoryReading();
        }

        await PersistHistoryAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        return Status;
    }

    /// <inheritdoc />
    public async ValueTask<StatisticsStatus> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_phase is not HistoryPhase.Paused)
            {
                return _status;
            }

            _paused = false;
            _phase = HistoryPhase.Reading;
            _historyCts = null;
        }

        await PersistHistoryAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        Wake();
        return Status;
    }

    /// <inheritdoc />
    public async ValueTask<StatisticsStatus> StopHereAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_phase is not (HistoryPhase.Reading or HistoryPhase.Paused))
            {
                return _status;
            }

            // The statistics start where the reading is: what is older is not read, and "read more history" goes on from there.
            _floorDay = _completeFromDay ?? _floorDay;
            _floorQuarter = ComputeFloorQuarter(_floorDay);
            _stopped = true;
            _paused = false;
            _phase = HistoryPhase.Stopped;
            _todo = null;
            CancelHistoryReading();
        }

        await PersistHistoryAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        return Status;
    }

    /// <inheritdoc />
    public async ValueTask<int> ForgetDeletedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var (sessions, days) = await _store.ForgetDeletedAsync(cancellationToken).ConfigureAwait(false);
        if (sessions > 0 && days.Count > 0)
        {
            RaiseDataChanged(days, []);
        }

        return sessions;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CancelHistoryReading();
            _catchUp.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (await ProcessDueFlowAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        HistoryPhase phase;
        lock (_gate)
        {
            phase = _phase;
        }

        switch (phase)
        {
            case HistoryPhase.Reading:
                return await ProcessNextHistoryAsync(cancellationToken).ConfigureAwait(false);
            case HistoryPhase.Done when _time.GetUtcNow() - _lastRescan >= _options.RescanInterval:
                return await RescanAsync(cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task<bool> ProcessDueFlowAsync(CancellationToken cancellationToken)
    {
        HistoryChoice? choice;
        lock (_gate)
        {
            choice = _choice;
        }

        var now = _time.GetUtcNow();
        var due = _flow.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToArray();
        if (due.Length == 0)
        {
            return false;
        }

        foreach (var sessionId in due)
        {
            _flow.TryRemove(sessionId, out _);
            if (choice is null)
            {
                continue;
            }

            try
            {
                await CatchUpFromFlowAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _options.Logger?.Warn($"Statistics could not catch up session {sessionId}: {exception.Message}");
            }
        }

        await RefreshProjectNamesAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        return true;
    }

    private async Task CatchUpFromFlowAsync(string sessionId, CancellationToken cancellationToken)
    {
        var file = await _journals.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var row = await _store.GetJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            if (row is { Deleted: false })
            {
                await _store.MarkDeletedAsync([sessionId], cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (row is null && file.LastWriteUtc < FloorTime())
        {
            return;
        }

        if (row is not null && !row.Deleted && row.FactsVersion == SessionFactsState.CurrentVersion && row.FileLength == file.Length && row.FileStampTicks == file.LastWriteUtc.UtcTicks && !NeedsSettling(row, file))
        {
            return;
        }

        await CatchUpSessionAsync(file, row, restart: false, history: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProcessNextHistoryAsync(CancellationToken cancellationToken)
    {
        if (_todo is null)
        {
            await BuildTodoAsync(cancellationToken).ConfigureAwait(false);
        }

        CancellationToken token;
        lock (_gate)
        {
            if (_phase != HistoryPhase.Reading)
            {
                return false;
            }

            _historyCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _historyCts.Token;
        }

        TodoItem? item;
        lock (_gate)
        {
            item = _todo is { Count: > 0 } queue ? queue.Peek() : null;
        }

        if (item is null)
        {
            await FinishReadingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        BeginReadingClock();
        lock (_gate)
        {
            _currentSession = item.File.SessionId;
        }

        Publish();
        try
        {
            var row = item.Restart ? null : await _store.GetJournalAsync(item.File.SessionId, token).ConfigureAwait(false);
            var file = await _journals.GetAsync(item.File.SessionId, token).ConfigureAwait(false) ?? item.File;
            await CatchUpSessionAsync(file, row, item.Restart, history: true, token).ConfigureAwait(false);
            lock (_gate)
            {
                // The choice may have been changed while the session was read: the list is then another one.
                if (_todo is { } queue && queue.Count > 0 && ReferenceEquals(queue.Peek(), item))
                {
                    queue.Dequeue();
                    _sessionsDone++;
                    _oldestReached = MinDay(_oldestReached, _days.DayOf(item.File.LastWriteUtc));
                    if (_reason == "first-read")
                    {
                        _completeFromDay = queue.Count > 0 ? NextDay(_days.DayOf(queue.Peek().File.LastWriteUtc)) : _floorDay;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Paused or stopped here: the cursor of what was read is saved, and the session is read on from there.
            StopReadingClock();
            Publish();
            return false;
        }
        catch (Exception exception)
        {
            _options.Logger?.Warn($"Statistics could not read session {item.File.SessionId}: {exception.Message}");
            lock (_gate)
            {
                if (_todo is { } queue && queue.Count > 0 && ReferenceEquals(queue.Peek(), item))
                {
                    queue.Dequeue();
                    _sessionsDone++;
                    _skippedCount++;
                    if (_skipped.Count < _options.MaxSkippedListed)
                    {
                        _skipped.Add(new SkippedSession(item.File.SessionId, exception.Message));
                    }
                }
            }
        }

        await RefreshProjectNamesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _currentSession = null;
        }

        Publish();
        return true;
    }

    private async Task FinishReadingAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _phase = HistoryPhase.Done;
            _todo = null;
            _reason = null;
            _currentSession = null;
            _completeFromDay = _floorDay;
            _lastRescan = _time.GetUtcNow();
        }

        StopReadingClock();
        await PersistHistoryAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task<bool> RescanAsync(CancellationToken cancellationToken)
    {
        _lastRescan = _time.GetUtcNow();
        await BuildTodoAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_todo is { Count: > 0 })
            {
                _phase = HistoryPhase.Reading;
                return true;
            }

            _todo = null;
            return false;
        }
    }

    private async Task BuildTodoAsync(CancellationToken cancellationToken)
    {
        var rows = await _store.ListJournalsAsync(cancellationToken).ConfigureAwait(false);
        var floorTime = FloorTime();
        var floorQuarter = _floorQuarter;
        var items = new List<TodoItem>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var file in _journals.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            present.Add(file.SessionId);
            if (_failed.TryGetValue(file.SessionId, out var failed) && failed == (file.Length, file.LastWriteUtc.UtcTicks))
            {
                continue;
            }

            if (!rows.TryGetValue(file.SessionId, out var row))
            {
                if (file.LastWriteUtc >= floorTime)
                {
                    items.Add(new TodoItem(file, Restart: false, file.Length));
                }

                continue;
            }

            if (row.FactsVersion != SessionFactsState.CurrentVersion || (floorQuarter < row.FloorQuarter && !row.Deleted))
            {
                items.Add(new TodoItem(file, Restart: true, file.Length));
            }
            else if (row.Deleted || row.FileLength != file.Length || row.FileStampTicks != file.LastWriteUtc.UtcTicks)
            {
                items.Add(new TodoItem(file, Restart: false, Math.Max(0, file.Length - row.Offset)));
            }
            else if (NeedsSettling(row, file))
            {
                items.Add(new TodoItem(file, Restart: false, 0));
            }
        }

        var gone = rows.Values.Where(row => !row.Deleted && !present.Contains(row.SessionId)).Select(static row => row.SessionId).ToArray();
        if (gone.Length > 0)
        {
            await _store.MarkDeletedAsync(gone, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _todo = new Queue<TodoItem>(items.OrderByDescending(static item => item.File.LastWriteUtc));
            ResetProgress();
            _sessionsTotal = items.Count;
            _bytesTotal = items.Sum(static item => item.BytesToRead);
            if (_reason is null or "catch-up" or "facts-improved")
            {
                _reason = items.Any(static item => item.Restart) ? "facts-improved" : "catch-up";
            }

            // Only the first reading fills in from the present towards the past. Reading more history keeps the old floor until it is
            // done, and the numbers of a catch-up or of a new version of the facts are complete: the old ones stay until the new ones are saved.
            if (_reason == "first-read")
            {
                _completeFromDay = items.Count > 0 ? NextDay(_days.DayOf(_todo.Peek().File.LastWriteUtc)) : _floorDay;
            }
            else if (_reason != "extended")
            {
                _completeFromDay = _floorDay;
            }
        }

        Publish();
    }

    private async Task CatchUpSessionAsync(SessionJournalFile file, JournalRow? row, bool restart, bool history, CancellationToken cancellationToken)
    {
        try
        {
            await CatchUpCoreAsync(file, row, restart, history, cancellationToken).ConfigureAwait(false);
            _failed.Remove(file.SessionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _failed[file.SessionId] = (file.Length, file.LastWriteUtc.UtcTicks);
            throw;
        }
    }

    private async Task CatchUpCoreAsync(SessionJournalFile file, JournalRow? row, bool restart, bool history, CancellationToken cancellationToken)
    {
        var sessionId = file.SessionId;
        var cursor = restart || row is null ? null : row.ToCursor();
        FactBatch? accumulated = null;
        var replace = restart;
        var floorQuarter = _floorQuarter;
        var changedDays = new SortedSet<int>();
        var changedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sessionId };
        while (true)
        {
            var opened = await _journals.OpenAsync(sessionId, cursor?.Offset ?? 0, cancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                await _store.MarkDeletedAsync([sessionId], cancellationToken).ConfigureAwait(false);
                return;
            }

            CatchUpResult result;
            await using (opened.ConfigureAwait(false))
            {
                var fromCursor = cursor;
                result = await RunOnReaderThreadAsync(() => _catchUp.CatchUp(sessionId, opened, fromCursor, _options.ChunkBytes, cancellationToken)).ConfigureAwait(false);
            }

            cursor = result.Cursor;
            replace |= result.Restarted;
            if (history)
            {
                lock (_gate)
                {
                    _bytesDone += result.Scan.BytesConsumed;
                }

                Publish();
            }

            var done = result.ReachedEnd || cancellationToken.IsCancellationRequested;
            if (accumulated is null)
            {
                accumulated = result.Batch;
            }
            else
            {
                accumulated.Merge(result.Batch);
            }

            if (done && result.ReachedEnd && IsDead(file, cursor.State))
            {
                var reducer = new SessionFactsReducer(sessionId, cursor.State.Clone());
                reducer.InterruptOpenRuns();
                accumulated.Merge(reducer.TakeBatch());
                cursor = cursor with { State = reducer.State };
            }

            // A session that is read again from the start shows its old numbers until the new ones are complete, so what is read is
            // kept and saved once; a session that goes on is saved chunk by chunk.
            if (!done && replace)
            {
                continue;
            }

            var request = new ApplyRequest
            {
                SessionId = sessionId,
                Batch = accumulated,
                Cursor = cursor,
                Replace = replace,
                FileLength = result.ReachedEnd ? file.Length : 0,
                FileStampTicks = result.ReachedEnd ? file.LastWriteUtc.UtcTicks : 0,
                FloorQuarter = floorQuarter,
                Meta = history ? HistoryMeta() : null,
            };
            var applied = await _store.ApplyAsync(request, CancellationToken.None).ConfigureAwait(false);
            foreach (var day in applied.ChangedDays)
            {
                changedDays.Add(day);
            }

            if (accumulated.Session?.ProjectRef is { } project && !_projectNames.ContainsKey(project))
            {
                _projectNamesStale = true;
            }

            accumulated = null;
            replace = false;
            if (changedDays.Count > 0)
            {
                RaiseDataChanged([.. changedDays], [.. changedSessions]);
                changedDays.Clear();
            }

            if (done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Between two chunks of a large journal the flow goes first.
            if (history && _flow.Any(pair => pair.Value <= _time.GetUtcNow()))
            {
                await ProcessDueFlowAsync(_lifetime).ConfigureAwait(false);
            }
        }
    }

    private bool IsDead(SessionJournalFile file, SessionFactsState state)
        => state.OpenRuns.Count > 0 && _time.GetUtcNow() - file.LastWriteUtc > _options.DeadAfter;

    private bool NeedsSettling(JournalRow row, SessionJournalFile file)
        => row.OpenRuns > 0 && _time.GetUtcNow() - file.LastWriteUtc > _options.DeadAfter;

    private IReadOnlyDictionary<string, string> HistoryMeta()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CompleteFromKey] = (_completeFromDay ?? 0).ToString(CultureInfo.InvariantCulture),
            };
        }
    }

    private async Task PersistHistoryAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string?> values;
        lock (_gate)
        {
            values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ChoiceKey] = _choice?.ToText(),
                [FloorKey] = _floorDay?.ToString(CultureInfo.InvariantCulture),
                [PausedKey] = _paused ? "1" : "0",
                [StoppedKey] = _stopped ? "1" : "0",
                [DoneKey] = _phase == HistoryPhase.Done ? "1" : "0",
                [ReasonKey] = _reason is "first-read" or "extended" ? _reason : null,
                [CompleteFromKey] = _completeFromDay?.ToString(CultureInfo.InvariantCulture),
            };
        }

        await _store.SetMetaAsync(values, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshProjectNamesAsync(CancellationToken cancellationToken)
    {
        if (_options.ResolveProjectNames is not { } resolve || !_projectNamesStale || (_time.GetUtcNow() - _lastProjectRefresh).TotalSeconds < ProjectNameRefreshSeconds)
        {
            return;
        }

        _lastProjectRefresh = _time.GetUtcNow();
        try
        {
            var names = await resolve(cancellationToken).ConfigureAwait(false);
            foreach (var (reference, name) in names)
            {
                _projectNames[reference] = name;
            }

            await _store.SetProjectNamesAsync(names, cancellationToken).ConfigureAwait(false);
            _projectNamesStale = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _options.Logger?.Warn($"Statistics could not resolve the names of the projects: {exception.Message}");
        }
    }

    private void RaiseDataChanged(IReadOnlyList<int> days, IReadOnlyList<string> sessions)
    {
        if (days.Count == 0)
        {
            return;
        }

        var revision = Interlocked.Increment(ref _revision);
        DataChanged?.Invoke(new StatisticsDataChange(revision, days[0], days[^1], sessions));
    }

    private void Publish()
    {
        StatisticsStatus status;
        lock (_gate)
        {
            var elapsed = ReadingSeconds();
            double? speed = elapsed >= 0.2 && _bytesDone > 0 ? _bytesDone / elapsed : null;
            double? eta = speed is > 0 && _bytesTotal > _bytesDone ? (_bytesTotal - _bytesDone) / speed.Value : null;
            status = new StatisticsStatus
            {
                State = _phase switch
                {
                    HistoryPhase.Starting => HistoryState.Starting,
                    HistoryPhase.NeedsChoice => HistoryState.NeedsChoice,
                    HistoryPhase.Reading => HistoryState.Reading,
                    HistoryPhase.Paused => HistoryState.Paused,
                    HistoryPhase.Stopped => HistoryState.StoppedHere,
                    HistoryPhase.Done => HistoryState.Done,
                    _ => HistoryState.Failed,
                },
                Reason = _phase is HistoryPhase.Reading or HistoryPhase.Paused ? _reason : null,
                Choice = _choice?.ToText(),
                FloorDay = _floorDay,
                SessionsTotal = _sessionsTotal,
                SessionsDone = _sessionsDone,
                BytesTotal = _bytesTotal,
                BytesDone = _bytesDone,
                OldestDateReached = _oldestReached,
                CompleteFromDay = _completeFromDay,
                BytesPerSecond = speed,
                EtaSeconds = eta,
                CurrentSessionId = _currentSession,
                SkippedCount = _skippedCount,
                Skipped = [.. _skipped],
                PendingFlow = _flow.Count,
                Revision = Volatile.Read(ref _revision),
                Error = _error,
            };
            Volatile.Write(ref _status, status);
        }

        StatusChanged?.Invoke(status);
    }

    private void ResetProgress()
    {
        _sessionsTotal = 0;
        _sessionsDone = 0;
        _bytesTotal = 0;
        _bytesDone = 0;
        _oldestReached = null;
        _currentSession = null;
        _skipped.Clear();
        _skippedCount = 0;
        _readingTicks = 0;
        _readingStartedAt = 0;
    }

    private void BeginReadingClock()
    {
        lock (_gate)
        {
            if (_readingStartedAt == 0)
            {
                _readingStartedAt = _time.GetTimestamp();
            }
        }
    }

    private void StopReadingClock()
    {
        lock (_gate)
        {
            if (_readingStartedAt != 0)
            {
                _readingTicks += _time.GetTimestamp() - _readingStartedAt;
                _readingStartedAt = 0;
            }
        }
    }

    private double ReadingSeconds()
    {
        var ticks = _readingTicks + (_readingStartedAt != 0 ? _time.GetTimestamp() - _readingStartedAt : 0);
        return ticks / (double)_time.TimestampFrequency;
    }

    private void CancelHistoryReading()
    {
        var source = _historyCts;
        _historyCts = null;
        if (source is not null)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(30);
        var now = _time.GetUtcNow();
        foreach (var due in _flow.Values)
        {
            var wait = due - now;
            if (wait < delay)
            {
                delay = wait;
            }
        }

        bool done;
        lock (_gate)
        {
            done = _phase == HistoryPhase.Done;
        }

        if (done)
        {
            var rescan = _lastRescan + _options.RescanInterval - now;
            if (rescan < delay)
            {
                delay = rescan;
            }
        }

        if (delay > TimeSpan.Zero)
        {
            await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private bool GoesFurtherBack(int? newFloor)
    {
        if (_floorDay is null)
        {
            return false;
        }

        return newFloor is null || newFloor < _floorDay;
    }

    private int? FloorDayOf(HistoryChoice choice)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_time.GetUtcNow().UtcDateTime, _days.TimeZone));
        return choice.Kind switch
        {
            HistoryChoiceKind.All => null,
            HistoryChoiceKind.Days => LocalDays.ToDay(today.AddDays(1 - choice.Days)),
            _ => LocalDays.ToDay(today),
        };
    }

    private int ComputeFloorQuarter(int? floorDay)
        => floorDay is { } day ? _days.FirstQuarterOf(LocalDays.ToDate(day)) : int.MinValue;

    private DateTimeOffset FloorTime()
        => _floorQuarter == int.MinValue ? DateTimeOffset.MinValue : new QuarterHour(_floorQuarter).Start;

    private LocalDays _days => _store.Days;

    private static int? MinDay(int? current, int day) => current is { } value && value <= day ? value : day;

    private int NextDay(int day) => LocalDays.ToDay(LocalDays.ToDate(day).AddDays(1));

    private async Task<T> RunOnReaderThreadAsync<T>(Func<T> work)
    {
        // The reading is CPU and disk work: it runs on a thread of its own with a priority below normal, never on the pool.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "statistics-reader",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return await completion.Task.ConfigureAwait(false);
    }
}
