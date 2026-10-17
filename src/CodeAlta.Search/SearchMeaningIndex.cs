using System.Text.Json;

namespace CodeAlta.Search;

/// <summary>
/// The vectors of the active vector set in memory, for search by meaning. It loads them on a background task, then
/// follows the log of changes of the index before each search: a chunk added, removed or moved, and a vector that came,
/// change its rows, so that the rows of a deleted session vanish from the next answer. A change of the active set (a
/// new model made complete) loads the new set. The embedding of the query is the caller's.
/// </summary>
public sealed class SearchMeaningIndex : IAsyncDisposable
{
    private const int MaxChangesPerSync = 50_000;
    private const long PruneEvery = 4_096;

    private readonly SearchIndexStore _store;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _startLock = new();
    private Loaded? _loaded;
    private long _pruned;
    private Task? _loading;

    /// <summary>Creates the index of a store; nothing is loaded before <see cref="Start"/> or <see cref="LoadAsync"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="log">Gets what is worth a line in the log, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public SearchMeaningIndex(SearchIndexStore store, Action<string>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>Gets whether the active set was loaded once: until then, a search answers nothing.</summary>
    public bool IsLoaded => Volatile.Read(ref _loaded) is not null;

    /// <summary>Gets the set in memory, or null when there is no active set or it is not loaded yet.</summary>
    public SearchVectorSet? ActiveSet => Volatile.Read(ref _loaded)?.Set;

    /// <summary>Gets the chunks that have a row in memory.</summary>
    public int Count => Volatile.Read(ref _loaded)?.Index?.Count ?? 0;

    /// <summary>Gets the share of chunks that have a vector in the active set, from 0 to 1, as of the last load or sync.</summary>
    public double Coverage => Volatile.Read(ref _loaded) is { Index: { } index, Chunks: var chunks } ? chunks <= 0 ? 1 : Math.Min(1, (double)index.Count / chunks) : 0;

    /// <summary>Starts loading the active set on a background task.</summary>
    /// <returns>The task of the load.</returns>
    public Task Start()
    {
        lock (_startLock)
        {
            return _loading ??= Task.Run(async () =>
            {
                try
                {
                    await LoadAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    _log?.Invoke($"Search could not load its vectors: {exception.Message}");
                }
            });
        }
    }

    /// <summary>Loads the active set now, replacing what is in memory.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies what changed in the index since the last load or sync: a search does it first.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _loaded) is not { } loaded)
            {
                await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (loaded.Set is null || loaded.Generation != _store.Generation)
            {
                // No set was active, or the file was made again: whatever is active now is loaded.
                await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var changes = await _store.ReadVectorChangesAsync(loaded.Set.Id, loaded.Epoch, loaded.Cursor, MaxChangesPerSync, cancellationToken).ConfigureAwait(false);
            if (changes.Reload || changes.Active is null)
            {
                await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var index = loaded.Index!;
            foreach (var chunkId in changes.Touched) index.Remove(chunkId);
            foreach (var row in changes.Rows)
            {
                if (!index.Contains(row.ChunkId)) index.Add(row.ChunkId, row.Codes, row.Scale, row.Info);
            }

            Volatile.Write(ref _loaded, loaded with { Cursor = changes.Cursor, Chunks = changes.Chunks >= 0 ? changes.Chunks : loaded.Chunks });
            if (changes.Cursor - _pruned >= PruneEvery)
            {
                await _store.PruneVectorChangesAsync(changes.Cursor, cancellationToken).ConfigureAwait(false);
                _pruned = changes.Cursor;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadLockedAsync(CancellationToken cancellationToken)
    {
        var generation = _store.Generation;
        var load = await _store.LoadVectorsAsync(null, cancellationToken).ConfigureAwait(false);
        _pruned = load?.Cursor ?? 0;
        Volatile.Write(ref _loaded, load is null
            ? new Loaded(null, null, "", 0, 0, generation)
            : new Loaded(load.Set, load.Index, load.Epoch, load.Cursor, load.Chunks, generation));
    }

    /// <summary>
    /// Finds the chunks closest to the vector of a query in a scope, the best first, after applying what changed in the
    /// index. Nothing is found before the first load, or when the vector is not of the width of the active set.
    /// </summary>
    /// <param name="queryVector">The vector of the query, made by the model of <see cref="ActiveSet"/>.</param>
    /// <param name="scope">The scope; <see cref="SearchScope.CatalogFilter"/> is left to the caller.</param>
    /// <param name="count">The most chunks.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The chunks and their similarity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="queryVector"/> or <paramref name="scope"/> is null.</exception>
    public async ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(float[] queryVector, SearchScope scope, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsLoaded) return [];
        try
        {
            await SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            // What is in memory still answers.
            _log?.Invoke($"Search could not follow the changes of its vectors: {exception.Message}");
        }

        return Volatile.Read(ref _loaded) is { Index: { } index } && index.Dimensions == queryVector.Length
            ? index.Search(queryVector, scope, count)
            : [];
    }

    private static bool IsStoreFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task? loading;
        lock (_startLock) loading = _loading;
        if (loading is not null) await loading.ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private sealed record Loaded(SearchVectorSet? Set, SearchVectorIndex? Index, string Epoch, long Cursor, long Chunks, int Generation);
}
