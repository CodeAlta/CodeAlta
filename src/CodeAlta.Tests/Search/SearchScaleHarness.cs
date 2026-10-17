using System.Diagnostics;
using System.Text;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>
/// The time budget of a query by words on a large index: 200,000 chunks (2,000 sessions of 100 messages), under 30 ms
/// at the 95th percentile. Opt-in, since filling the index takes a minute: set <c>CODEALTA_SEARCH_SCALE=1</c>
/// (<c>CODEALTA_SEARCH_SCALE_SESSIONS</c> changes the number of sessions).
/// </summary>
[TestClass]
public sealed class SearchScaleHarness
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AQueryByWords_OnTwoHundredThousandChunks_StaysUnderItsBudget()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_SCALE") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_SCALE=1 to run this harness.");
            return;
        }

        var sessions = int.TryParse(Environment.GetEnvironmentVariable("CODEALTA_SEARCH_SCALE_SESSIONS"), out var count) && count > 0 ? count : 2000;
        const int MessagesPerSession = 100;
        await using var fixture = new SearchFixture();
        var random = new Random(118);
        var words = Vocabulary(random);
        var now = DateTimeOffset.UtcNow;

        var watch = Stopwatch.StartNew();
        for (var session = 0; session < sessions; session++)
        {
            var documents = new SearchDocument[MessagesPerSession];
            for (var message = 0; message < MessagesPerSession; message++)
            {
                documents[message] = new SearchDocument($"msg:s{session}/m{message}", SearchKind.Message, $"Session {session}", Sentence(random, words, 40 + random.Next(80)), now.AddMinutes(-(session * MessagesPerSession + message)))
                {
                    SessionId = $"s{session}", ProjectId = $"p{session % 40}", SpaceId = session % 5 == 0 ? "work" : "default", Role = message % 2 == 0 ? SearchRole.User : SearchRole.Assistant,
                };
            }

            await fixture.Store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState($"journal:s{session}", "1", null, null, 0, null, now), documents), TestContext.CancellationToken);
        }

        var fill = watch.Elapsed;
        var counts = await fixture.Store.CountAsync(TestContext.CancellationToken);

        // Queries of two or three words, frequent and rare, the last one typed in part as in the search window.
        var queries = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var parts = Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Pick(random, words)).ToArray();
            if (i % 3 == 0 && parts[^1].Length > 4) parts[^1] = parts[^1][..^2];
            queries.Add(string.Join(' ', parts));
        }

        foreach (var query in queries.Take(20)) await fixture.SearchAsync(query);
        var times = new List<double>();
        var scoped = new List<double>();
        var slowest = new List<(double Milliseconds, string Query, double Words, int Hits)>();
        foreach (var query in queries)
        {
            watch.Restart();
            var response = await fixture.SearchAsync(query);
            times.Add(watch.Elapsed.TotalMilliseconds);
            slowest.Add((watch.Elapsed.TotalMilliseconds, query, response.WordsMilliseconds, response.Hits.Count));
            watch.Restart();
            await fixture.SearchAsync(query, new SearchScope { ProjectId = "p7" });
            scoped.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        scoped.Sort();
        static double At(List<double> sorted, double share) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * share))];
        TestContext.WriteLine($"{counts.Chunks} chunks of {sessions} sessions, filled in {fill.TotalSeconds:F1} s, index {counts.Bytes / (1024.0 * 1024):F0} MB");
        TestContext.WriteLine($"all: p50 {At(times, 0.5):F1} ms, p95 {At(times, 0.95):F1} ms, max {times[^1]:F1} ms");
        foreach (var slow in slowest.OrderByDescending(static slow => slow.Milliseconds).Take(8)) TestContext.WriteLine($"  slow: {slow.Milliseconds:F1} ms (words {slow.Words:F1} ms, {slow.Hits} hits) '{slow.Query}'");
        TestContext.WriteLine($"one project: p50 {At(scoped, 0.5):F1} ms, p95 {At(scoped, 0.95):F1} ms, max {scoped[^1]:F1} ms");
        Assert.IsGreaterThanOrEqualTo(sessions * MessagesPerSession, counts.Chunks);
        Assert.IsLessThan(30, At(times, 0.95), "a query by words at the 95th percentile");
        Assert.IsLessThan(30, At(scoped, 0.95), "a query of one project at the 95th percentile");
    }

    // Common words, and invented words of code with a Zipf-like use: a few are everywhere, most are rare.
    private static string[] Vocabulary(Random random)
    {
        string[] common = ["the", "test", "file", "fix", "build", "error", "session", "journal", "window", "search", "index", "port", "retry", "model", "tool", "plan", "commit", "branch", "review", "query"];
        const string Letters = "abcdefghijklmnopqrstuvwxyz";
        var invented = Enumerable.Range(0, 6000).Select(_ =>
        {
            var builder = new StringBuilder();
            var length = 4 + random.Next(8);
            for (var i = 0; i < length; i++) builder.Append(Letters[random.Next(Letters.Length)]);
            return builder.ToString();
        });
        return [.. common, .. invented];
    }

    private static string Pick(Random random, string[] words)
    {
        // The cube of a uniform value favors the first words of the list, the common ones.
        var share = random.NextDouble();
        return words[(int)(share * share * share * (words.Length - 1))];
    }

    private static string Sentence(Random random, string[] words, int length)
    {
        var builder = new StringBuilder(length * 8);
        for (var i = 0; i < length; i++)
        {
            if (i > 0) builder.Append(i % 13 == 0 ? ". " : " ");
            builder.Append(Pick(random, words));
        }

        return builder.Append('.').ToString();
    }
}
