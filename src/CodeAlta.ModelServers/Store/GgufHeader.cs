using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;

namespace CodeAlta.ModelServers.Store;

/// <summary>
/// What a model needs in memory besides its weights, read from the header of its GGUF file: the bytes of its KV cache for
/// each token of context, on the layers that keep every token and on those that keep a sliding window only, and the size
/// of its vocabulary (its logits). Hybrid models (recurrent layers beside attention layers) count their attention layers
/// only; DeepSeek's latent attention counts its compressed cache.
/// </summary>
/// <param name="Architecture">The architecture (<c>qwen3</c>, <c>gemma3</c>...).</param>
/// <param name="Layers">The layers.</param>
/// <param name="TrainedContext">The context it was trained for, in tokens.</param>
/// <param name="KvBytesPerToken">The bytes of KV cache per token on the layers that keep every token (f16).</param>
/// <param name="SlidingKvBytesPerToken">The bytes per token on the layers that keep a sliding window only.</param>
/// <param name="SlidingWindow">The tokens of that window; 0 for none.</param>
/// <param name="Vocabulary">The tokens of its vocabulary; 0 when not read.</param>
/// <param name="Known">Whether CodeAlta knows the layout of its architecture; otherwise every layer is counted as keeping every token.</param>
public sealed record GgufShape(string Architecture, int Layers, int TrainedContext, long KvBytesPerToken, long SlidingKvBytesPerToken, int SlidingWindow, int Vocabulary, bool Known)
{
    /// <summary>The bytes of KV cache for a context.</summary>
    /// <param name="context">The tokens of context.</param>
    /// <returns>The bytes.</returns>
    public long KvBytes(int context)
        => KvBytesPerToken * context + SlidingKvBytesPerToken * (SlidingWindow > 0 ? Math.Min(context, SlidingWindow + 512) : context);
}

/// <summary>
/// Reads the header of a GGUF file (version 2 or 3): its metadata, as far as the bytes given go, without its tensors. The
/// first megabytes of a file hold it; the vocabulary, which comes last, can be cut: what came before stays.
/// </summary>
public static class GgufHeader
{
    // The architectures whose layout is known: their attention layers, their sliding windows and their latent attention.
    private static readonly FrozenSet<string> KnownArchitectures = FrozenSet.Create(StringComparer.Ordinal,
    [
        "llama", "llama4", "mistral3", "qwen2", "qwen2moe", "qwen3", "qwen3moe", "qwen3next", "qwen35", "qwen35moe", "qwen3vl", "qwen3vlmoe", "gemma", "gemma2", "gemma3",
        "gemma3n", "gemma4", "glm4", "glm4moe", "deepseek2", "gpt-oss", "phi3", "command-r", "cohere2", "granite", "granitemoe", "granitehybrid", "nemotron_h",
        "olmo2", "exaone4", "ernie4_5", "ernie4_5-moe", "minimax-m2", "seed_oss", "apertus", "lfm2", "smollm3", "hunyuan-moe", "bailingmoe2",
    ]);

    // The pattern of the sliding window when the file does not give it: of each n layers, the last keeps every token.
    private static readonly FrozenDictionary<string, int> SlidingPatterns = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["gemma2"] = 2, ["gemma3"] = 6, ["gpt-oss"] = 2, ["cohere2"] = 4, ["exaone4"] = 4,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Reads the metadata of a GGUF header: scalars, short arrays of numbers, and the length of the other arrays.</summary>
    /// <param name="data">The first bytes of the file.</param>
    /// <param name="complete">Whether every key was read (false: the bytes ended first).</param>
    /// <returns>The keys and their values (<see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>,
    /// <see cref="long"/>[] for an array of numbers or booleans, <see cref="GgufArrayLength"/> for another array); null for a file that is no GGUF.</returns>
    public static IReadOnlyDictionary<string, object>? ReadMetadata(ReadOnlySpan<byte> data, out bool complete)
    {
        complete = false;
        if (data.Length < 24 || !data[..4].SequenceEqual("GGUF"u8)) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (version is < 2 or > 3) return null;
        var count = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var reader = new Reader(data, 24);
        for (ulong i = 0; i < count; i++)
        {
            if (!reader.TryString(out var key) || !reader.TryUInt32(out var type)) return result;
            var read = reader.TryValue(type, out var value);
            // The length of an array cut by the end of the bytes is known all the same.
            if (value is not null) result[key] = value;
            if (!read) return result;
        }

        complete = true;
        return result;
    }

