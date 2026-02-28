using LLama;
using LLama.Common;
using LLama.Native;

namespace CodeAlta.Search;

/// <summary>
/// Provides LLamaSharp-backed embeddings from a local GGUF model.
/// </summary>
public sealed class LlamaSharpEmbedder : IEmbedder, IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly SemaphoreSlim _initializationLock = new(initialCount: 1, maxCount: 1);
    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private int _dimension;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlamaSharpEmbedder"/> class.
    /// </summary>
    /// <param name="modelPath">Path to a local GGUF embedding model.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modelPath"/> is empty.</exception>
    public LlamaSharpEmbedder(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Model path is required.", nameof(modelPath));
        }

        _modelPath = modelPath;
    }

    /// <inheritdoc />
    public int Dimension => _dimension;

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var embedder = _embedder ?? throw new InvalidOperationException("Embedder was not initialized.");

        var results = new List<float[]>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embeddings = await embedder.GetEmbeddings(input ?? string.Empty).ConfigureAwait(false);
            if (embeddings.Count == 0)
            {
                results.Add([]);
                continue;
            }

            var vector = embeddings[0].ToArray();
            Normalize(vector);
            _dimension = vector.Length;
            results.Add(vector);
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _embedder?.Dispose();

        _weights?.Dispose();
        _initializationLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_embedder is not null)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_embedder is not null)
            {
                return;
            }

            var parameters = new ModelParams(_modelPath)
            {
                PoolingType = LLamaPoolingType.Mean,
            };
            _weights = LLamaWeights.LoadFromFile(parameters);
            _embedder = new LLamaEmbedder(_weights, parameters);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static void Normalize(Span<float> vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        if (sum <= 0)
        {
            return;
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }
}
