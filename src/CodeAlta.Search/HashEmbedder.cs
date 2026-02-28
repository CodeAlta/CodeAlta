using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Search;

/// <summary>
/// Provides deterministic local embeddings based on SHA-256 hashing.
/// </summary>
public sealed class HashEmbedder : IEmbedder
{
    private readonly int _dimension;

    /// <summary>
    /// Initializes a new instance of the <see cref="HashEmbedder"/> class.
    /// </summary>
    /// <param name="dimension">Output dimension.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="dimension"/> is not positive.</exception>
    public HashEmbedder(int dimension = 32)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), "Dimension must be positive.");
        }

        _dimension = dimension;
    }

    /// <inheritdoc />
    public int Dimension => _dimension;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<float[]>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
            var vector = new float[_dimension];

            for (var i = 0; i < vector.Length; i++)
            {
                var value = bytes[i % bytes.Length];
                vector[i] = (value - 127.5f) / 127.5f;
            }

            Normalize(vector);
            results.Add(vector);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(results);
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