    /// <summary>Gives the shape of a model from the metadata of its header.</summary>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The shape, or null when the header does not tell its layers.</returns>
    public static GgufShape? ShapeOf(IReadOnlyDictionary<string, object> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.TryGetValue("general.architecture", out var named) || named is not string arch) return null;
        long? Number(string key) => metadata.TryGetValue($"{arch}.{key}", out var value) ? value switch { long l => l, double d => (long)d, long[] { Length: > 0 } a => a.Max(), _ => null } : null;
        long[]? PerLayer(string key) => metadata.TryGetValue($"{arch}.{key}", out var value) ? value as long[] : null;
        if (Number("block_count") is not { } blocks || blocks is <= 0 or > 4096) return null;
        var layers = (int)blocks;
        var embedding = Number("embedding_length") ?? 0;
        var heads = PerLayer("attention.head_count") ?? Fill(layers, Number("attention.head_count") ?? 0);
        var kvHeads = PerLayer("attention.head_count_kv") ?? Fill(layers, Number("attention.head_count_kv") ?? -1);
        for (var i = 0; i < kvHeads.Length; i++) if (kvHeads[i] < 0) kvHeads[i] = i < heads.Length ? heads[i] : 0;
        var firstHeads = heads.FirstOrDefault(static value => value > 0);
        var keyLength = Number("attention.key_length") ?? (firstHeads > 0 ? embedding / firstHeads : 0);
        var valueLength = Number("attention.value_length") ?? keyLength;
        var latentRank = Number("attention.kv_lora_rank");
        var ropeDimensions = Number("rope.dimension_count") ?? 64;
        var window = (int)Math.Clamp(Number("attention.sliding_window") ?? 0, 0, int.MaxValue);
        var interval = Number("full_attention_interval");
        var known = KnownArchitectures.Contains(arch);

        // Which layers keep a sliding window: the file's pattern (a flag per layer, or n), else the architecture's.
        var sliding = new bool[layers];
        if (window > 0)
        {
            var flags = PerLayer("attention.sliding_window_pattern");
            var pattern = flags is null ? Number("attention.sliding_window_pattern") ?? (SlidingPatterns.TryGetValue(arch, out var p) ? p : 0) : 0;
            for (var i = 0; i < layers; i++) sliding[i] = flags is not null ? i < flags.Length && flags[i] != 0 : pattern > 1 && i % pattern < pattern - 1;
        }

        long full = 0, partial = 0;
        for (var i = 0; i < layers; i++)
        {
            // A recurrent layer of a hybrid model keeps a state of fixed size, not a cache per token.
            if (interval is > 0 && (i + 1) % interval != 0) continue;
            var kv = i < kvHeads.Length ? kvHeads[i] : kvHeads.LastOrDefault();
            if (kv <= 0 && latentRank is null) continue;
            var bytes = latentRank is { } rank ? (rank + ropeDimensions) * 2 : kv * (keyLength + valueLength) * 2;
            if (sliding[i]) partial += bytes;
            else full += bytes;
        }

