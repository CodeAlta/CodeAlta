using CodeAlta.Desktop.Search;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Tests;

/// <summary>The searches of the window are recorded once each, as typed to their end; those of agents at once.</summary>
[TestClass]
public sealed class SearchActivityRecorderTests
{
    [TestMethod]
    public async Task AQueryBeingTyped_IsOneSearch_WithTheAnswerOfItsLastKey_AndAgentsAreRecordedAtOnce()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-activity-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await using var store = new SearchIndexStore(Path.Combine(root, "search.sqlite3"));
            var recorder = new SearchActivityRecorder(store, TimeProvider.System, TimeSpan.FromMilliseconds(150), null);
            SearchResponse Answer(int hits) => new() { Hits = [.. Enumerable.Range(0, hits).Select(index => new SearchHit { Kind = SearchKind.Doc, Ref = "doc:" + index, Title = "t", Snippet = "" })], WordsMilliseconds = hits };
            var typing = new SearchRequest("re", new SearchScope()) { Origin = "window", PrefixLastTerm = true };

            await recorder.RecordAsync(typing, Answer(9));
            await recorder.RecordAsync(typing with { Query = "retr" }, Answer(4));
            await recorder.RecordAsync(typing with { Query = "retry" }, Answer(2));
            await recorder.RecordAsync(new SearchRequest("port", new SearchScope()) { Origin = "agent", OriginSessionId = "s1" }, Answer(1));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while ((await store.GetActivityAsync(DateTimeOffset.Now.AddDays(-1), timeout.Token)).Searches < 2) await Task.Delay(20, timeout.Token);
            }

            var summary = await store.GetActivityAsync(DateTimeOffset.Now.AddDays(-1));
            Assert.AreEqual((2, 1, 1), (summary.Searches, summary.Days.Single().Window, summary.Days.Single().Agent), "three keys, one search");
            Assert.AreEqual(1.5d, summary.MedianWordsMilliseconds, "the search of the window kept the answer of its last key (2 ms), the agent's 1 ms");

            // A query that is not being typed ends the one before it: only it is recorded; what waits is written when the recorder ends.
            await recorder.RecordAsync(typing with { Query = "wor" }, Answer(7));
            await recorder.RecordAsync(typing with { Query = "worktree", PrefixLastTerm = false }, Answer(0));
            await recorder.RecordAsync(typing with { Query = "pending" }, Answer(3));
            await recorder.DisposeAsync();
            summary = await store.GetActivityAsync(DateTimeOffset.Now.AddDays(-1));
            Assert.AreEqual((4, 1), (summary.Searches, summary.Empty));
            await recorder.RecordAsync(typing, Answer(1));
            Assert.AreEqual(4, (await store.GetActivityAsync(DateTimeOffset.Now.AddDays(-1))).Searches, "nothing is recorded once it ended");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
