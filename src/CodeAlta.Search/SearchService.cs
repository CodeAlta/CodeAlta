namespace CodeAlta.Search;

/// <summary>
/// Provides FTS and hybrid retrieval queries.
/// </summary>
public sealed class SearchService
{
    private readonly DocumentIndexStore _indexStore;
    private readonly EmbeddingModelManager _embeddingModelManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchService"/> class.
    /// </summary>
    /// <param name="indexStore">Document index store.</param>
    /// <param name="embeddingModelManager">Embedding model manager.</param>
    /// <exception cref="ArgumentNullException">Thrown when required arguments are <see langword="null"/>.</exception>
    public SearchService(
        DocumentIndexStore indexStore,
        EmbeddingModelManager embeddingModelManager)
    {
        ArgumentNullException.ThrowIfNull(indexStore);
        ArgumentNullException.ThrowIfNull(embeddingModelManager);

        _indexStore = indexStore;
        _embeddingModelManager = embeddingModelManager;
    }

    /// <summary>
    /// Executes a full-text-only query.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>FTS-ranked results.</returns>
    public Task<IReadOnlyList<SearchResult>> QueryFtsAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        return _indexStore.QueryFtsAsync(query, cancellationToken);
    }

    /// <summary>
    /// Executes hybrid retrieval: FTS prefilter followed by vector reranking.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hybrid-ranked results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<SearchResult>> QueryHybridAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Limit must be positive.");
        }

        var ftsResults = await _indexStore.QueryFtsAsync(query, cancellationToken).ConfigureAwait(false);
        if (ftsResults.Count == 0)
        {
            return [];
        }

        var embedder = await _embeddingModelManager.GetEmbedderAsync(cancellationToken).ConfigureAwait(false);
        var queryVector = (await embedder.EmbedAsync([query.Text], cancellationToken).ConfigureAwait(false))[0];
        var candidateIds = ftsResults.Select(static x => x.DocumentId).ToArray();
        var vectorDistances = await _indexStore.QueryVectorDistancesAsync(
            candidateIds,
            queryVector,
            query.WorkspaceId,
            query.ProjectId,
            cancellationToken).ConfigureAwait(false);

        Dictionary<long, float[]>? embeddings = null;
        if (vectorDistances.Count != ftsResults.Count)
        {
            embeddings = await _indexStore.GetEmbeddingsAsync(candidateIds, cancellationToken).ConfigureAwait(false);
        }

        var reranked = new List<SearchResult>(ftsResults.Count);
        foreach (var result in ftsResults)
        {
            double vectorScore;
            if (vectorDistances.TryGetValue(result.DocumentId, out var distance))
            {
                vectorScore = 1.0 / (1.0 + Math.Max(0, distance));
            }
            else if (embeddings is not null && embeddings.TryGetValue(result.DocumentId, out var embedding))
            {
                vectorScore = Cosine(queryVector, embedding);
            }
            else
            {
                vectorScore = 0;
            }

            var ftsComponent = result.FtsScore.HasValue
                ? 1.0 / (1.0 + Math.Abs(result.FtsScore.Value))
                : 0;
            var combined = (vectorScore * 0.7) + (ftsComponent * 0.3);

            reranked.Add(
                result with
                {
                    VectorScore = vectorScore,
                    CombinedScore = combined,
                });
        }

        return reranked
            .OrderByDescending(static x => x.CombinedScore)
            .Take(query.Limit)
            .ToArray();
    }

    private static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0;
        }

        return dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