        var vocabulary = metadata.TryGetValue("tokenizer.ggml.tokens", out var tokens) ? tokens switch { GgufArrayLength length => (int)length.Count, long[] values => values.Length, _ => 0 }
            : (int)(Number("vocab_size") ?? 0);
        return new GgufShape(arch, layers, (int)Math.Clamp(Number("context_length") ?? 0, 0, int.MaxValue), full, partial, sliding.Any(static value => value) ? window : 0, vocabulary, known);
    }

    private static long[] Fill(int count, long value)
    {
        var values = new long[count];
        Array.Fill(values, value);
        return values;
    }

    private ref struct Reader(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position = position;

        public bool TryUInt32(out uint value)
        {
            value = 0;
            if (_position + 4 > _data.Length) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += 4;
            return true;
        }

        public bool TryString(out string value)
        {
            value = "";
            if (!TrySkipString(out var start, out var length)) return false;
            value = Encoding.UTF8.GetString(_data.Slice(start, length));
            return true;
        }

        private bool TrySkipString(out int start, out int length)
        {
            start = 0;
            length = 0;
            if (_position + 8 > _data.Length) return false;
            var size = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            if (size > (ulong)(_data.Length - _position - 8)) return false;
            start = _position + 8;
            length = (int)size;
            _position = start + length;
            return true;
        }

        // A value: a scalar, a short array of numbers, or the length of another array (its items skipped). False when the bytes end.
        public bool TryValue(uint type, out object? value)
        {
            value = null;
            if (type == 8)
            {
                if (!TryString(out var text)) return false;
                value = text;
                return true;
            }

            if (type == 9)
            {
                if (!TryUInt32(out var itemType) || _position + 8 > _data.Length) return false;
                var count = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
                _position += 8;
                var width = Width(itemType);
                if (itemType == 8)
                {
                    // An array of strings (the vocabulary): its length counts; its items are skipped as far as the bytes go.
                    value = new GgufArrayLength(count);
                    for (ulong i = 0; i < count; i++)
                    {
                        if (!TrySkipString(out _, out _)) return false;
                    }

                    return true;
                }

                if (width == 0 || count > (ulong)(_data.Length - _position) / (ulong)width) return false;
                if (count <= 4096)
                {
                    var items = new long[count];
                    for (var i = 0; i < (int)count; i++) items[i] = ReadNumber(itemType, out _);
                    value = items;
                }
                else
                {
                    _position += (int)count * width;
                    value = new GgufArrayLength(count);
                }

                return true;
            }

            if (Width(type) is var size && (size == 0 || _position + size > _data.Length)) return false;
            var number = ReadNumber(type, out var real);
            value = type == 7 ? number != 0 : real is { } d ? d : number;
            return true;
        }

        private long ReadNumber(uint type, out double? real)
        {
            real = null;
            var span = _data[_position..];
            long result;
            switch (type)
            {
                case 0: result = span[0]; break;
                case 1: result = (sbyte)span[0]; break;
                case 2: result = BinaryPrimitives.ReadUInt16LittleEndian(span); break;
                case 3: result = BinaryPrimitives.ReadInt16LittleEndian(span); break;
                case 4: result = BinaryPrimitives.ReadUInt32LittleEndian(span); break;
                case 5: result = BinaryPrimitives.ReadInt32LittleEndian(span); break;
                case 6: real = BinaryPrimitives.ReadSingleLittleEndian(span); result = (long)real.Value; break;
                case 7: result = span[0] != 0 ? 1 : 0; break;
                case 10: result = (long)BinaryPrimitives.ReadUInt64LittleEndian(span); break;
                case 11: result = BinaryPrimitives.ReadInt64LittleEndian(span); break;
                case 12: real = BinaryPrimitives.ReadDoubleLittleEndian(span); result = (long)real.Value; break;
                default: result = 0; break;
            }

            _position += Width(type);
            return result;
        }

        private static int Width(uint type) => type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
    }
}

/// <summary>The length of an array of a GGUF header whose items are not kept (the vocabulary).</summary>
/// <param name="Count">Its items.</param>
public sealed record GgufArrayLength(ulong Count);
