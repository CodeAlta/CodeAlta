using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Tests.SearchEvaluation;

/// <summary>
/// The evaluation of search: recall@10 and MRR@10 of words only, meaning only and both (the fusion of the application),
/// for each preset of <see cref="EmbeddingModelCatalog"/>, with the median time of a query, the speed of the embedding,
/// and how much of the first 10 chunks by float32 vectors the 8-bit codes keep.
/// <list type="bullet">
/// <item>The synthetic corpus (<c>SearchEvaluation/corpus.json</c>, with <c>queries.jsonl</c>) is checked in every run; it is
/// measured with the models when <c>CODEALTA_SEARCH_TEST_MODEL_ROOT</c> names a folder with <c>eg2/</c> and <c>granite/</c>
/// (the files of the presets) and <c>CODEALTA_SEARCH_TEST_ONNXRUNTIME</c> the ONNX Runtime library of sherpa-onnx.</item>
/// <item>A private set is measured the same way when <c>~/.alta/search-eval/queries.jsonl</c> exists (another folder with
/// <c>CODEALTA_SEARCH_EVAL</c>). It is one JSON object per line, never committed:
/// <c>{"query": "why did we keep the anchor", "relevant": ["msg:&lt;session id&gt;/&lt;content id&gt;"], "type": "paraphrase"}</c>,
/// with the refs that <c>alta search</c> gives (<c>msg:</c>, <c>session:</c>, <c>note:</c>) and <c>type</c> optional. Its index
/// is built in a temporary folder from the journals of <c>~/.alta/sessions</c> (or <c>CODEALTA_SEARCH_SESSIONS</c>), which
/// are only read.</item>
/// </list>
/// <c>CODEALTA_SEARCH_EVAL_PRESETS</c> limits the presets (ids separated by commas), and <c>CODEALTA_SEARCH_EVAL_REPORT</c>
/// names a file the report is also written to. Only sanity is asserted, so that a change of model or ranking is read in the
/// tables, not in a failure.
/// </summary>
[TestClass]
public sealed class SearchEvaluationTests
{
    [TestMethod]
    public void TheCorpus_IsConsistent_AndEveryRelevantRefExists()
    {
        var documents = EvaluationData.ReadCorpus(Path.Combine(EvaluationData.Folder, "corpus.json"));
        var queries = EvaluationData.ReadQueries(Path.Combine(EvaluationData.Folder, "queries.jsonl"));
        var refs = documents.Select(static document => document.Ref).ToHashSet(StringComparer.Ordinal);
        Assert.HasCount(documents.Count, refs, "refs are unique");
        Assert.IsGreaterThanOrEqualTo(150, documents.Count(static document => document.Kind == SearchKind.Message));
        foreach (var kind in new[] { SearchKind.Session, SearchKind.Doc, SearchKind.Tool, SearchKind.Skill })
            Assert.IsTrue(documents.Any(document => document.Kind == kind), kind.ToString());
        Assert.IsGreaterThanOrEqualTo(40, queries.Count);
        foreach (var type in new[] { "exact", "paraphrase", "cross" })
            Assert.IsGreaterThanOrEqualTo(10, queries.Count(query => query.Type == type), type);
        foreach (var query in queries)
        {
            foreach (var reference in query.Relevant) Assert.Contains(reference, refs, query.Query);
        }
    }

    [TestMethod]
    public async Task TheSyntheticCorpus_ByPresetAndMode()
    {
        if (Models() is not { } models)
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_TEST_MODEL_ROOT and CODEALTA_SEARCH_TEST_ONNXRUNTIME to measure search with the models.");
            return;
        }

