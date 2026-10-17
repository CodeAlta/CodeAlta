using CodeAlta.Catalog.Search;
using CodeAlta.Desktop.Search;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Desktop.Speech;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Tests;

/// <summary>Search by meaning in the window: it follows its settings, embeds the index in the background and answers queries.</summary>
[TestClass]
public sealed class DesktopSearchMeaningTests
{
    [TestMethod]
    public async Task TurnedOn_ItEmbedsTheIndex_AnswersByMeaning_PausesAndTurnsOff()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-meaning-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var (model, installer) = InstallModel(root);

            await using var store = new SearchIndexStore(Path.Combine(root, "data", "search.sqlite3"));
            var documents = new List<SearchDocument>
            {
                new("doc:a.md", SearchKind.Doc, "Timeline", "We fixed the scroll jump of the timeline by keeping the anchor message.", DateTimeOffset.UtcNow),
                new("doc:b.md", SearchKind.Doc, "Speech", "A microphone button dictates into the prompt with a local speech model.", DateTimeOffset.UtcNow),
                new("doc:c.md", SearchKind.Doc, "Journal", "The cache keeps a fingerprint of the first line of the journal to detect a rewrite.", DateTimeOffset.UtcNow),
            };
            var source = new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. documents]));
            var settings = new Settings(SearchSettings.Default with { Semantic = true, Model = model.Id });
            await using var indexer = new SearchIndexer(store, new NoJournals(), new Host(settings), () => [source], TimeProvider.System,
                new SearchIndexerOptions { StartDelay = TimeSpan.Zero, PassInterval = TimeSpan.FromHours(1), FreshnessBeforeQuery = TimeSpan.Zero });
            indexer.Start();
            indexer.SignalPass();
            await indexer.WaitIdleAsync(Timeout());

            var engines = new FakeEngines(model.Dimensions, words: true);
            var loads = new List<EmbeddingWorkerLoad>();
            var clients = new List<EmbeddingClient>();
            var meaning = new DesktopSearchMeaning(settings, indexer, installer, new StoreMeaningVectors(store, null), load =>
            {
                lock (loads) loads.Add(load);
                var client = new EmbeddingClient(() => new FakeEmbeddingWorker(engines), load);
                lock (clients) clients.Add(client);
                return client;
            }, null);
            await using var _ = meaning;
            var service = new SearchService(store, indexer, new Host(settings), null) { Meaning = meaning };
            meaning.Start();
            await UntilAsync(() => meaning.Status.State == "ready");
            Assert.AreEqual((3, 3), (meaning.Status.Embedded, meaning.Status.Total));
            Assert.AreEqual(model.Id, loads.Single().ModelId);
            Assert.AreEqual(installer.RuntimeLibrary(), loads.Single().OnnxRuntime);
            Assert.AreEqual(SearchMeaningState.Ready, meaning.State);
            // The documents were embedded with the prompt of the model.
            Assert.IsTrue(engines.Texts.Contains(model.Document("Speech", documents[1].Text)));

            // A query that shares no word with the text finds it by meaning.
            var hits = (await service.SearchAsync(new SearchRequest("microphone dictates", new SearchScope()) { Mode = SearchMode.Meaning })).Hits;
            Assert.AreEqual("doc:b.md", hits[0].Ref);
            Assert.AreEqual(SearchMatch.Meaning, hits[0].Match);

            // What the indexer writes is embedded next.
            documents.Add(new("doc:d.md", SearchKind.Doc, "Release", "The release notes are written for the new version.", DateTimeOffset.UtcNow));
            indexer.Signal("docs");
            await UntilAsync(() => meaning.Status is { State: "ready", Embedded: 4 });

            // Many texts: batches of a few, each of texts of about the same length, as many at once as the settings say.
            var cores = Environment.ProcessorCount;
            Assert.AreEqual(SearchSettings.Default.EffectiveParallelism(cores), clients.Single().Parallelism);
            Assert.AreEqual(EmbeddingClient.ThreadsPerWorker(clients.Single().Parallelism, cores), loads.Single().Threads);
            var random = new Random(5);
            for (var i = 0; i < 40; i++)
                documents.Add(new($"doc:n{i}.md", SearchKind.Doc, "Note", string.Join(' ', Enumerable.Repeat($"word{i}", 1 + random.Next(30))), DateTimeOffset.UtcNow));
            engines.Batches.Clear();
            indexer.Signal("docs");
            await UntilAsync(() => meaning.Status is { State: "ready", Embedded: 44 });
            var batches = engines.Batches.ToArray();
            Assert.IsTrue(batches.All(static batch => batch.Length <= DesktopSearchMeaning.BatchSize), "small batches");
            Assert.IsTrue(batches.All(static batch => batch.Zip(batch.Skip(1)).All(static pair => pair.First.Length <= pair.Second.Length)), "each batch sorted by length");

            // The batches at once follow the settings at once, on the same workers' client.
            settings.SaveSettings(settings.GetSettings() with { Parallelism = 1 });
            Assert.AreEqual(1, clients.Single().Parallelism);
            Assert.AreEqual(EmbeddingClient.ThreadsPerWorker(1, cores), clients.Single().Load.Threads);
            settings.SaveSettings(settings.GetSettings() with { Parallelism = SearchSettings.DefaultParallelism });

            // Paused, the indexing waits and queries are still answered.
            meaning.Paused = true;
            documents.Add(new("doc:e.md", SearchKind.Doc, "Plugins", "A plugin adds commands and canvases.", DateTimeOffset.UtcNow));
            indexer.Signal("docs");
            await UntilAsync(() => meaning.Status.State == "paused");
            Assert.AreEqual("doc:b.md", (await service.SearchAsync(new SearchRequest("microphone dictates", new SearchScope()) { Mode = SearchMode.Meaning })).Hits[0].Ref);
            meaning.Paused = false;
            await UntilAsync(() => meaning.Status is { State: "ready", Embedded: 45 });

            // Turned off, it says so and is not offered again.
            meaning.TurnOff();
            await UntilAsync(() => meaning.Status.State == "off");
            Assert.AreEqual(SearchSettings.MeaningOfferTurnedOff, settings.GetSettings().MeaningOffer);
            Assert.IsEmpty(await meaning.SearchAsync("microphone", new SearchScope(), false, 5, default));
            // The model in use cannot be deleted while it is on; once off it can.
            Assert.IsTrue(await meaning.DeleteModelAsync(model.Id, default));
            Assert.IsFalse(installer.IsInstalled(model));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task OnceItsWorkerEndedWhileIdle_AQueryIsAnsweredByWordsAtOnce_WhileTheWorkerStartsAgain()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-meaning-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var (model, installer) = InstallModel(root);
            await using var store = new SearchIndexStore(Path.Combine(root, "data", "search.sqlite3"));
            var documents = new List<SearchDocument>
            {
                new("doc:a.md", SearchKind.Doc, "Timeline", "We fixed the scroll jump of the timeline by keeping the anchor message.", DateTimeOffset.UtcNow),
                new("doc:b.md", SearchKind.Doc, "Speech", "A microphone button dictates into the prompt with a local speech model.", DateTimeOffset.UtcNow),
            };
            var source = new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. documents]));
            var settings = new Settings(SearchSettings.Default with { Semantic = true, Model = model.Id });
            await using var indexer = new SearchIndexer(store, new NoJournals(), new Host(settings), () => [source], TimeProvider.System,
                new SearchIndexerOptions { StartDelay = TimeSpan.Zero, PassInterval = TimeSpan.FromHours(1), FreshnessBeforeQuery = TimeSpan.Zero });
            indexer.Start();
            indexer.SignalPass();
            await indexer.WaitIdleAsync(Timeout());

            var engines = new FakeEngines(model.Dimensions, words: true);
            EmbeddingClient? client = null;
            // Its worker ends soon after its last batch.
            var meaning = new DesktopSearchMeaning(settings, indexer, installer, new StoreMeaningVectors(store, null),
                load => client = new EmbeddingClient(() => new FakeEmbeddingWorker(engines), load, idle: TimeSpan.FromMilliseconds(200), extraIdle: TimeSpan.FromMilliseconds(200)), null);
            await using var _ = meaning;
            var service = new SearchService(store, indexer, new Host(settings), null) { Meaning = meaning };
            meaning.Start();
            await UntilAsync(() => meaning.Status is { State: "ready", Embedded: 2 });
            await UntilAsync(() => client is { Running: false });
            Assert.AreEqual(SearchMeaningState.Warming, meaning.State, "A query would wait for a worker to start.");

            // The worker loads slowly: the query does not wait for it.
            engines.LoadGate.Reset();
            var started = System.Diagnostics.Stopwatch.StartNew();
            var cold = await service.SearchAsync(new SearchRequest("microphone", new SearchScope()) { Mode = SearchMode.Meaning });
            Assert.IsLessThan(5, started.Elapsed.TotalSeconds, "answered without the load of the model");
            Assert.AreEqual(SearchMeaningState.Warming, cold.Meaning);
            Assert.AreEqual("doc:b.md", cold.Hits.Single().Ref);
            Assert.AreEqual(SearchMatch.Words, cold.Hits[0].Match);
            Assert.AreEqual(SearchMeaningState.Warming, meaning.State, "still warming while the model loads");

            // Once the worker has its model, queries have meaning again.
            engines.LoadGate.Set();
            await UntilAsync(() => meaning.State == SearchMeaningState.Ready);
            var warm = await service.SearchAsync(new SearchRequest("microphone dictates", new SearchScope()) { Mode = SearchMode.Meaning });
            Assert.AreEqual(SearchMeaningState.Ready, warm.Meaning);
            Assert.AreEqual(SearchMatch.Meaning, warm.Hits[0].Match);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    // The model and the runtime on disk already: files of the right sizes, which nothing here reads.
    private static (EmbeddingModelDefinition Model, EmbeddingInstaller Installer) InstallModel(string root)
    {
        var model = EmbeddingModelCatalog.Find("granite-97m-multilingual-r2")!;
        var engine = SpeechModelCatalog.FindEngine(SpeechModelCatalog.CurrentRuntimeIdentifier())!;
        var installer = new EmbeddingInstaller(Path.Combine(root, "search"), Path.Combine(root, "runtime"), Path.Combine(root, "speech"), engine, handler: null);
        var folder = Directory.CreateDirectory(installer.DirectoryOf(model)).FullName;
        foreach (var file in model.Files)
        {
            using var stream = File.Create(Path.Combine(folder, file.Name));
            stream.SetLength(file.Size);
        }

        File.WriteAllText(Path.Combine(folder, "model.json"), "{}");
        var runtime = Directory.CreateDirectory(SherpaRuntime.DirectoryOf(Path.Combine(root, "runtime"), engine)).FullName;
        File.WriteAllText(Path.Combine(runtime, SherpaRuntime.Marker), "{}");
        foreach (var library in engine.Libraries) File.WriteAllText(Path.Combine(runtime, library), "");
        Assert.IsTrue(installer.IsInstalled(model));
        return (model, installer);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Settings(SearchSettings settings) : IMeaningSettings
    {
        private SearchSettings _settings = settings;
        public event Action<SearchSettings, SearchSettings>? SettingsChanged;
        public SearchSettings GetSettings() => _settings;

        public void SaveSettings(SearchSettings settings)
        {
            var before = _settings;
            _settings = settings;
            SettingsChanged?.Invoke(before, settings);
        }
    }

    private sealed class Host(Settings settings) : ISearchHost
    {
        public SearchSettings GetSettings() => settings.GetSettings();
        public ValueTask<IReadOnlyList<SearchProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SearchProjectInfo>>([]);
        public ValueTask<IReadOnlyDictionary<string, SearchSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyDictionary<string, SearchSessionInfo>>(new Dictionary<string, SearchSessionInfo>());
        public bool IsProjectExcludedByItself(SearchProjectInfo project) => false;
    }

    private sealed class NoJournals : CodeAlta.Agent.ISessionJournalCatalog
    {
        public async IAsyncEnumerable<CodeAlta.Agent.SessionJournalFile> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<CodeAlta.Agent.SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<CodeAlta.Agent.SessionJournalFile?>(null);
        public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(null);
    }
}
