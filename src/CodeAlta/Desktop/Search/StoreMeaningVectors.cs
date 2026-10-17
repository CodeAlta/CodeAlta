using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// The vectors of search by meaning in the index of the window: one set per model, revision, prompts and runtime, and
/// the active set in memory for the queries. A set of a new model fills while the old one stays active; it becomes
/// active once every chunk has its vector, and queries by meaning wait for it meanwhile, so that only one model runs.
/// </summary>
internal sealed class StoreMeaningVectors : IMeaningVectors
{
    private readonly SearchIndexStore _store;
    private readonly SearchMeaningIndex _index;
    private SearchVectorSet? _open;
    private (int Embedded, int Total) _coverage;

    public StoreMeaningVectors(SearchIndexStore store, Action<string>? log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = new SearchMeaningIndex(store, log);
    }

    /// <inheritdoc />
    public (int Embedded, int Total) Coverage => Volatile.Read(ref _open) is null ? (0, 0) : _coverage;

    /// <inheritdoc />
    public async Task OpenAsync(EmbeddingModelDefinition model, string runtimeVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var set = await _store.OpenVectorSetAsync(Identity(model, runtimeVersion), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _open, set);
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        await CountAsync(set, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeaningChunk>> MissingAsync(int count, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _open) is not { } set) return [];
        var missing = await _store.ListMissingVectorsAsync(set.Id, count, cancellationToken).ConfigureAwait(false);
        if (missing.Count == 0 && _index.ActiveSet?.Id != set.Id)
        {
            // The set of a new model is complete: it replaces the old one, which is kept retired.
            await _store.CompleteVectorSetAsync(set.Id, cancellationToken).ConfigureAwait(false);
            await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return [.. missing.Select(static work => new MeaningChunk(work.ChunkId, work.EmbedHash, work.Title, work.Text))];
    }

    /// <inheritdoc />
    public async Task PutAsync(IReadOnlyList<(MeaningChunk Chunk, float[] Vector)> vectors, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _open) is not { } set || vectors.Count == 0) return;
        await _store.PutVectorsAsync(set.Id, [.. vectors.Select(static pair => new SearchVectorInput(pair.Chunk.EmbedHash, pair.Vector))], cancellationToken).ConfigureAwait(false);
        await CountAsync(set, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(float[] query, SearchScope scope, int limit, CancellationToken cancellationToken)
    {
        // The vectors in memory are those of the active set: they are compared with a query of the same model only.
        if (Volatile.Read(ref _open) is not { } set || _index.ActiveSet?.Id != set.Id) return [];
        return await _index.SearchAsync(query, scope, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _index.SyncAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _open) is { } set) await CountAsync(set, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(EmbeddingModelDefinition model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var set in await _store.GetVectorSetsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(set.Identity.Model, model.Id, StringComparison.OrdinalIgnoreCase) && set.Id != Volatile.Read(ref _open)?.Id)
                await _store.DeleteVectorSetAsync(set.Id, cancellationToken).ConfigureAwait(false);
        }

        await _store.CompactVectorsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SearchVectorSetIdentity Identity(EmbeddingModelDefinition model, string runtimeVersion)
        => new(model.Id, model.Revision, model.Dimensions, model.PromptsVersion, runtimeVersion);

    private async Task CountAsync(SearchVectorSet set, CancellationToken cancellationToken)
    {
        var coverage = await _store.GetVectorCoverageAsync(set.Id, cancellationToken).ConfigureAwait(false);
        _coverage = ((int)Math.Min(int.MaxValue, coverage.Embedded), (int)Math.Min(int.MaxValue, coverage.Chunks));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _index.DisposeAsync();
}
