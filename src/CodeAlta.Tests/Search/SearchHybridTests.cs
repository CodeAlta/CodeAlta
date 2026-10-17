using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

/// <summary>Words and meaning fused: each hit says how it was found, and an answer says where search by meaning is.</summary>
[TestClass]
public sealed class SearchHybridTests
{
    [TestMethod]
    public async Task AHitFoundBothWays_RanksFirst_AndAHitFoundByMeaningOnly_IsSaidSo()
    {
        var documents = new List<SearchDocument>
        {
            new("doc:scroll.md", SearchKind.Doc, "Timeline", "We fixed the scroll jump of the timeline.", DateTimeOffset.UtcNow),
            new("doc:voice.md", SearchKind.Doc, "Speech", "A microphone button dictates into the prompt.", DateTimeOffset.UtcNow),
            new("doc:other.md", SearchKind.Doc, "Other", "The scroll bar of the editor.", DateTimeOffset.UtcNow),
        };
        await using var fixture = new SearchFixture(new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. documents])));
        fixture.Host.Settings = CodeAlta.Catalog.Search.SearchSettings.Default with { Semantic = true };
        await fixture.PassAsync();
        var chunks = await ChunksAsync(fixture.Store);
        var meaning = new FakeMeaning { Closest = [(chunks["doc:voice.md"], 0.9f), (chunks["doc:scroll.md"], 0.8f)] };
        fixture.Service.Meaning = meaning;

        var response = await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()) { Limit = 10 });
        Assert.AreEqual(SearchMeaningState.Ready, response.Meaning);
        Assert.AreEqual(SearchMode.Auto, response.Mode);
        Assert.AreEqual("doc:scroll.md", response.Hits[0].Ref, "found by its words and by its meaning");
        Assert.AreEqual(SearchMatch.Both, response.Hits[0].Match);
        var voice = response.Hits.Single(static hit => hit.Ref == "doc:voice.md");
        Assert.AreEqual(SearchMatch.Meaning, voice.Match);
        Assert.AreEqual(1, voice.MeaningRank);
        Assert.IsNull(voice.WordsRank);
        Assert.IsFalse(string.IsNullOrEmpty(voice.Snippet), "a hit found by meaning only has the start of its text");
        Assert.AreEqual(SearchMatch.Words, response.Hits.Single(static hit => hit.Ref == "doc:other.md").Match);

        // By words only, meaning is not asked; by meaning only, words are not.
        meaning.Calls = 0;
        var words = await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()) { Mode = SearchMode.Keyword });
        Assert.AreEqual(0, meaning.Calls);
        Assert.IsTrue(words.Hits.All(static hit => hit.Match == SearchMatch.Words));
        var byMeaning = await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()) { Mode = SearchMode.Meaning });
        Assert.IsTrue(byMeaning.Hits.All(static hit => hit.Match == SearchMatch.Meaning));

        // While it is off, an answer says so and keeps to the words.
        meaning.State = SearchMeaningState.Off;
        var off = await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()));
        Assert.AreEqual(SearchMeaningState.Off, off.Meaning);
        Assert.IsTrue(off.Hits.All(static hit => hit.Match == SearchMatch.Words));
    }

    [TestMethod]
    public async Task TheConstantsOfTheRanking_AreThoseItIsGiven()
    {
        var documents = new List<SearchDocument>
        {
            new("doc:scroll.md", SearchKind.Doc, "Timeline", "We fixed the scroll jump of the timeline.", DateTimeOffset.UtcNow),
            new("doc:voice.md", SearchKind.Doc, "Speech", "A microphone button dictates into the prompt.", DateTimeOffset.UtcNow),
        };
        await using var fixture = new SearchFixture(new ListSearchSource("docs", [SearchKind.Doc], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. documents])));
        fixture.Host.Settings = CodeAlta.Catalog.Search.SearchSettings.Default with { Semantic = true };
        await fixture.PassAsync();
        var chunks = await ChunksAsync(fixture.Store);
        fixture.Service.Meaning = new FakeMeaning { Closest = [(chunks["doc:voice.md"], 0.9f), (chunks["doc:scroll.md"], 0.8f)] };
        Assert.AreEqual(SearchRanking.Default, fixture.Service.Ranking);
        Assert.AreEqual(new SearchRanking(60, 100, 3, 1, 1, 1, true), SearchRanking.Default);
        Assert.AreEqual("doc:scroll.md", (await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()))).Hits[0].Ref);

        // Words that weigh nothing leave the order to meaning.
        fixture.Service.Ranking = SearchRanking.Default with { WordsWeight = 0 };
        var hits = (await fixture.Service.SearchAsync(new SearchRequest("scroll", new SearchScope()))).Hits;
        Assert.AreEqual("doc:voice.md", hits[0].Ref);
        Assert.AreEqual(SearchMatch.Both, hits[1].Match);
    }

    [TestMethod]
    public async Task AMessageThatMatchesOnlyTheTitleOfItsSession_IsLeftToTheSession()
    {
        await using var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new("s1", "Release notes", null, "default", false);
        fixture.Journals.Write(new JournalWriter("s1", DateTimeOffset.UtcNow.AddHours(-1)).User("r1", "user:1", "Write the release notes").Assistant("r1", "a:1", "Done.").Idle("r1"));
        await fixture.PassAsync();
        var hits = (await fixture.SearchAsync("release notes")).Hits;
        Assert.IsTrue(hits.Any(static hit => hit.Ref == "msg:s1/user:1"), "the prompt has the words");
        Assert.IsFalse(hits.Any(static hit => hit.Ref == "msg:s1/a:1"), "the answer has only the title of its session");
        Assert.IsTrue(hits.Any(static hit => hit.Kind == SearchKind.Session));
    }

    private static async Task<Dictionary<string, long>> ChunksAsync(SearchIndexStore store)
        => await store.ReadAsync<Dictionary<string, long>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT i.ref, c.id FROM search_chunk c JOIN search_item i ON i.id = c.item_id";
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) map[reader.GetString(0)] = reader.GetInt64(1);
            return map;
        }, default);

    private sealed class FakeMeaning : ISearchMeaning
    {
        public IReadOnlyList<(long ChunkId, float Similarity)> Closest { get; set; } = [];
        public int Calls { get; set; }
        public SearchMeaningState State { get; set; } = SearchMeaningState.Ready;
        public double Coverage => 1;

        public ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(string query, SearchScope scope, bool codeQuery, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(State == SearchMeaningState.Ready ? Closest : []);
        }
    }
}
