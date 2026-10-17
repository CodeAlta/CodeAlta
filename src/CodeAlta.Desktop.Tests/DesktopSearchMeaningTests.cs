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
            var model = EmbeddingModelCatalog.Find("granite-97m-multilingual-r2")!;
            var engine = SpeechModelCatalog.FindEngine(SpeechModelCatalog.CurrentRuntimeIdentifier())!;
            var installer = new EmbeddingInstaller(Path.Combine(root, "search"), Path.Combine(root, "runtime"), Path.Combine(root, "speech"), engine, handler: null);
            // The model and the runtime are on disk already: files of the right sizes, which nothing here reads.
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
            var meaning = new DesktopSearchMeaning(settings, indexer, installer, new StoreMeaningVectors(store, null), load =>
            {
                lock (loads) loads.Add(load);
                return new EmbeddingClient(() => new FakeEmbeddingWorker(engines), load);
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

            // Paused, the indexing waits and queries are still answered.
            meaning.Paused = true;
            documents.Add(new("doc:e.md", SearchKind.Doc, "Plugins", "A plugin adds commands and canvases.", DateTimeOffset.UtcNow));
            indexer.Signal("docs");
            await UntilAsync(() => meaning.Status.State == "paused");
            Assert.AreEqual("doc:b.md", (await service.SearchAsync(new SearchRequest("microphone dictates", new SearchScope()) { Mode = SearchMode.Meaning })).Hits[0].Ref);
            meaning.Paused = false;
            await UntilAsync(() => meaning.Status is { State: "ready", Embedded: 5 });

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
