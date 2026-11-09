using System.Buffers.Binary;
using System.Text;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.Tests.ModelServers;

[TestClass]
public sealed class GgufHeaderTests
{
    [TestMethod]
    public void AHeaderCutInItsVocabulary_StillGivesTheShape()
    {
        // Qwen3 8B: 36 layers, 8 KV heads of 128, 40,960 tokens; its vocabulary of 151,936 tokens comes last.
        var header = Gguf(w =>
        {
            w.String("general.architecture", "qwen3");
            w.UInt32("qwen3.block_count", 36);
            w.UInt32("qwen3.context_length", 40960);
            w.UInt32("qwen3.embedding_length", 4096);
            w.UInt32("qwen3.attention.head_count", 32);
            w.UInt32("qwen3.attention.head_count_kv", 8);
            w.UInt32("qwen3.attention.key_length", 128);
            w.UInt32("qwen3.attention.value_length", 128);
            w.Float32("qwen3.rope.freq_base", 1_000_000f);
            w.Strings("tokenizer.ggml.tokens", Enumerable.Range(0, 151_936).Select(static i => "token" + i));
        });
        var cut = header.AsSpan(0, header.Length / 2);
        var metadata = GgufHeader.ReadMetadata(cut, out var complete)!;
        Assert.IsFalse(complete);
        Assert.AreEqual(1_000_000d, metadata["qwen3.rope.freq_base"]);
        var shape = GgufHeader.ShapeOf(metadata)!;
        Assert.AreEqual(("qwen3", 36, 40960, 151_936, true), (shape.Architecture, shape.Layers, shape.TrainedContext, shape.Vocabulary, shape.Known));
        Assert.AreEqual(36L * 8 * (128 + 128) * 2, shape.KvBytesPerToken, "144 KiB a token, in f16");
        Assert.AreEqual(0, shape.SlidingWindow);
        Assert.IsNull(GgufHeader.ReadMetadata("GGML"u8.ToArray().Concat(new byte[40]).ToArray(), out _), "not a GGUF");
    }

    [TestMethod]
    public void SlidingWindows_HybridLayers_AndLatentAttention_CountTheirOwnCache()
    {
        // Gemma 3: 5 layers of each 6 keep a window of 1,024 tokens.
        var gemma = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(Gguf(w =>
        {
            w.String("general.architecture", "gemma3");
            w.UInt32("gemma3.block_count", 12);
            w.UInt32("gemma3.context_length", 131072);
            w.UInt32("gemma3.embedding_length", 2560);
            w.UInt32("gemma3.attention.head_count", 8);
            w.UInt32("gemma3.attention.head_count_kv", 4);
            w.UInt32("gemma3.attention.key_length", 256);
            w.UInt32("gemma3.attention.sliding_window", 1024);
        }), out _)!)!;
        var perLayer = 4L * 512 * 2;
        Assert.AreEqual((2 * perLayer, 10 * perLayer, 1024), (gemma.KvBytesPerToken, gemma.SlidingKvBytesPerToken, gemma.SlidingWindow));
        Assert.AreEqual(2 * perLayer * 100_000 + 10 * perLayer * (1024 + 512), gemma.KvBytes(100_000), "the window does not grow with the context");

