using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>The counts of the status: read once from the index, kept while it does not change, and right after every change.</summary>
[TestClass]
public sealed class SearchStatusCountsTests
{
    [TestMethod]
    public async Task Status_ReadsTheCountsOnce_UntilTheIndexChanges_AndStaysRightAfterEachChange()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        var now = DateTimeOffset.UtcNow;
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s1"),
        [
            Message("msg:s1/1", "the scroll jumps", "s1", "p1", now),
            Message("msg:s1/2", "it keeps the anchor", "s1", "p1", now, SearchRole.Assistant),
        ]) { Complete = true });

        // The page asks several times at once when it starts: one read answers them all.
        var reads = store.CountReads;
        var statuses = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => fixture.Service.GetStatusAsync()));
        Assert.AreEqual(reads + 1, store.CountReads, "seven status calls at once read the counts once");
        Assert.IsTrue(statuses.All(status => status.Counts.Chunks == statuses[0].Counts.Chunks && status.Counts.Sessions == 1));
        await fixture.Service.GetStatusAsync();
        await store.CountAsync();
        Assert.AreEqual(reads + 1, store.CountReads, "nor again while the index does not change");
        await AssertSameAsync(store, statuses[0].Counts);

        // What leaves the counts as they are reads nothing: a check that found nothing new, the same texts written again, a rename.
        reads = store.CountReads;
        await store.SaveSourceAsync(Source("docs"));
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });
        await store.RetitleSessionAsync("s1", "Renamed");
        await fixture.Service.GetStatusAsync();
        Assert.AreEqual(reads, store.CountReads);

        // Each change is in the next answer.
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings"), Doc("doc:c", "release notes")]) { Complete = true });
        Assert.AreEqual(3, (await AssertSameAsync(store, (await fixture.Service.GetStatusAsync()).Counts)).Items[SearchKind.Doc]);
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s1"),
        [
            Message("msg:s1/1", "the scroll jumps", "s1", "p2", now),
            Message("msg:s1/2", "it keeps the anchor", "s1", "p2", now, SearchRole.Assistant),
        ]) { Complete = true });
        Assert.AreEqual(2, (await AssertSameAsync(store, await store.CountAsync())).Projects["p2"], "an item moved to another project");
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s2"), [Message("msg:s2/1", "a second session", "s2", "p1", now.AddDays(-2))]) { Complete = true });
        var counts = await AssertSameAsync(store, await store.CountAsync());
        Assert.AreEqual((2, 2), (counts.Sessions, counts.Days.Count));
        await store.DeleteSourcesAsync(["journal:s2"]);
        Assert.AreEqual(1, (await AssertSameAsync(store, await store.CountAsync())).Sessions, "a session deleted");
        await store.DeleteContentAsync("p2", null);
        Assert.AreEqual(0, (await AssertSameAsync(store, await store.CountAsync())).Sessions, "a project kept out");
        await store.ClearAsync();
        counts = await AssertSameAsync(store, await store.CountAsync());
        Assert.AreEqual((0, 0L), (counts.Items.Count, counts.Chunks), "a reset");
    }

    // The counts kept are those a read of the tables gives now.
    private static async Task<SearchIndexCounts> AssertSameAsync(SearchIndexStore store, SearchIndexCounts kept)
    {
        var read = await store.CountAsync(new SearchScope(), null);
        CollectionAssert.AreEquivalent(read.Items.ToList(), kept.Items.ToList());
        CollectionAssert.AreEquivalent(read.Projects.ToList(), kept.Projects.ToList());
        CollectionAssert.AreEqual(read.Days.ToList(), kept.Days.ToList());
        Assert.AreEqual((read.Chunks, read.Sessions), (kept.Chunks, kept.Sessions));
        Assert.IsGreaterThan(0, kept.Bytes);
        return kept;
    }

    private static SearchSourceState Source(string key) => new(key, "1", null, null, 0, null, DateTimeOffset.UtcNow);

    private static SearchDocument Doc(string reference, string text) => new(reference, SearchKind.Doc, "Title", text, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private static SearchDocument Message(string reference, string text, string session, string project, DateTimeOffset at, SearchRole role = SearchRole.User)
        => new(reference, SearchKind.Message, "Session", text, at) { SessionId = session, ProjectId = project, Role = role, RunId = "r1" };
}
