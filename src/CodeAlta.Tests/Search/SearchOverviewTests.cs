using CodeAlta.Catalog.Search;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>What the Search canvas reads without a query: the counts of what a space sees, the items of a kind, and the state of the sources.</summary>
[TestClass]
public sealed class SearchOverviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static SearchDocument Message(string reference, string session, string project, DateTimeOffset at, SearchRole role = SearchRole.User)
        => new(reference, SearchKind.Message, "Turn", "the retry test waits for the port", at) { SessionId = session, ProjectId = project, Role = role, SpaceId = session == "s1" ? "work" : "default" };

    private static async Task FillAsync(SearchFixture fixture)
    {
        var journal = new SearchSourceState("journal:s1", "1", null, null, 0, null, Now);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(journal,
        [
            Message("msg:s1/a", "s1", "p1", Now.AddDays(-1)),
            Message("msg:s1/b", "s1", "p1", Now.AddDays(-1), SearchRole.Assistant),
            Message("msg:s1/c", "s1", "p1", Now.AddDays(-30)),
        ]));
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState("journal:s2", "1", null, null, 0, null, Now.AddMinutes(-5), "unreadable line"),
            [Message("msg:s2/a", "s2", "p2", Now.AddDays(-2))]));
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState("skills", "1", null, null, 0, null, Now),
        [
            new SearchDocument("skill:release", SearchKind.Skill, "release", "Release a new version", DateTimeOffset.UnixEpoch) { Extra = """{"name":"release"}""" },
            new SearchDocument("skill:pdf", SearchKind.Skill, "pdf", "Read a PDF", DateTimeOffset.UnixEpoch) { Extra = """{"name":"pdf"}""" },
        ]) { Complete = true });
    }

    [TestMethod]
    public async Task Overview_CountsWhatTheScopeSees_TheRecentItems_AndTheSkillsTheSpaceLets()
    {
        await using var fixture = new SearchFixture();
        await FillAsync(fixture);

        var all = await fixture.Service.GetOverviewAsync(new SearchScope(), Now.AddDays(-7));
        Assert.AreEqual(4, all.Counts.Items[SearchKind.Message]);
        Assert.AreEqual(2, all.Counts.Items[SearchKind.Skill]);
        Assert.AreEqual(2, all.Counts.Sessions);
        Assert.AreEqual(3, all.Counts.Recent, "the messages of the last week; the skills have no date");
        Assert.AreEqual(3, all.Counts.Projects["p1"]);
        Assert.AreEqual(1, all.Counts.Projects["p2"]);
        Assert.IsTrue(all.Counts.Bytes > 0);

        // A space with one project sees its sessions, its project and what has none, and only the skills it lets its sessions use.
        var space = new SearchScope
        {
            SpaceId = "work", Projects = new HashSet<string> { "p1" },
            CatalogFilter = hit => hit.Kind != SearchKind.Skill || hit.Extra?.Contains("\"release\"", StringComparison.Ordinal) == true,
        };
        var work = await fixture.Service.GetOverviewAsync(space, Now.AddDays(-7));
        Assert.AreEqual(3, work.Counts.Items[SearchKind.Message]);
        Assert.AreEqual(1, work.Counts.Items[SearchKind.Skill]);
        Assert.AreEqual(1, work.Counts.Sessions);
        Assert.AreEqual(2, work.Counts.Recent);
        Assert.IsFalse(work.Counts.Projects.ContainsKey("p2"));
        // One prompt and one answer the same day, one prompt a month before: a turn is counted by its prompt.
        CollectionAssert.AreEqual(new[] { 1L, 1L }, work.Counts.Days.Select(static day => day.Turns).ToArray());

        // A project kept out of search in the settings is kept out of the overview too.
        fixture.Host.Settings = SearchSettings.Default with { ExcludedProjects = ["p1"] };
        var kept = await fixture.Service.GetOverviewAsync(new SearchScope(), null);
        Assert.AreEqual(1, kept.Counts.Items[SearchKind.Message]);
        Assert.AreEqual(0, kept.Counts.Recent, "no time given, nothing counted as recent");
    }

    [TestMethod]
    public async Task Overview_SumsUpTheSourcesByFamily_WithTheirLastCheckAndFailures()
    {
        await using var fixture = new SearchFixture();
        await FillAsync(fixture);

        var sources = (await fixture.Service.GetOverviewAsync(new SearchScope(), null)).Sources;

        CollectionAssert.AreEqual(new[] { "journal", "skills" }, sources.Select(static source => source.Family).ToArray());
        var journals = sources[0];
        Assert.AreEqual((2L, 1L, "unreadable line"), (journals.Sources, journals.Failed, journals.LastError));
        Assert.IsNotNull(journals.LastCheck);
        Assert.AreEqual(Now.ToUnixTimeMilliseconds(), journals.LastCheck.Value.ToUnixTimeMilliseconds(), "the newest check of the family");
        Assert.AreEqual((1L, 0L, (string?)null), (sources[1].Sources, sources[1].Failed, sources[1].LastError));
    }

    [TestMethod]
    public async Task Overview_SaysWhatIsSearchableByMeaning_InTheScope_AndDayByDay()
    {
        await using var fixture = new SearchFixture();
        await FillAsync(fixture);
        Assert.IsNull((await fixture.Service.GetOverviewAsync(new SearchScope(), null)).Meaning, "search by meaning is off");

        // The messages of yesterday have their vectors; the one of a month ago and the skills do not.
        var set = await fixture.Store.OpenVectorSetAsync(new SearchVectorSetIdentity("model", "r1", 8, "p", "ort"));
        var work = await fixture.Store.ListMissingVectorsAsync(set.Id, 100);
        var yesterday = work.Where(static item => item.Text.Contains("retry", StringComparison.Ordinal)).Take(1).ToList();
        await fixture.Store.PutVectorsAsync(set.Id, [.. yesterday.Select(static item => new SearchVectorInput(item.EmbedHash, SearchVectorTestData.VectorOf(item.Text, 8)))]);
        fixture.Host.Settings = SearchSettings.Default with { Semantic = true };

        var meaning = (await fixture.Service.GetOverviewAsync(new SearchScope(), null)).Meaning;
        Assert.IsNotNull(meaning);
        // The four messages share one text, which has its vector: every message is searchable by meaning, the two skills are not.
        Assert.AreEqual((6L, 4L), (meaning.Coverage.Chunks, meaning.Coverage.Embedded));
        Assert.HasCount(3, meaning.Days);
        Assert.IsTrue(meaning.Days.All(static day => day.Coverage.Embedded == day.Coverage.Chunks));

        // A space sees its own sessions only.
        var work2 = await fixture.Service.GetOverviewAsync(new SearchScope { SpaceId = "work", Projects = new HashSet<string> { "p1" } }, null);
        Assert.AreEqual(5L, work2.Meaning!.Coverage.Chunks, "the three messages of the space and the two skills");
    }

    [TestMethod]
    public async Task Browse_ListsAKind_TheNewestFirst_InTheScope_WithTheStartOfTheText()
    {
        await using var fixture = new SearchFixture();
        await FillAsync(fixture);
        var messages = new HashSet<SearchKind> { SearchKind.Message };

        var (hits, truncated) = await fixture.Service.BrowseAsync(new SearchScope { Kinds = messages }, 2);
        Assert.IsTrue(truncated);
        CollectionAssert.AreEqual(new[] { "msg:s1/a", "msg:s1/b" }, hits.Select(static hit => hit.Ref).OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        Assert.AreEqual("the retry test waits for the port", hits[0].Snippet);
        Assert.IsNull(hits[0].WordsRank, "a listed item has no rank");

        var (inProject, more) = await fixture.Service.BrowseAsync(new SearchScope { Kinds = messages, ProjectId = "p2" }, 10);
        Assert.IsFalse(more);
        Assert.AreEqual("msg:s2/a", inProject.Single().Ref);

        // The skills have no date: they come by title, and a space sees only those it lets its sessions use.
        var skills = new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Skill } };
        CollectionAssert.AreEqual(new[] { "pdf", "release" }, (await fixture.Service.BrowseAsync(skills, 10)).Hits.Select(static hit => hit.Title).ToArray());
        var filtered = await fixture.Service.BrowseAsync(skills with { CatalogFilter = static hit => hit.Title == "release" }, 10);
        Assert.AreEqual("release", filtered.Hits.Single().Title);

        fixture.Host.Settings = SearchSettings.Default with { Enabled = false };
        Assert.IsEmpty((await fixture.Service.BrowseAsync(skills, 10)).Hits, "no index, nothing listed");
    }
}