        // Qwen3-Next: one layer of 4 has attention; the others are recurrent.
        var next = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(Gguf(w =>
        {
            w.String("general.architecture", "qwen3next");
            w.UInt32("qwen3next.block_count", 48);
            w.UInt32("qwen3next.context_length", 262144);
            w.UInt32("qwen3next.attention.head_count", 16);
            w.UInt32("qwen3next.attention.head_count_kv", 2);
            w.UInt32("qwen3next.attention.key_length", 256);
            w.UInt32("qwen3next.full_attention_interval", 4);
        }), out _)!)!;
        Assert.AreEqual(12L * 2 * 512 * 2, next.KvBytesPerToken);

        // Nemotron-H: the layers without KV heads are recurrent.
        var hybrid = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(Gguf(w =>
        {
            w.String("general.architecture", "nemotron_h");
            w.UInt32("nemotron_h.block_count", 4);
            w.UInt32("nemotron_h.context_length", 8192);
            w.UInt32("nemotron_h.attention.head_count", 32);
            w.Int32s("nemotron_h.attention.head_count_kv", [0, 8, 0, 0]);
            w.UInt32("nemotron_h.attention.key_length", 128);
        }), out _)!)!;
        Assert.AreEqual(1L * 8 * 256 * 2, hybrid.KvBytesPerToken);

        // DeepSeek V3: a compressed cache of 512 + 64 per layer.
        var deepseek = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(Gguf(w =>
        {
            w.String("general.architecture", "deepseek2");
            w.UInt32("deepseek2.block_count", 61);
            w.UInt32("deepseek2.context_length", 163840);
            w.UInt32("deepseek2.attention.head_count", 128);
            w.UInt32("deepseek2.attention.head_count_kv", 128);
            w.UInt32("deepseek2.attention.kv_lora_rank", 512);
            w.UInt32("deepseek2.rope.dimension_count", 64);
        }), out _)!)!;
        Assert.AreEqual(61L * 576 * 2, deepseek.KvBytesPerToken);

        var unknown = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(Gguf(w =>
        {
            w.String("general.architecture", "newarch");
            w.UInt32("newarch.block_count", 2);
            w.UInt32("newarch.embedding_length", 1024);
            w.UInt32("newarch.attention.head_count", 8);
        }), out _)!)!;
        Assert.IsFalse(unknown.Known);
        Assert.AreEqual(2L * 8 * 256 * 2, unknown.KvBytesPerToken, "every head keeps every token: the safe side");
    }

    [TestMethod]
    public void TheEstimateFromTheHeader_SaysTheLargestContextThatFits()
    {
        var quant = new QuantFiles("Q4_K_M", [new CodeAlta.ModelServers.HuggingFace.HubFile("m-Q4_K_M.gguf", 5_000_000_000, null)], 5_000_000_000, 0, 4);
        var shape = new GgufShape("qwen3", 36, 40960, 36L * 8 * 256 * 2, 0, 0, 151_936, true);
        var gpu16 = new HardwareInventory("linux", "x64", null, 64L << 30, [new GpuInfo("0", "amd", "RX 7600 XT", 16L << 30, false, "gfx1102", null)],
            new MachineLibraries(null, null, null, false, true, null), []);
        var fit = FitEstimate.For(quant, gpu16, shape);
        Assert.AreEqual(("gpu", 40960), (fit.Fit, fit.Context), "the whole trained context fits in 16 GiB");
        StringAssert.Contains(fit.Message, "40,960 tokens of context");

        var gpu8 = gpu16 with { Gpus = [gpu16.Gpus[0] with { MemoryBytes = 8L << 30 }] };
        var tight = FitEstimate.For(quant, gpu8, shape);
        Assert.AreEqual("gpu", tight.Fit);
        Assert.IsTrue(tight.Context is > FitEstimate.UsefulContext and < 40960, $"a part of the context: {tight.Context}");
        Assert.IsNull(FitEstimate.For(quant, gpu8).Context, "from its size alone, no context is told");
        StringAssert.Contains(FitEstimate.For(quant, gpu16, shape with { Known = false }).Message, "rough");
    }

    [TestMethod]
    public void TheMeasureOfLlamaFitParams_IsRead()
    {
        var build = new InstalledBuild("b12110", 12110, "cuda", "13.4", "f", 1, DateTimeOffset.UnixEpoch, [], null, true, false, false, "ggml-org");
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var fit = LlamaBuilds.ParseFit(build, "-c 40960 -ngl -1\n", "CUDA0 1050 4480 352 \nHost 243 0 56\n", at);
        Assert.AreEqual(("cuda", "b12110", 40960, -1, "-c 40960 -ngl -1"), (fit.Backend, fit.Build, fit.Context, fit.GpuLayers, fit.Arguments));
        Assert.HasCount(2, fit.Devices);
        Assert.AreEqual(new DeviceMemory("CUDA0", 1050L << 20, 4480L << 20, 352L << 20), fit.Devices[0]);
        var moe = LlamaBuilds.ParseFit(build, "log line\n-c 65536 -ngl 99 -ncmoe 12", "", at);
        Assert.AreEqual((65536, 99), (moe.Context, moe.GpuLayers));
    }

    [TestMethod]
    public void ARealHeader_IsRead_WhenTheModelIsOnThisMachine()
    {
        // The model the live tests use, when the Hugging Face cache of this machine has it.
        var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub", "models--unsloth--Qwen3-1.7B-GGUF", "snapshots");
        var file = Directory.Exists(snapshots) ? Directory.EnumerateFiles(snapshots, "*.gguf", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (file is null)
        {
            Assert.Inconclusive("unsloth/Qwen3-1.7B-GGUF is not in the Hugging Face cache of this machine.");
            return;
        }

        var head = new byte[4 << 20];
        using (var stream = File.OpenRead(file)) stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var shape = GgufHeader.ShapeOf(GgufHeader.ReadMetadata(head, out _)!)!;
        Assert.AreEqual(("qwen3", 28, 40960), (shape.Architecture, shape.Layers, shape.TrainedContext));
        Assert.AreEqual(28L * 8 * 256 * 2, shape.KvBytesPerToken);
    }

    private static byte[] Gguf(Action<Writer> keys)
    {
        var writer = new Writer();
        keys(writer);
        var header = new byte[24];
        "GGUF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)writer.Count);
        return [.. header, .. writer.Bytes.ToArray()];
    }

    private sealed class Writer
    {
        public MemoryStream Bytes { get; } = new();

        public int Count { get; private set; }

        public void String(string key, string value) => Key(key, 8, () => Text(value));

        public void UInt32(string key, uint value) => Key(key, 4, () => Raw(BitConverter.GetBytes(value)));

        public void Float32(string key, float value) => Key(key, 6, () => Raw(BitConverter.GetBytes(value)));

        public void Int32s(string key, int[] values) => Key(key, 9, () =>
        {
            Raw(BitConverter.GetBytes(5u));
            Raw(BitConverter.GetBytes((ulong)values.Length));
            foreach (var value in values) Raw(BitConverter.GetBytes(value));
        });

        public void Strings(string key, IEnumerable<string> values)
        {
            var list = values.ToList();
            Key(key, 9, () =>
            {
                Raw(BitConverter.GetBytes(8u));
                Raw(BitConverter.GetBytes((ulong)list.Count));
                foreach (var value in list) Text(value);
            });
        }

        private void Key(string key, uint type, Action value)
        {
            Text(key);
            Raw(BitConverter.GetBytes(type));
            value();
            Count++;
        }

        private void Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Raw(BitConverter.GetBytes((ulong)bytes.Length));
            Raw(bytes);
        }

        private void Raw(byte[] bytes) => Bytes.Write(bytes);
    }
}
