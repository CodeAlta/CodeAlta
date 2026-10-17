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
