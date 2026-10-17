using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// Records the searches of the window and of agents in the activity of the index, out of the way of the queries. The window
/// asks again as each word is typed: a query being typed is held a moment and replaced by the next one, so that a search is
/// one row, with the hits and the times of the query the user stopped at.
/// </summary>
internal sealed class SearchActivityRecorder : IAsyncDisposable
{
    /// <summary>How long a query being typed waits for the next one before it is recorded.</summary>
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);

    private readonly SearchIndexStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _settle;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly ITimer _timer;
    private SearchActivityEntry? _pending;
    private Task _writes = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Creates the recorder of an index.</summary>
    /// <param name="store">The index.</param>
    /// <param name="time">The clock.</param>
    /// <param name="settle">How long a query being typed waits for the next one.</param>
    /// <param name="log">Gets what is worth a line in the log.</param>
    public SearchActivityRecorder(SearchIndexStore store, TimeProvider time, TimeSpan settle, Action<string>? log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settle = settle;
        _log = log;
        _timer = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Records a query and its answer; it returns at once.</summary>
    /// <param name="request">The query.</param>
    /// <param name="response">Its answer.</param>
    /// <returns>A completed task.</returns>
    public ValueTask RecordAsync(SearchRequest request, SearchResponse response)
    {
        var entry = SearchActivityEntry.Of(request, response, _time.GetUtcNow());
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (entry.Origin == SearchOrigin.Window)
            {
                // The query before this one was the same search, still being typed.
                _pending = null;
                if (request.PrefixLastTerm)
                {
                    _pending = entry;
                    _timer.Change(_settle, Timeout.InfiniteTimeSpan);
                    return ValueTask.CompletedTask;
                }
            }

            Write(entry);
        }

        return ValueTask.CompletedTask;
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_pending is not { } entry) return;
            _pending = null;
            Write(entry);
        }
    }

    // One write after the other, each failure logged and forgotten: the activity is not worth an error of a query.
    private void Write(SearchActivityEntry entry)
    {
        _writes = _writes.ContinueWith(async _ =>
        {
            try { await _store.RecordActivityAsync(entry).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            {
                _log?.Invoke("Search could not record a search: " + exception.Message);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Writes what waits, then stops.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        Task writes;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending is { } entry) Write(entry);
            _pending = null;
            _disposed = true;
            writes = _writes;
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
        await writes.ConfigureAwait(false);
    }
}
