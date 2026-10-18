using System.Diagnostics;
using CodeAlta.Desktop.Search.Embedding;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The speed and the memory of the indexing by meaning with a real model, by the threads of a run, the runs at once
/// and the batches. Opt-in: <c>CODEALTA_SEARCH_BENCH_MODEL</c> names a preset, <c>CODEALTA_SEARCH_BENCH_MODEL_DIR</c>
/// its folder (such as <c>~/.alta/cache/search/models/embeddinggemma-2-…</c>) and <c>CODEALTA_SEARCH_TEST_ONNXRUNTIME</c>
/// the library. <c>CODEALTA_SEARCH_BENCH_CONFIGS</c> lists the runs (<c>2x4engines</c>: two engines of four threads
/// each; <c>2x4shared</c>: two runs at once on one engine), <c>CODEALTA_SEARCH_BENCH_BATCH</c> the texts of a batch
/// and <c>CODEALTA_SEARCH_BENCH_SORT=1</c> sorts them by length. Run one configuration per process for its memory.
/// </summary>
[TestClass]
public sealed class EmbeddingThroughputHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Throughput_ByThreadsAndRunsAtOnce()
    {
        var id = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_MODEL") ?? "embeddinggemma-2";
        var directory = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_MODEL_DIR");
        var runtime = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_ONNXRUNTIME");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime))
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_BENCH_MODEL_DIR and CODEALTA_SEARCH_TEST_ONNXRUNTIME to run this harness.");
            return;
        }

        var model = EmbeddingModelCatalog.Find(id)!;
        var texts = Texts(model, 192);
        var factory = new OnnxEmbeddingEngineFactory();
        var configurations = (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_CONFIGS") ?? "1x4,1x8,2x4shared,3x4shared,2x2shared,4x2shared,2x4engines,3x3engines")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var configuration in configurations)
        {
            var runs = int.Parse(configuration[..configuration.IndexOf('x')], System.Globalization.CultureInfo.InvariantCulture);
            var threads = int.Parse(new string([.. configuration[(configuration.IndexOf('x') + 1)..].TakeWhile(char.IsDigit)]), System.Globalization.CultureInfo.InvariantCulture);
            var shared = !configuration.EndsWith("engines", StringComparison.Ordinal);
            var before = Process.GetCurrentProcess().WorkingSet64;
            var engines = Enumerable.Range(0, shared ? 1 : runs).Select(_ => factory.Load(new EmbeddingWorkerLoad(id, directory, runtime, threads))).ToArray();
            try
            {
                // A first batch warms each engine.
                foreach (var engine in engines) engine.Embed([.. texts.Take(4)]);
                var size = int.TryParse(Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_BATCH"), out var asked) ? asked : 16;
                var ordered = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_SORT") == "1" ? texts.OrderBy(static text => text.Length).ToArray() : texts;
                var batches = ordered.Chunk(size).ToArray();
                var next = -1;
                var watch = Stopwatch.StartNew();
                var workers = Enumerable.Range(0, runs).Select(slot => Task.Factory.StartNew(() =>
                {
                    var engine = engines[shared ? 0 : slot];
                    int index;
                    while ((index = Interlocked.Increment(ref next)) < batches.Length) engine.Embed(batches[index]);
                }, TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(workers);
                var seconds = watch.Elapsed.TotalSeconds;
                Process.GetCurrentProcess().Refresh();
                var process = Process.GetCurrentProcess();
                TestContext.WriteLine($"{configuration,-12} batch {Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_BATCH") ?? "16",-3} sort {Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_SORT") ?? "0"} {texts.Length / seconds,6:F1} chunks/s  memory {process.WorkingSet64 / (1024 * 1024)} MB (before {before / (1024 * 1024)} MB)");
            }
            finally
            {
                foreach (var engine in engines) engine.Dispose();
                GC.Collect();
            }
        }
    }

    /// <summary>
    /// The backends of the ONNX Runtime on one model: <c>CODEALTA_SEARCH_BENCH_EPS</c> lists them (<c>cpu</c>,
    /// <c>coreml-all</c>, <c>coreml-gpu</c>, <c>coreml-ane</c>, <c>coreml-cpu</c>), <c>CODEALTA_SEARCH_BENCH_MODEL_FILE</c>
    /// names the graph (the preset's by default, or its float16 one). Each line gives the load, the speed of batches of
    /// 8 sorted texts, a query, and the cosine of the vectors to those of the preset's graph on the CPU.
    /// </summary>
    [TestMethod]
    public void Backends_ByExecutionProvider()
    {
        var id = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_MODEL") ?? "embeddinggemma-2";
        var directory = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_MODEL_DIR");
        var runtime = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_ONNXRUNTIME");
        var providers = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_EPS");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime) || string.IsNullOrWhiteSpace(providers))
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_BENCH_MODEL_DIR, CODEALTA_SEARCH_TEST_ONNXRUNTIME and CODEALTA_SEARCH_BENCH_EPS to run this harness.");
            return;
        }

        var model = EmbeddingModelCatalog.Find(id)!;
        var file = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_MODEL_FILE");
        var threads = int.TryParse(Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_THREADS"), out var asked) ? asked : 4;
        var texts = Texts(model, 96).OrderBy(static text => text.Length).ToArray();
        var batches = texts.Chunk(8).ToArray();
        float[][] reference;
        // The preset as shipped, on the CPU: the vectors the others are compared to. Loading it also sets the library.
        using (var shipped = new OnnxEmbeddingEngineFactory().Load(new EmbeddingWorkerLoad(id, directory, runtime, threads)))
            reference = [.. batches.SelectMany(batch => shipped.Embed(batch))];

        foreach (var provider in providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var watch = Stopwatch.StartNew();
            OnnxEmbeddingEngine engine;
            try
            {
                engine = new OnnxEmbeddingEngine(model, directory, threads, file, options => Configure(options, provider, Path.Combine(Path.GetTempPath(), "codealta-coreml-cache")));
            }
            catch (Exception exception) when (exception is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or InvalidOperationException)
            {
                TestContext.WriteLine($"{provider,-11} {file ?? model.ModelFile}: does not load: {exception.Message.ReplaceLineEndings(" ")[..Math.Min(900, exception.Message.Length)]}");
                continue;
            }

            using (engine)
            {
                var load = watch.Elapsed.TotalSeconds;
                watch.Restart();
                engine.Embed([.. texts.Take(8)]);
                var first = watch.Elapsed.TotalSeconds;
                watch.Restart();
                var vectors = batches.SelectMany(batch => engine.Embed(batch)).ToArray();
                var rate = texts.Length / watch.Elapsed.TotalSeconds;
                watch.Restart();
                for (var i = 0; i < 10; i++) engine.Embed([model.Query("where did we fix the scroll jump", code: false)]);
                var query = watch.Elapsed.TotalMilliseconds / 10;
                var cosines = vectors.Zip(reference, static (left, right) => left.Zip(right, static (a, b) => a * b).Sum()).ToArray();
                Process.GetCurrentProcess().Refresh();
                TestContext.WriteLine($"{provider,-11} {file ?? model.ModelFile,-22} load {load,5:F1} s  first batch {first,5:F1} s  {rate,6:F1} chunks/s  query {query,6:F1} ms  cosine {cosines.Average():F4} (min {cosines.Min():F4})  memory {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");
            }
        }
    }

    private static void Configure(Microsoft.ML.OnnxRuntime.SessionOptions options, string provider, string cache)
    {
        if (provider == "cpu") return;
        var units = provider switch { "coreml-gpu" => "CPUAndGPU", "coreml-ane" => "CPUAndNeuralEngine", "coreml-cpu" => "CPUOnly", _ => "ALL" };
        var settings = new Dictionary<string, string> { ["ModelFormat"] = "MLProgram", ["MLComputeUnits"] = units };
        // More options, as key=value;key=value (such as ModelFormat=NeuralNetwork;RequireStaticInputShapes=1).
        foreach (var pair in (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH_COREML") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == "cache") { Directory.CreateDirectory(cache); settings["ModelCacheDirectory"] = cache; }
            else settings[parts[0]] = parts[1];
        }

        options.AppendExecutionProvider("CoreML", settings);
    }

    // Texts like the chunks of a conversation: about 900 characters of prose and code.
    private static string[] Texts(EmbeddingModelDefinition model, int count)
    {
        string[] sentences =
        [
            "We fixed the scroll jump of the timeline by keeping the anchor message when new content arrives.",
            "The journal of a session is read from its last offset, and a fingerprint of the first line tells a rewrite.",
            "`SearchIndexStore.ApplyAsync` writes the items of a source in one transaction and removes what is gone.",
            "Le cache des sessions garde une empreinte de la première ligne du journal pour détecter une réécriture.",
            "Run `dotnet test -c Release` from src, then npm test in the frontend, and lunet build in the site.",
            "The prompt grows upwards over the timeline, and only its editor gives room back, down to two lines.",
            "A worker process loads the model so that a failure of the native runtime never takes the application down.",
            "private static float[] Normalize(float[] vector) { double sum = 0; foreach (var value in vector) sum += value * value; }",
        ];
        var random = new Random(118);
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var text = new System.Text.StringBuilder();
            while (text.Length < 700 + random.Next(400)) text.Append(sentences[random.Next(sentences.Length)]).Append(' ');
            return model.Document($"Session {i % 20}", text.ToString());
        })];
    }
}
