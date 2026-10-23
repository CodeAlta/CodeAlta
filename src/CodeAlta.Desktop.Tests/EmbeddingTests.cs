using System.IO.Pipes;
using System.Text.Json;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Tests;

/// <summary>The models of search by meaning, the worker they run in, and the client of the host.</summary>
[TestClass]
public sealed class EmbeddingTests
{
    [TestMethod]
    public void EveryPreset_IsPinned_AndHasItsGraphAndItsTokenizer()
    {
        Assert.AreEqual(EmbeddingModelCatalog.DefaultId, EmbeddingModelCatalog.Presets[0].Id);
        foreach (var model in EmbeddingModelCatalog.Presets)
        {
            Assert.IsTrue(model.Files.Any(file => file.Name == model.ModelFile), model.Id);
            Assert.IsTrue(model.Files.Any(file => file.Name == model.TokenizerFile), model.Id);
            foreach (var file in model.Files)
            {
                Assert.IsGreaterThan(0L, file.Size, file.Name);
                StringAssert.Matches(file.Sha256, new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$"), file.Name);
                StringAssert.Matches(file.Commit, new System.Text.RegularExpressions.Regex("^[0-9a-f]{40}$"), file.Name);
                StringAssert.StartsWith(file.Address.AbsoluteUri, $"https://huggingface.co/{file.Repository}/resolve/{file.Commit}/");
            }

            Assert.IsTrue(model.FolderName.StartsWith(model.Id + "-", StringComparison.Ordinal));
            Assert.IsTrue(model.Dimensions is 384 or 768);
        }

        var gemma = EmbeddingModelCatalog.Find("EMBEDDINGGEMMA-2")!;
        Assert.AreEqual("task: search result | query: dark theme", gemma.Query("dark theme", code: false));
        Assert.AreEqual("task: code retrieval | query: read a file", gemma.Query("read a file", code: true));
        Assert.AreEqual("title: none | text: body", gemma.Document(null, "body"));
        Assert.AreEqual("title: Release notes · CodeAlta | text: body", gemma.Document("Release notes\n· CodeAlta".Replace("\n", " ", StringComparison.Ordinal), "body"));
        Assert.AreEqual("body", EmbeddingModelCatalog.Find("granite-97m-multilingual-r2")!.Document("title", "body"));
        Assert.IsNull(EmbeddingModelCatalog.Find("nothing"));
    }

    [TestMethod]
    public void Vectors_CrossTheFrames_AsTheyWereWritten()
    {
        float[][] vectors = [[0.5f, -0.25f, 1f], [0f, 0f, -1f]];
        var read = EmbeddingProtocol.ReadVectors(EmbeddingProtocol.WriteVectors(vectors, 3));
        Assert.HasCount(2, read);
        CollectionAssert.AreEqual(vectors[0], read[0]);
        CollectionAssert.AreEqual(vectors[1], read[1]);
        Assert.ThrowsExactly<InvalidDataException>(() => EmbeddingProtocol.ReadVectors(new byte[5]));
        Assert.ThrowsExactly<InvalidDataException>(() => EmbeddingProtocol.WriteVectors([[1f]], 3));
    }

    [TestMethod]
    public async Task TheClient_LoadsOnce_AnswersQueriesFirst_AndStartsAnotherWorkerAfterADeath()
    {
        var engines = new FakeEngines();
        var workers = new List<FakeEmbeddingWorker>();
        await using var client = new EmbeddingClient(() =>
        {
            var worker = new FakeEmbeddingWorker(engines);
            lock (workers) workers.Add(worker);
            return worker;
        }, new EmbeddingWorkerLoad("granite-97m-multilingual-r2", "/models/granite", "/runtime/libonnxruntime.dylib", 2));

        var first = await client.EmbedDocumentsAsync(["alpha", "beta"], default);
        Assert.HasCount(2, first);
        Assert.AreEqual(4, client.Dimensions);
        Assert.AreEqual(1, engines.Loads);

        // While a batch runs, a query given after the batches that wait goes before them.
        engines.Gate.Reset();
        var running = client.EmbedDocumentsAsync(["slow"], default);
        await engines.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = client.EmbedDocumentsAsync(["later"], default);
        var query = client.EmbedQueryAsync("query", default);
        engines.Gate.Set();
        await Task.WhenAll(running, waiting, query);
        var order = engines.Texts.ToList();
        Assert.IsLessThan(order.IndexOf("later"), order.IndexOf("query"), "the query went first");
        Assert.AreEqual(1, engines.Loads, "the model is loaded once");

        // An engine that refuses a text fails that request, and the worker goes on.
        var refused = await Assert.ThrowsExactlyAsync<EmbeddingException>(() => client.EmbedDocumentsAsync(["refuse"], default));
        Assert.AreEqual("engine_failed", refused.Code);
        Assert.HasCount(1, await client.EmbedDocumentsAsync(["fine"], default));

        // A worker that dies fails what it had; the next request starts another one.
        engines.Gate.Reset();
        var lost = client.EmbedDocumentsAsync(["lost"], default);
        await engines.Started.WaitAsync(TimeSpan.FromSeconds(10));
        workers[^1].Kill();
        engines.Gate.Set();
        Assert.AreEqual("worker_died", (await Assert.ThrowsExactlyAsync<EmbeddingException>(() => lost)).Code);
        Assert.HasCount(4, await client.EmbedQueryAsync("again", default));
        Assert.HasCount(2, workers);
        Assert.AreEqual(2, engines.Loads);
    }

    [TestMethod]
    public async Task TheClient_EndsAnIdleWorker_AndAPausedIndexingWaits()
    {
        var engines = new FakeEngines();
        var workers = new List<FakeEmbeddingWorker>();
        await using var client = new EmbeddingClient(() => { var worker = new FakeEmbeddingWorker(engines); workers.Add(worker); return worker; },
            new EmbeddingWorkerLoad("granite-97m-multilingual-r2", "/models", "/runtime", 1), idle: TimeSpan.FromMilliseconds(200));
        await client.EmbedQueryAsync("one", default);
        Assert.IsTrue(client.Running);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (client.Running) await Task.Delay(20, timeout.Token);
        }

        Assert.IsTrue(workers[0].Ended, "an idle worker is asked to exit");

        client.Paused = true;
        var batch = client.EmbedDocumentsAsync(["paused"], default);
        await client.EmbedQueryAsync("still answered", default);
        await Task.Delay(100);
        Assert.IsFalse(batch.IsCompleted, "a paused indexing waits");
        client.Paused = false;
        Assert.HasCount(1, await batch.WaitAsync(TimeSpan.FromSeconds(10)));

        using var canceled = new CancellationTokenSource();
        client.Paused = true;
        var given = client.EmbedDocumentsAsync(["given up"], canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => given);
    }

