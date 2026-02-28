namespace CodeAlta.Search;

/// <summary>
/// Resolves the active embedding model for indexing and query embedding.
/// </summary>
public sealed class EmbeddingModelManager
{
    private readonly IEmbedder _embedder;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddingModelManager"/> class.
    /// </summary>
    /// <param name="embedder">Optional embedder implementation.</param>
    public EmbeddingModelManager(IEmbedder? embedder = null)
    {
        _embedder = embedder ?? new HashEmbedder();
    }

    /// <summary>
    /// Gets the active embedder.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active <see cref="IEmbedder"/>.</returns>
    public Task<IEmbedder> GetEmbedderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_embedder);
    }
}
