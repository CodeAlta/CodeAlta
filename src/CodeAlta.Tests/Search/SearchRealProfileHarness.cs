using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>
/// Builds the index of a real profile and says what it cost. Opt-in: set <c>CODEALTA_SEARCH_SESSIONS</c> to a sessions
/// folder (such as <c>~/.alta/sessions</c>); the index is written in a temporary folder and the journals are only read.
/// </summary>
[TestClass]
public sealed class SearchRealProfileHarness
{
    [TestMethod]
    public async Task FirstBuild_OfARealProfile()
    {
        var sessions = Environment.GetEnvironmentVariable("CODEALTA_SEARCH_SESSIONS");
        if (string.IsNullOrWhiteSpace(sessions) || !Directory.Exists(sessions))
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_SESSIONS to a sessions folder to run this harness.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "codealta-search-harness-" + Guid.NewGuid().ToString("N"));
        var journals = new TreeJournals(sessions);
        var host = new FakeSearchHost();
        var store = new SearchIndexStore(Path.Combine(root, "search.sqlite3"));
        var indexer = new SearchIndexer(store, journals, host, () => [], TimeProvider.System, new SearchIndexerOptions { StartDelay = TimeSpan.FromHours(1), QuietStart = TimeSpan.Zero });
        try
        {
            var peakBefore = Process.GetCurrentProcess().WorkingSet64;
            var watch = Stopwatch.StartNew();
            indexer.Start();
            indexer.SignalPass();
            await Task.Delay(20);
            await indexer.WaitIdleAsync(default);
            var first = watch.Elapsed;
            watch.Restart();
            indexer.SignalPass();
            await Task.Delay(20);
            await indexer.WaitIdleAsync(default);
            var second = watch.Elapsed;
            var counts = await store.CountAsync();
            long bytes = 0;
            await foreach (var journal in journals.ListAsync()) bytes += journal.Length;
            var service = new SearchService(store, indexer, host, journals);
            watch.Restart();
            var response = await service.SearchAsync(new SearchRequest("search index fingerprint journal", new SearchScope()) { Limit = 10 });
            var query = watch.Elapsed;
            Console.WriteLine($"journals: {counts.Sessions} sessions indexed, {bytes / (1024.0 * 1024):F1} MB of journals");
            Console.WriteLine($"items: {string.Join(", ", counts.Items.Select(static pair => $"{SearchNames.Of(pair.Key)} {pair.Value}"))}; chunks {counts.Chunks}");
            Console.WriteLine($"first build {first.TotalSeconds:F2} s; next pass with nothing new {second.TotalMilliseconds:F0} ms");
            Console.WriteLine($"index {counts.Bytes / (1024.0 * 1024):F1} MB; working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB (before {peakBefore / (1024 * 1024)} MB)");
            Console.WriteLine($"a query: {query.TotalMilliseconds:F1} ms, {response.Hits.Count} hits");
            foreach (var hit in response.Hits.Take(5)) Console.WriteLine($"  {SearchNames.Of(hit.Kind)} {hit.Title} — {hit.Snippet[..Math.Min(90, hit.Snippet.Length)]}");
            Assert.IsTrue(counts.Items.GetValueOrDefault(SearchKind.Message) > 0);
        }
        finally
        {
            await indexer.DisposeAsync();
            await store.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The journals of a sessions folder, in its year/month/day folders.</summary>
    private sealed class TreeJournals(string root) : ISessionJournalCatalog
    {
        private Dictionary<string, string>? _paths;

        private Dictionary<string, string> Paths => _paths ??= Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .ToDictionary(static path => Path.GetFileNameWithoutExtension(path), static path => path, StringComparer.Ordinal);

        public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var (id, path) in Paths.Select(static pair => (pair.Key, pair.Value)).OrderByDescending(static pair => File.GetLastWriteTimeUtc(pair.Value)))
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