        var documents = EvaluationData.ReadCorpus(Path.Combine(EvaluationData.Folder, "corpus.json"));
        var queries = EvaluationData.ReadQueries(Path.Combine(EvaluationData.Folder, "queries.jsonl"));
        var source = new ListSearchSource("evaluation", [SearchKind.Message, SearchKind.Session, SearchKind.Doc, SearchKind.Tool, SearchKind.Skill],
            _ => ValueTask.FromResult(documents));
        // A session is found by its first prompt: as a hit, it counts as that prompt.
        var equivalents = documents.Where(static document => document.Kind == SearchKind.Session).ToDictionary(static document => document.Ref, static document => $"msg:{document.SessionId}/1p");
        var run = await RunAsync(new NoJournals(), [source], queries, models.Root, models.Runtime, "synthetic corpus", equivalents);
        foreach (var preset in Presets())
        {
            // Meaning adds what words miss: the fusion finds at least what words find for paraphrases and across languages.
            foreach (var type in new[] { "paraphrase", "cross" })
                Assert.IsGreaterThanOrEqualTo(run.Scores[(preset, "words", type)].Recall, run.Scores[(preset, "both", type)].Recall, $"{preset} {type}");
            Assert.IsGreaterThan(0.0, run.Scores[(preset, "meaning", "all")].Recall, preset);
            Assert.IsGreaterThanOrEqualTo(0.8, run.Overlap[preset].Mean, preset);
        }
    }

    [TestMethod]
    public async Task ThePrivateSet_OverTheRealProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_EVAL") is { Length: > 0 } named ? named : Path.Combine(home, ".alta", "search-eval");
        var sessions = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_SESSIONS") is { Length: > 0 } given ? given : Path.Combine(home, ".alta", "sessions");
        var file = Path.Combine(folder, "queries.jsonl");
        if (Models() is not { } models || !File.Exists(file) || !Directory.Exists(sessions))
        {
            Assert.Inconclusive("Write ~/.alta/search-eval/queries.jsonl, and set the variables of the models, to measure search on the real profile.");
            return;
        }

        var run = await RunAsync(new TreeJournals(sessions), [], EvaluationData.ReadQueries(file), models.Root, models.Runtime, "private set", null);
        foreach (var preset in Presets()) Assert.IsGreaterThan(0.0, run.Scores[(preset, "both", "all")].Recall, preset);
    }

    private static (string Root, string Runtime)? Models()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_MODEL_ROOT");
        var runtime = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_TEST_ONNXRUNTIME");
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime) ? null : (root, runtime);
    }

    private static IReadOnlyList<string> Presets()
        => Environment.GetEnvironmentVariable("CODEALTA_SEARCH_EVAL_PRESETS") is { Length: > 0 } list
            ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static id => EmbeddingModelCatalog.Find(id)?.Id ?? throw new ArgumentException($"Unknown preset '{id}'."))]
            : [.. EmbeddingModelCatalog.Presets.Select(static preset => preset.Id)];

    // Builds the index in a temporary folder, measures it, and prints the report.
    private static async Task<EvaluationRun> RunAsync(ISessionJournalCatalog journals, IReadOnlyList<ISearchSource> sources, IReadOnlyList<EvaluationQuery> queries,
        string root, string runtime, string name, IReadOnlyDictionary<string, string>? equivalents)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codealta-search-eval-" + Guid.NewGuid().ToString("N"));
        var store = new SearchIndexStore(Path.Combine(folder, "search.sqlite3"));
        var indexer = new SearchIndexer(store, journals, new EvaluationHost(), () => sources, TimeProvider.System,
            new SearchIndexerOptions { StartDelay = TimeSpan.Zero, PassInterval = TimeSpan.FromHours(1), FreshnessBeforeQuery = TimeSpan.Zero });
        var report = new StringWriter();
        try
        {
            indexer.Start();
            indexer.SignalPass();
            await Task.Delay(20);
            await indexer.WaitIdleAsync(default);
            var counts = await store.CountAsync();
            report.WriteLine($"# Search evaluation: {name}");
            report.WriteLine($"items: {string.Join(", ", counts.Items.Select(static pair => $"{SearchNames.Of(pair.Key)} {pair.Value}"))}");
            var run = new EvaluationRun(store, queries, root, runtime, report, equivalents);
            await run.RunAsync(Presets(), variants: true, default);
            return run;
        }
        finally
        {
            Console.WriteLine(report.ToString());
            if (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_EVAL_REPORT") is { Length: > 0 } path) File.AppendAllText(path, report + Environment.NewLine);
            await indexer.DisposeAsync();
            await store.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NoJournals : ISessionJournalCatalog
    {
        public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<SessionJournalFile?>(null);
        public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(null);
    }

    /// <summary>The journals of a sessions folder, in its year/month/day folders, opened to read only.</summary>
    private sealed class TreeJournals(string root) : ISessionJournalCatalog
    {
        private Dictionary<string, string>? _paths;

        private Dictionary<string, string> Paths => _paths ??= Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .ToDictionary(static path => Path.GetFileNameWithoutExtension(path), static path => path, StringComparer.Ordinal);

        public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var (id, path) in Paths.Select(static pair => (pair.Key, pair.Value)))
            {
                var info = new FileInfo(path);
                yield return new SessionJournalFile(id, path, info.Length, info.LastWriteTimeUtc);
            }
        }

        public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!Paths.TryGetValue(sessionId, out var path)) return ValueTask.FromResult<SessionJournalFile?>(null);
            var info = new FileInfo(path);
            return ValueTask.FromResult<SessionJournalFile?>(new SessionJournalFile(sessionId, path, info.Length, info.LastWriteTimeUtc));
        }

        public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default)
        {
            if (!Paths.TryGetValue(sessionId, out var path)) return ValueTask.FromResult<Stream?>(null);
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = offset;
            return ValueTask.FromResult<Stream?>(stream);
        }
    }
}