    [TestMethod]
    public async Task TheClient_RunsAsManyWorkersAsAsked_AndFollowsAChangeDuringTheWork()
    {
        var engines = new FakeEngines();
        var workers = new List<FakeEmbeddingWorker>();
        await using var client = new EmbeddingClient(() =>
        {
            var worker = new FakeEmbeddingWorker(engines);
            lock (workers) workers.Add(worker);
            return worker;
        }, new EmbeddingWorkerLoad("granite-97m-multilingual-r2", "/models", "/runtime", 4), extraIdle: TimeSpan.FromMilliseconds(300));
        client.SetParallelism(2, 4);

        // Two batches at once, each in a worker of its own.
        engines.Gate.Reset();
        var first = client.EmbedDocumentsAsync(["one"], default);
        var second = client.EmbedDocumentsAsync(["two"], default);
        var third = client.EmbedDocumentsAsync(["three"], default);
        await engines.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await engines.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, client.Workers);
        Assert.IsFalse(third.IsCompleted, "the third batch waits for a free worker");
        engines.Gate.Set();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, engines.MostAtOnce);

        // The second worker ends soon after the work; the first stays for the queries.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (client.Workers > 1) await Task.Delay(20, timeout.Token);
        }

        Assert.IsTrue(client.Running);

        // Fewer at once with more threads: the worker loaded with other threads starts again with the next request.
        client.SetParallelism(1, 6);
        await client.EmbedDocumentsAsync(["after"], default);
        Assert.AreEqual(1, client.Workers);
        CollectionAssert.AreEqual(new[] { 4, 4, 6 }, engines.LoadedThreads.ToArray());
        Assert.AreEqual((1, 6), (client.Parallelism, client.Load.Threads));
    }

    [TestMethod]
    public void TheThreadsOfAWorker_ShareTwoThirdsOfTheCores_AtMostFourEach()
    {
        Assert.AreEqual(4, EmbeddingClient.ThreadsPerWorker(1, 14));
        Assert.AreEqual(4, EmbeddingClient.ThreadsPerWorker(2, 14));
        Assert.AreEqual(3, EmbeddingClient.ThreadsPerWorker(3, 14));
        Assert.AreEqual(2, EmbeddingClient.ThreadsPerWorker(4, 14));
        Assert.AreEqual(1, EmbeddingClient.ThreadsPerWorker(7, 14));
        Assert.AreEqual(1, EmbeddingClient.ThreadsPerWorker(1, 1));
        Assert.AreEqual(2, EmbeddingClient.ThreadsPerWorker(1, 4));
    }

    [TestMethod]
    public void TheWorker_SaysWhenAModelDoesNotLoad()
    {
        using var host = new FakeEmbeddingWorker(new FakeEngines());
        WorkerFrames.Write(host.Input, EmbeddingFrameKind.Embed, 7, JsonSerializer.SerializeToUtf8Bytes(new EmbeddingRequest(["x"]), EmbeddingJsonContext.Default.EmbeddingRequest));
        var refused = WorkerFrames.Read(host.Output)!.Value;
        Assert.AreEqual((EmbeddingFrameKind.Error, 7), (refused.Kind, refused.Correlation));
        WorkerFrames.Write(host.Input, EmbeddingFrameKind.Load, 1, JsonSerializer.SerializeToUtf8Bytes(new EmbeddingWorkerLoad("broken", "/models", "/runtime", 1), EmbeddingJsonContext.Default.EmbeddingWorkerLoad));
        var failed = WorkerFrames.Read(host.Output)!.Value;
        Assert.AreEqual(EmbeddingFrameKind.Error, failed.Kind);
        StringAssert.Contains(failed.Text, "did not load");
        WorkerFrames.Write(host.Input, EmbeddingFrameKind.Exit, 0, []);
        Assert.IsNull(WorkerFrames.Read(host.Output));
    }

    [TestMethod]
    public void TheManagedBinding_IsNotNewerThanTheNativeRuntime()
    {
        // sherpa-onnx 2.0.0 ships the ONNX Runtime 1.28.2, as 1.13.8 did: a newer binding reads past the end of its function table.
        var version = typeof(Microsoft.ML.OnnxRuntime.OrtEnv).Assembly.GetName().Version!;
        Assert.IsTrue(version.Major == 1 && version.Minor <= 28, version.ToString());
    }

    [TestMethod]
    public async Task TheWorkerProcess_EmbedsWithARealModel()
    {
        // Opt-in, as below: the executable of the build is started with --embedding-worker and loads Granite.
        var root = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_MODEL_ROOT");
        var runtime = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_ONNXRUNTIME");
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!);
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CodeAlta", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "alta.exe" : "alta"));
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime) || !File.Exists(executable))
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_TEST_MODEL_ROOT and CODEALTA_SEARCH_TEST_ONNXRUNTIME, and build the application, to run the worker.");
            return;
        }

        var model = EmbeddingModelCatalog.Find("granite-97m-multilingual-r2")!;
        await using var client = new EmbeddingClient(() => WorkerProcess.Start(executable, EmbeddingWorker.Option, Path.GetTempPath(), null),
            new EmbeddingWorkerLoad(model.Id, Path.Combine(root, "granite"), runtime, 2));
        var documents = await client.EmbedDocumentsAsync([model.Document(null, "A microphone button dictates into the prompt."), model.Document(null, "The release notes are written.")], default);
        var query = await client.EmbedQueryAsync(model.Query("voice input", code: false), default);
        Assert.AreEqual(model.Dimensions, client.Dimensions);
        Assert.IsGreaterThan(documents[1].Zip(query, static (left, right) => left * right).Sum(), documents[0].Zip(query, static (left, right) => left * right).Sum());
        Assert.IsTrue(client.Running);
    }

    [TestMethod]
    public void TheRealModels_GiveTheTokensOfHuggingFace_AndRankByMeaning()
    {
        // Opt-in: a folder with eg2/ and granite/ (the files of the presets) and ref.json (the tokens of Hugging Face),
        // and the ONNX Runtime library of sherpa-onnx.
        var root = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_MODEL_ROOT");
        var runtime = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_ONNXRUNTIME");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime))
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_TEST_MODEL_ROOT and CODEALTA_SEARCH_TEST_ONNXRUNTIME to run the models.");
            return;
        }

        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "..", "ref.json")));
        var texts = reference.RootElement.GetProperty("texts").EnumerateArray().Select(static text => text.GetString()!).ToArray();
        var factory = new OnnxEmbeddingEngineFactory();
        foreach (var (id, folder) in new[] { ("embeddinggemma-2", "eg2"), ("granite-97m-multilingual-r2", "granite") })
        {
            var model = EmbeddingModelCatalog.Find(id)!;
            var tokenizer = OnnxEmbeddingEngine.CreateTokenizer(model, Path.Combine(root, folder));
            var expected = reference.RootElement.GetProperty(folder).EnumerateArray().Select(static ids => ids.EnumerateArray().Select(static value => value.GetInt32()).ToArray()).ToArray();
            for (var i = 0; i < texts.Length; i++)
                CollectionAssert.AreEqual(expected[i], OnnxEmbeddingEngine.TokensOf(model, tokenizer, texts[i]), $"{id} #{i}");

            using var engine = factory.Load(new EmbeddingWorkerLoad(id, Path.Combine(root, folder), runtime, 4));
            string[] documents =
            [
                model.Document(null, "We fixed the scroll jump of the timeline by keeping the anchor message when new content arrives."),
                model.Document(null, "Le cache SQLite des sessions garde une empreinte de la première ligne du journal pour détecter une réécriture."),
                model.Document(null, "Added a microphone button that dictates into the prompt with a local speech model."),
            ];
            var vectors = engine.Embed(documents);
            Assert.HasCount(3, vectors);
            Assert.AreEqual(model.Dimensions, vectors[0].Length);
            Assert.AreEqual(1, Math.Sqrt(vectors[0].Sum(static value => value * value)), 1e-3);
            var query = engine.Embed([model.Query("voice input", code: false)])[0];
            var scores = vectors.Select(vector => vector.Zip(query, static (left, right) => left * right).Sum()).ToArray();
            Assert.AreEqual(2, Array.IndexOf(scores, scores.Max()), id);
            // A text longer than the model takes is cut, not refused.
            Assert.HasCount(1, engine.Embed([string.Join(' ', Enumerable.Repeat("journal", 2000))]));
        }
    }
}
