using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Search.Embedding;

/// <summary>The kinds of the frames between the host and the embedding worker.</summary>
internal static class EmbeddingFrameKind
{
    /// <summary>Host to worker: load a model (<see cref="EmbeddingWorkerLoad"/> as JSON).</summary>
    public const byte Load = 1;

    /// <summary>Worker to host: the model is loaded; its content is the width of its vectors as text.</summary>
    public const byte Loaded = 2;

    /// <summary>Host to worker: embed texts (<see cref="EmbeddingRequest"/> as JSON).</summary>
    public const byte Embed = 3;

    /// <summary>Worker to host: the vectors of a request (<see cref="EmbeddingProtocol.WriteVectors"/>).</summary>
    public const byte Vectors = 4;

    /// <summary>Worker to host: what went wrong, as text.</summary>
    public const byte Error = 5;

    /// <summary>Host to worker: end.</summary>
    public const byte Exit = 6;
}

/// <summary>Texts to embed, each with its prompt.</summary>
internal sealed record EmbeddingRequest(IReadOnlyList<string> Texts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmbeddingWorkerLoad))]
[JsonSerializable(typeof(EmbeddingRequest))]
internal sealed partial class EmbeddingJsonContext : JsonSerializerContext;

/// <summary>The content of the frames: JSON for requests, little-endian floats for vectors.</summary>
internal static class EmbeddingProtocol
{
    /// <summary>Writes vectors: their count, their width, then each vector.</summary>
    public static byte[] WriteVectors(IReadOnlyList<float[]> vectors, int dimensions)
    {
        var content = new byte[8 + vectors.Count * dimensions * 4];
        BinaryPrimitives.WriteInt32LittleEndian(content, vectors.Count);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(4), dimensions);
        var offset = 8;
        foreach (var vector in vectors)
        {
            if (vector.Length != dimensions) throw new InvalidDataException("A vector has another width.");
            foreach (var value in vector)
            {
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset), value);
                offset += 4;
            }
        }

        return content;
    }

    /// <summary>Reads vectors written by <see cref="WriteVectors"/>.</summary>
    /// <exception cref="InvalidDataException">The content is not such vectors.</exception>
    public static float[][] ReadVectors(ReadOnlySpan<byte> content)
    {
        if (content.Length < 8) throw new InvalidDataException("The vectors are cut.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(content), dimensions = BinaryPrimitives.ReadInt32LittleEndian(content[4..]);
        if (count < 0 || dimensions is <= 0 or > 8192 || content.Length != 8 + (long)count * dimensions * 4) throw new InvalidDataException("The vectors have another size.");
        var vectors = new float[count][];
        var offset = 8;
        for (var row = 0; row < count; row++)
        {
            var vector = new float[dimensions];
            for (var dimension = 0; dimension < dimensions; dimension++, offset += 4) vector[dimension] = BinaryPrimitives.ReadSingleLittleEndian(content[offset..]);
            vectors[row] = vector;
        }

        return vectors;
    }
}

/// <summary>
/// The embedding worker: the process the model runs in, so that a failure of the native runtime fails a batch and
/// never the application, and so that the memory of the model is given back when it is idle. It answers frames on its
/// standard streams, one at a time, until they close or it is asked to exit.
/// </summary>
internal static class EmbeddingWorker
{
    /// <summary>The argument that starts this executable as the worker.</summary>
    public const string Option = "--embedding-worker";

    /// <summary>Runs the worker on the standard streams of the process.</summary>
    public static int RunProcess()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Run(input, output, new OnnxEmbeddingEngineFactory());
        return 0;
    }

    /// <summary>Answers the frames of a stream until it ends or asks to exit.</summary>
    public static void Run(Stream input, Stream output, IEmbeddingEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(factory);
        IEmbeddingEngine? engine = null;
        try
        {
            while (true)
            {
                WorkerFrame? next;
                try { next = WorkerFrames.Read(input); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException) { return; }
                if (next is not { } frame || frame.Kind == EmbeddingFrameKind.Exit) return;
                try
                {
                    switch (frame.Kind)
                    {
                        case EmbeddingFrameKind.Load:
                            engine?.Dispose();
                            engine = null;
                            var load = JsonSerializer.Deserialize(frame.Content, EmbeddingJsonContext.Default.EmbeddingWorkerLoad) ?? throw new InvalidOperationException("No model was given.");
                            engine = factory.Load(load);
                            WorkerFrames.Write(output, EmbeddingFrameKind.Loaded, frame.Correlation, Encoding.UTF8.GetBytes(engine.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                            break;
                        case EmbeddingFrameKind.Embed when engine is not null:
                            var request = JsonSerializer.Deserialize(frame.Content, EmbeddingJsonContext.Default.EmbeddingRequest) ?? throw new InvalidOperationException("No texts were given.");
                            var vectors = engine.Embed(request.Texts);
                            WorkerFrames.Write(output, EmbeddingFrameKind.Vectors, frame.Correlation, EmbeddingProtocol.WriteVectors(vectors, engine.Dimensions));
                            break;
                        case EmbeddingFrameKind.Embed:
                            WorkerFrames.Write(output, EmbeddingFrameKind.Error, frame.Correlation, "No model is loaded."u8);
                            break;
                    }
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    // A model that cannot be loaded or a text it refuses fails that request; the worker goes on.
                    WorkerFrames.Write(output, EmbeddingFrameKind.Error, frame.Correlation, Encoding.UTF8.GetBytes(exception.Message));
                }

                output.Flush();
            }
        }
        finally
        {
            engine?.Dispose();
        }
    }
}
