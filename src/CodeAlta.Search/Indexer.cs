namespace CodeAlta.Search;

/// <summary>
/// Processes queued indexing jobs and writes indexed documents.
/// </summary>
public sealed class Indexer
{
    private readonly IndexingQueue _queue;
    private readonly DocumentIndexStore _indexStore;
    private readonly EmbeddingModelManager _embeddingModelManager;
    private DateTimeOffset? _lastCompletedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="Indexer"/> class.
    /// </summary>
    /// <param name="queue">Indexing queue.</param>
    /// <param name="indexStore">Document index store.</param>
    /// <param name="embeddingModelManager">Embedding model manager.</param>
    /// <exception cref="ArgumentNullException">Thrown when required arguments are <see langword="null"/>.</exception>
    public Indexer(
        IndexingQueue queue,
        DocumentIndexStore indexStore,
        EmbeddingModelManager embeddingModelManager)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(indexStore);
        ArgumentNullException.ThrowIfNull(embeddingModelManager);

        _queue = queue;
        _indexStore = indexStore;
        _embeddingModelManager = embeddingModelManager;
    }

    /// <summary>
    /// Gets indexing status.
    /// </summary>
    public IndexingStatus Status => new()
    {
        QueueDepth = _queue.Depth,
        LastCompletedAt = _lastCompletedAt,
    };

    /// <summary>
    /// Enqueues an indexing job.
    /// </summary>
    /// <param name="job">Job to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task EnqueueAsync(IndexingJob job, CancellationToken cancellationToken = default)
    {
        return _queue.EnqueueAsync(job, cancellationToken);
    }

    /// <summary>
    /// Processes the next queued job.
    /// </summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processed job id.</returns>
    public async Task<string> ProcessNextAsync(
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var job = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
        await ProcessJobAsync(job, progress, cancellationToken).ConfigureAwait(false);
        return job.JobId;
    }

    /// <summary>
    /// Runs continuous indexing until cancelled.
    /// </summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var job = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            await ProcessJobAsync(job, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(
        IndexingJob job,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (job.Documents.Count == 0)
        {
            _lastCompletedAt = DateTimeOffset.UtcNow;
            return;
        }

        var embedder = await _embeddingModelManager.GetEmbedderAsync(cancellationToken).ConfigureAwait(false);
        await _indexStore.UpsertDocumentsAsync(job.Documents, embedder, cancellationToken).ConfigureAwait(false);

        progress?.Report(
            new IndexingProgress
            {
                JobId = job.JobId,
                Processed = job.Documents.Count,
                Total = job.Documents.Count,
            });

        _lastCompletedAt = DateTimeOffset.UtcNow;
    }
}
