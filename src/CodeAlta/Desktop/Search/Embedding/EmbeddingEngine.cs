using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace CodeAlta.Desktop.Search.Embedding;

/// <summary>What the worker is asked to load.</summary>
/// <param name="ModelId">The preset.</param>
/// <param name="ModelDirectory">The folder of its files.</param>
/// <param name="OnnxRuntime">The full path of the ONNX Runtime library: it is never loaded by its bare name.</param>
/// <param name="Threads">The threads of a run.</param>
internal sealed record EmbeddingWorkerLoad(string ModelId, string ModelDirectory, string OnnxRuntime, int Threads);

/// <summary>Turns texts into normalized vectors.</summary>
internal interface IEmbeddingEngine : IDisposable
{
    /// <summary>Gets the width of the vectors.</summary>
    int Dimensions { get; }

    /// <summary>Embeds texts, each given whole with its prompt; a text longer than the model takes is cut.</summary>
    /// <returns>One vector per text, in order.</returns>
    float[][] Embed(IReadOnlyList<string> texts);
}

/// <summary>Loads a model into an engine.</summary>
internal interface IEmbeddingEngineFactory
{
    /// <summary>Loads it.</summary>
    /// <exception cref="InvalidOperationException">The model cannot be loaded.</exception>
    IEmbeddingEngine Load(EmbeddingWorkerLoad load);
}

/// <summary>
/// The models of <see cref="EmbeddingModelCatalog"/> on the ONNX Runtime of sherpa-onnx, through
/// <c>Microsoft.ML.OnnxRuntime.Managed</c> 1.28 (the managed binding must not be newer than the native library).
/// </summary>
internal sealed class OnnxEmbeddingEngineFactory : IEmbeddingEngineFactory
{
    private string? _runtime;

    /// <inheritdoc />
    public IEmbeddingEngine Load(EmbeddingWorkerLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        var model = EmbeddingModelCatalog.Find(load.ModelId) ?? throw new InvalidOperationException($"Unknown model '{load.ModelId}'.");
        if (!File.Exists(load.OnnxRuntime)) throw new InvalidOperationException("The ONNX Runtime library is missing.");
        if (_runtime is null)
        {
            // The library is loaded by its full path, never by its bare name: Windows can have another onnxruntime.dll.
            OrtEnv.DisableDllImportResolver = true;
            var path = load.OnnxRuntime;
            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(path) : IntPtr.Zero);
            _runtime = path;
        }
        else if (!string.Equals(_runtime, load.OnnxRuntime, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The worker already runs another ONNX Runtime.");
        }

        return new OnnxEmbeddingEngine(model, load.ModelDirectory, Math.Clamp(load.Threads, 1, 8));
    }
}

/// <summary>One model loaded: its tokenizer and its session.</summary>
internal sealed class OnnxEmbeddingEngine : IEmbeddingEngine
{
    private readonly EmbeddingModelDefinition _model;
    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly string[] _extraInputs;

    public OnnxEmbeddingEngine(EmbeddingModelDefinition model, string directory, int threads)
    {
        _model = model;
        _tokenizer = CreateTokenizer(model, directory);
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableMemoryPattern = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        // Threads that spin between runs would keep a core busy for nothing in the background.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _session = new InferenceSession(Path.Combine(directory, model.ModelFile), options);
        // EmbeddingGemma 2 also takes the features of images, video and sound: empty for a text.
        _extraInputs = [.. _session.InputMetadata.Keys.Where(static name => name is not ("input_ids" or "attention_mask"))];
        Dimensions = model.Dimensions;
    }

    public int Dimensions { get; }

    /// <summary>Gets the token ids of a text as the model is given them: its start and end tokens, cut at the most tokens.</summary>
    internal int[] Tokens(string text) => TokensOf(_model, _tokenizer, text);

