using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>The activity of search: one row per search, never its text, counted by day and by who asked, kept 90 days.</summary>
[TestClass]
public sealed class SearchActivityTests
{
    private static SearchActivityEntry Entry(DateTimeOffset at, SearchOrigin origin, int hits, double words, double? meaning = null, long tokens = 0)
        => new(at, origin, origin == SearchOrigin.Agent ? "s1" : null, null, SearchMode.Auto, hits, words, meaning, tokens);

    [TestMethod]
    public async Task Activity_CountsTheSearchesByDayAndOrigin_TheEmptyOnes_AndTheMedianTimes()
    {
        await using var fixture = new SearchFixture();
        var now = DateTimeOffset.Now;
        var yesterday = now.AddDays(-1);
        await fixture.Store.RecordActivityAsync(Entry(yesterday, SearchOrigin.Window, 3, 4));
        await fixture.Store.RecordActivityAsync(Entry(yesterday, SearchOrigin.Agent, 0, 6, 30));
        await fixture.Store.RecordActivityAsync(Entry(now, SearchOrigin.Agent, 5, 2, 10));
        await fixture.Store.RecordActivityAsync(Entry(now, SearchOrigin.ToolSearch, 1, 1, 20, tokens: 1200));
        await fixture.Store.RecordActivityAsync(Entry(now, SearchOrigin.Other, 2, 8));

        var summary = await fixture.Store.GetActivityAsync(now.AddDays(-90));

        Assert.AreEqual((5, 1), (summary.Searches, summary.Empty));
        Assert.AreEqual(4d, summary.MedianWordsMilliseconds, "the median of 1, 2, 4, 6, 8");
        Assert.AreEqual(20d, summary.MedianMeaningMilliseconds, "only the searches that had a search by meaning");
        Assert.AreEqual(1200L, summary.Tokens);
        Assert.HasCount(2, summary.Days);
        Assert.AreEqual(new SearchActivityDay(DateOnly.FromDateTime(yesterday.LocalDateTime), 1, 1, 0, 0, 1), summary.Days[0]);
        Assert.AreEqual(new SearchActivityDay(DateOnly.FromDateTime(now.LocalDateTime), 0, 1, 1, 1, 0), summary.Days[1]);

        var none = await fixture.Store.GetActivityAsync(now.AddMinutes(1));
        Assert.AreEqual((0, (double?)null, (double?)null), (none.Searches, none.MedianWordsMilliseconds, none.MedianMeaningMilliseconds));
    }

    [TestMethod]
    public async Task Activity_IsKept90Days()
    {
        await using var fixture = new SearchFixture();
        var now = DateTimeOffset.Now;
        await fixture.Store.RecordActivityAsync(Entry(now.AddDays(-100), SearchOrigin.Window, 1, 1));
        await fixture.Store.RecordActivityAsync(Entry(now.AddDays(-89), SearchOrigin.Window, 1, 1));
        Assert.AreEqual(2, (await fixture.Store.GetActivityAsync(now.AddDays(-365))).Searches, "the old search goes with the next write");

        await fixture.Store.RecordActivityAsync(Entry(now, SearchOrigin.Agent, 1, 1));
        var kept = await fixture.Store.GetActivityAsync(now.AddDays(-365));
        Assert.AreEqual(2, kept.Searches, "a search older than 90 days is forgotten when one is recorded");
        Assert.AreEqual(DateOnly.FromDateTime(now.AddDays(-89).LocalDateTime), kept.Days[0].Day);
    }

    [TestMethod]
    public void AnEntry_IsMadeOfTheQueryAndItsAnswer_WithoutItsText()
    {
        var request = new SearchRequest("secret words", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Skill } }) { Origin = "window", Mode = SearchMode.Meaning };
        var response = new SearchResponse { Hits = [], WordsMilliseconds = 3.5, MeaningMilliseconds = 0 };
        var entry = SearchActivityEntry.Of(request, response, DateTimeOffset.UnixEpoch);
        Assert.AreEqual((SearchOrigin.Window, SearchMode.Meaning, 0, 3.5, (double?)null), (entry.Origin, entry.Mode, entry.Hits, entry.WordsMilliseconds, entry.MeaningMilliseconds));
        Assert.IsFalse(entry.ToString().Contains("secret", StringComparison.Ordinal), "the text of the query is not kept");
        Assert.AreEqual(SearchOrigin.Agent, SearchActivityEntry.Of(request with { Origin = "agent" }, response, DateTimeOffset.UnixEpoch).Origin);
        Assert.AreEqual(SearchOrigin.ToolSearch, SearchActivityEntry.Of(request with { Origin = "tool_search" }, response, DateTimeOffset.UnixEpoch).Origin);
        Assert.AreEqual(SearchOrigin.Other, SearchActivityEntry.Of(request with { Origin = "client" }, response, DateTimeOffset.UnixEpoch).Origin);
    }
}
