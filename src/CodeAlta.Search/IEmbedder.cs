namespace CodeAlta.Search;

/// <summary>
/// Defines embedding generation for text inputs.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// Gets embedding dimension.
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// Embeds a batch of input texts.
    /// </summary>
    /// <param name="inputs">Input texts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Embeddings for each input in order.</returns>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}