    internal static int[] TokensOf(EmbeddingModelDefinition model, Tokenizer tokenizer, string text)
    {
        var maximum = EmbeddingModelDefinition.MaximumTokens;
        if (model.Tokenizer == EmbeddingTokenizerKind.SentencePiece)
        {
            var ids = tokenizer.EncodeToIds(text).ToArray();
            var sentencePiece = (SentencePieceTokenizer)tokenizer;
            // An empty text is its start and its end, as the tokenizer of Hugging Face gives it.
            if (ids.Length == 0) return [sentencePiece.BeginningOfSentenceId, sentencePiece.EndOfSentenceId];
            if (ids.Length <= maximum) return ids;
            var cut = ids[..maximum];
            cut[^1] = sentencePiece.EndOfSentenceId;
            return cut;
        }

        var body = tokenizer.EncodeToIds(text, maximum - 2, out _, out _).ToArray();
        return [model.StartToken, .. body, model.EndToken];
    }

    public float[][] Embed(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return [];
        var tokens = texts.Select(Tokens).ToArray();
        int count = tokens.Length, length = tokens.Max(static ids => ids.Length);
        var ids = new DenseTensor<long>([count, length]);
        var mask = new DenseTensor<long>([count, length]);
        for (var row = 0; row < count; row++)
        {
            var text = tokens[row];
            for (var column = 0; column < length; column++)
            {
                var real = column < text.Length;
                ids[row, column] = real ? text[column] : _model.PadToken;
                mask[row, column] = real ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask) };
        foreach (var name in _extraInputs) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>([0, 512])));
        using var results = _session.Run(inputs);
        var vectors = new float[count][];
        if (_model.Pooling == EmbeddingPooling.SentenceEmbedding)
        {
            var output = results.First(static result => result.Name == "sentence_embedding").AsTensor<float>();
            for (var row = 0; row < count; row++)
            {
                var vector = new float[Dimensions];
                for (var dimension = 0; dimension < Dimensions; dimension++) vector[dimension] = output[row, dimension];
                vectors[row] = Normalize(vector);
            }
        }
        else
        {
            var output = results.First(static result => result.Name == "last_hidden_state").AsTensor<float>();
            for (var row = 0; row < count; row++)
            {
                var vector = new float[Dimensions];
                for (var dimension = 0; dimension < Dimensions; dimension++) vector[dimension] = output[row, 0, dimension];
                vectors[row] = Normalize(vector);
            }
        }

        return vectors;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        if (sum <= 0) return vector;
        var scale = (float)(1 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++) vector[i] *= scale;
        return vector;
    }

    /// <summary>Reads the tokenizer of a model.</summary>
    /// <exception cref="InvalidOperationException">The file cannot be read as the tokenizer of the model.</exception>
    internal static Tokenizer CreateTokenizer(EmbeddingModelDefinition model, string directory)
    {
        var path = Path.Combine(directory, model.TokenizerFile);
        try
        {
            if (model.Tokenizer == EmbeddingTokenizerKind.SentencePiece)
            {
                using var stream = File.OpenRead(path);
                return LlamaTokenizer.Create(stream, addBeginOfSentence: true, addEndOfSentence: true);
            }

            // A byte-level BPE: its vocabulary, its merges and the pattern of its pre-tokenizer, read from tokenizer.json.
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var bpe = root.GetProperty("model");
            var vocabulary = bpe.GetProperty("vocab").EnumerateObject().Select(static entry => new KeyValuePair<string, int>(entry.Name, entry.Value.GetInt32())).ToList();
            var merges = bpe.GetProperty("merges").EnumerateArray()
                .Select(static merge => merge.ValueKind == JsonValueKind.Array ? merge[0].GetString() + " " + merge[1].GetString() : merge.GetString()!).ToList();
            var pattern = root.GetProperty("pre_tokenizer").GetProperty("pretokenizers")[0].GetProperty("pattern").GetProperty("Regex").GetString()
                ?? throw new InvalidOperationException("The tokenizer has no pattern.");
            return BpeTokenizer.Create(new BpeOptions(vocabulary)
            {
                Merges = merges, ByteLevel = true,
                PreTokenizer = new RegexPreTokenizer(new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant), null),
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidDataException or FormatException)
        {
            throw new InvalidOperationException($"The tokenizer of {model.Name} cannot be read: {exception.Message}", exception);
        }
    }

    public void Dispose() => _session.Dispose();
}
