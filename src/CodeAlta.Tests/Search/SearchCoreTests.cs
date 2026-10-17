using CodeAlta.Catalog.Search;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

[TestClass]
public sealed class SearchTextTests
{
    [TestMethod]
    public void Chunk_KeepsShortTextWhole_AndCutsLongTextAtParagraphs()
    {
        Assert.HasCount(1, SearchText.Chunk("short"));
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 30).Select(static i => $"Paragraph {i} " + new string('a', 200)));
        var chunks = SearchText.Chunk(paragraphs);
        Assert.IsGreaterThan(1, chunks.Count);
        Assert.IsTrue(chunks.All(static chunk => chunk.Length <= SearchText.ChunkCharacters), "no chunk is longer than the limit");
        Assert.IsTrue(chunks.All(static chunk => chunk.StartsWith("Paragraph", StringComparison.Ordinal)), "a cut falls between paragraphs");
    }

    [TestMethod]
    public void Chunk_CutsAVeryLongLine_AndKeepsAtMostTheLimit()
    {
        var chunks = SearchText.Chunk(new string('z', SearchText.ChunkCharacters * 100));
        Assert.HasCount(SearchText.MaximumChunks, chunks);
    }

    [TestMethod]
    public void SplitIdentifiers_GivesThePartsOfCamelSnakeAndPaths()
    {
        var words = SearchText.SplitIdentifiers("Call SessionJournalSqliteCache.ReconcileAsync on src/CodeAlta.Search/search_index.cs and HTTPServer");
        StringAssert.Contains(words, "session journal sqlite cache");
        StringAssert.Contains(words, "reconcile async");
        StringAssert.Contains(words, "search index");
        StringAssert.Contains(words, "http server");
        Assert.DoesNotContain("call", words, "a word of one part is not repeated");
    }

    [TestMethod]
    public void BuildMatch_QuotesEveryTerm_SoThatNothingIsAnOperator()
    {
        Assert.AreEqual("\"fingerprint\" AND \"journal\"", SearchText.BuildMatch("the fingerprint of the journal", any: false, prefixLastTerm: false));
        var tricky = SearchText.BuildMatch("a\"b NOT c", any: false, prefixLastTerm: false)!;
        StringAssert.Contains(tricky, "\"a\"\"b\"", "a quote inside a term is doubled");
        StringAssert.EndsWith(tricky, " AND \"NOT\" AND \"c\"", "an operator of FTS5 is only a word");
        Assert.AreEqual("\"scroll jump\" OR \"time\"*", SearchText.BuildMatch("\"scroll jump\" time", any: true, prefixLastTerm: true));
        StringAssert.Contains(SearchText.BuildMatch("SessionJournalSqliteCache", any: false, prefixLastTerm: false), "words : \"session journal sqlite cache\"");
        Assert.IsNull(SearchText.BuildMatch("  -- ? ", any: false, prefixLastTerm: false));
        Assert.AreEqual("\"why\" AND \"what\"", SearchText.BuildMatch("why what", any: false, prefixLastTerm: false), "a query of common words only keeps them");
        // Words found almost everywhere are left out of the query that takes any word, whatever their case.
        var common = new HashSet<string> { "build" };
        Assert.AreEqual("\"zebra\"", SearchText.BuildMatch("zebra Build", any: true, prefixLastTerm: false, common));
        Assert.IsNull(SearchText.BuildMatch("build", any: true, prefixLastTerm: false, common), "nothing is left to look for");
        Assert.AreEqual("\"zebra\" AND \"build\"", SearchText.BuildMatch("zebra build", any: false, prefixLastTerm: false, common), "every word is required still");
    }

    [TestMethod]
    public void TitleCoverage_CountsTheWordsOfTheQueryInTheTitle_WithItsIdentifiers()
    {
        Assert.AreEqual(1, SearchText.TitleCoverage("read file", "read_file", prefixLastTerm: false));
        Assert.AreEqual(1, SearchText.TitleCoverage("read_file", "read_file", prefixLastTerm: false));
        Assert.AreEqual(1, SearchText.TitleCoverage("the session journal", "SessionJournalReader", prefixLastTerm: false));
        Assert.AreEqual(0.5, SearchText.TitleCoverage("read image", "read_file", prefixLastTerm: false));
        Assert.AreEqual(0, SearchText.TitleCoverage("prompt", "read_file", prefixLastTerm: false));
        // The word being typed matches the start of a word.
        Assert.AreEqual(1, SearchText.TitleCoverage("work", "Worktrees › Cleanup", prefixLastTerm: true));
        Assert.AreEqual(0, SearchText.TitleCoverage("work", "Worktrees › Cleanup", prefixLastTerm: false));
    }

    [TestMethod]
    public void ReadSnippet_GivesTheMarks()
    {
        var (text, marks) = SearchText.ReadSnippet($"the {SearchText.MarkStart}fingerprint{SearchText.MarkEnd} of\nthe file");
        Assert.AreEqual("the fingerprint of the file", text);
        Assert.HasCount(1, marks);
        Assert.AreEqual("fingerprint", text.Substring(marks[0].Start, marks[0].Length));
    }
}

[TestClass]
public sealed class SearchIndexStoreTests
{
    private static SearchDocument Doc(string reference, string text, SearchKind kind = SearchKind.Doc, string? project = null, string title = "Title")
        => new(reference, kind, title, text, DateTimeOffset.UtcNow) { ProjectId = project };

    [TestMethod]
    public async Task Apply_WritesOnlyWhatChanged_AndRemovesWhatIsGone()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("docs", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        Assert.AreEqual(2, await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true }));
        Assert.AreEqual(0, await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true }),
            "the same texts are not written again");
        Assert.AreEqual(1, await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "worktree cleanup of branches")]) { Complete = true }));

        var hits = (await fixture.SearchAsync("worktree")).Hits;
        Assert.HasCount(1, hits);
        Assert.AreEqual("doc:a", hits[0].Ref);
        Assert.IsEmpty((await fixture.SearchAsync("provider")).Hits, "doc:b is gone with the complete update that no longer holds it");
        Assert.IsEmpty((await fixture.SearchAsync("cleanup ZZZnothing")).Hits.Where(static hit => hit.Ref == "doc:b"));
    }

    [TestMethod]
    public async Task Search_OfWordsThatNeverMeet_InALargeIndex_LeavesOutTheWordsFoundEverywhere()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("docs", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        var documents = Enumerable.Range(0, 1200).Select(i => Doc($"doc:{i}", $"build log number {i}")).Append(Doc("doc:zebra", "the zebra crossing")).ToList();
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, documents) { Complete = true });

        Assert.HasCount(10, (await fixture.SearchAsync("zebra build")).Hits, "an index under the floor keeps the words as typed");

        // No chunk has both words: the query that takes any word looks for the rare one only.
        fixture.Service.Ranking = SearchRanking.Default with { CommonTermsFloor = 1000 };
        var hits = (await fixture.SearchAsync("zebra build")).Hits;
        Assert.AreEqual("doc:zebra", hits.Single().Ref);
        Assert.HasCount(10, (await fixture.SearchAsync("build log")).Hits, "words that meet are found as before");
    }

    [TestMethod]
    public async Task AnIndexOfVersionTwo_GetsItsVocabulary_AndKeepsWhatItHas()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-v2-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "search.sqlite3");
        try
        {
            await using (var store = new SearchIndexStore(path))
            {
                await store.ApplyAsync(new SearchSourceUpdate(new SearchSourceState("docs", "1", null, null, 0, null, DateTimeOffset.UtcNow), [Doc("doc:a", "worktree cleanup")]) { Complete = true });
            }

            // What version 2 was: the same tables, without the view of the vocabulary.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP TABLE search_vocab; UPDATE app_meta SET version = 2 WHERE owner = '{SearchIndexStore.Owner}';";
                await command.ExecuteNonQueryAsync();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using (var store = new SearchIndexStore(path))
            {
                var (chunks, documents) = await store.CountTermsAsync(["worktree", "absent"], default);
                Assert.AreEqual(1, chunks, "the items are kept");
                Assert.AreEqual(1, documents["worktree"]);
                Assert.IsFalse(documents.ContainsKey("absent"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task Search_FindsIdentifiersByTheirParts_AndMarksTheWords()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("docs", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "The SessionJournalSqliteCache keeps a projection of the journals.")]) { Complete = true });

        var whole = (await fixture.SearchAsync("SessionJournalSqliteCache")).Hits.Single();
        Assert.AreEqual("doc:a", whole.Ref);
        Assert.IsNotEmpty(whole.Marks);
        Assert.HasCount(1, (await fixture.SearchAsync("sqlite cache")).Hits, "the parts of an identifier find it");
        Assert.HasCount(1, (await fixture.SearchAsync("journ", null)).Hits.Where(static _ => false).DefaultIfEmpty(whole), "sanity");
        var typed = await fixture.Service.SearchAsync(new SearchRequest("projec", new SearchScope()) { PrefixLastTerm = true });
        Assert.HasCount(1, typed.Hits, "the word being typed matches as a prefix");
    }

    [TestMethod]
    public async Task Search_KeepsToItsScope()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("plans", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source,
        [
            Doc("plan:p1/a", "release checklist", SearchKind.Plan, "p1"),
            Doc("plan:p2/b", "release checklist", SearchKind.Plan, "p2"),
            Doc("doc:c", "release checklist", SearchKind.Doc),
        ]) { Complete = true });

        Assert.HasCount(3, (await fixture.SearchAsync("release")).Hits);
        Assert.HasCount(1, (await fixture.SearchAsync("release", new SearchScope { ProjectId = "p2" })).Hits);
        Assert.HasCount(2, (await fixture.SearchAsync("release", new SearchScope { Projects = new HashSet<string> { "p1" } })).Hits, "a project of another space is not found, global items are");
        Assert.HasCount(1, (await fixture.SearchAsync("release", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Doc } })).Hits);
        Assert.HasCount(2, (await fixture.SearchAsync("release", new SearchScope { ExcludedProjects = new HashSet<string> { "P1" } })).Hits);
        fixture.Host.Settings = SearchSettings.Default with { ExcludedProjects = ["p2"] };
        Assert.HasCount(2, (await fixture.SearchAsync("release")).Hits, "the excluded projects of the settings apply to every query");
        fixture.Host.Settings = SearchSettings.Default with { Enabled = false };
        Assert.IsEmpty((await fixture.SearchAsync("release")).Hits, "no index, no answer");
    }

    [TestMethod]
    public async Task Clear_EmptiesTheIndex_AndKeepsItUsable()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("docs", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "worktree cleanup")]) { Complete = true });
        await fixture.Store.ClearAsync();
        Assert.IsEmpty((await fixture.SearchAsync("worktree")).Hits);
        Assert.IsEmpty(await fixture.Store.ListSourcesAsync(""), "every source is read again");
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("doc:a", "worktree cleanup")]) { Complete = true });
        Assert.HasCount(1, (await fixture.SearchAsync("worktree")).Hits);
    }

    [TestMethod]
    public async Task DeleteContent_RemovesAProjectForAnExclusion()
    {
        await using var fixture = new SearchFixture();
        var source = new SearchSourceState("plans", "1", null, null, 0, null, DateTimeOffset.UtcNow);
        await fixture.Store.ApplyAsync(new SearchSourceUpdate(source, [Doc("plan:p1/a", "secret roadmap", SearchKind.Plan, "p1"), Doc("plan:p2/b", "public roadmap", SearchKind.Plan, "p2")]));
        Assert.AreEqual(1, await fixture.Store.DeleteContentAsync("p1", null));
        Assert.AreEqual("plan:p2/b", (await fixture.SearchAsync("roadmap")).Hits.Single().Ref);
    }
}

[TestClass]
public sealed class SessionJournalReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static JournalReadResult Read(string text, SearchSourceState? previous = null, DateTimeOffset? now = null)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        return new SessionJournalReader().Read("s1", stream, previous, new JournalContext("My session", "Alpha"), now ?? Start.AddMinutes(5), long.MaxValue, default);
    }

    [TestMethod]
    public void Read_KeepsThePromptAndTheFinalAnswer_NotSteeringNorTheMessagesBetweenToolCalls()
    {
        var journal = new JournalWriter("s1", Start)
            .User("r1", "user:1", "Fix the scroll jump of the timeline")
            .Assistant("r1", "a:1", "Looking at the timeline first.")
            .ToolOutput("r1", 100_000)
            .User("r1", "user:2", "no, the other file")
            .Assistant("r1", "a:2", "Fixed: the anchor message is kept when content arrives.")
            .Idle("r1");

        var result = Read(journal.Text);

        var messages = result.Documents.Where(static document => document.Kind == SearchKind.Message).ToList();
        CollectionAssert.AreEqual(new[] { "msg:s1/user:1", "msg:s1/a:2" }, messages.Select(static document => document.Ref).ToArray());
        Assert.AreEqual(SearchRole.User, messages[0].Role);
        Assert.AreEqual(SearchRole.Assistant, messages[1].Role);
        Assert.AreEqual("r1", messages[1].RunId);
        Assert.AreEqual("p1", messages[0].ProjectId);
        Assert.AreEqual("My session", messages[0].Title);
        Assert.AreEqual("My session · Alpha", messages[0].EmbeddingTitle);
        Assert.IsTrue(result.Documents.Any(static document => document.Ref == "session:s1" && document.Text.StartsWith("Fix the scroll", StringComparison.Ordinal)));
        Assert.IsTrue(result.ReachedEnd);
        Assert.IsNull(result.State.OpenRun);
    }

    [TestMethod]
    public void Read_ClosesATurnAtTheNextPrompt_AndAgentPromptsHaveTheirRole()
    {
        var journal = new JournalWriter("s1", Start)
            .User("r1", "user:1", "first")
            .Assistant("r1", "a:1", "first answer")
            .User("r2", "user:2", "second, from the parent", fromAgent: true);

        var result = Read(journal.Text);

        Assert.IsTrue(result.Documents.Any(static document => document.Ref == "msg:s1/a:1"), "a new prompt ends the turn before it");
        Assert.AreEqual(SearchRole.Agent, result.Documents.Single(static document => document.Ref == "msg:s1/user:2").Role);
        Assert.AreEqual("r2", result.State.OpenRun);
    }

    [TestMethod]
    public void Read_KeepsTheTurnInProgress_ThenFinishesIt_FromTheCursor()
    {
        var journal = new JournalWriter("s1", Start).User("r1", "user:1", "prompt").Assistant("r1", "a:1", "partial answer");
        var first = Read(journal.Text);
        Assert.IsFalse(first.Documents.Any(static document => document.Ref == "msg:s1/a:1"), "the answer of a turn in progress is not final yet");
        Assert.AreEqual("a:1", first.State.Answer?.ContentId);

        var cursor = new SearchSourceState("journal:s1", null, first.Fingerprint, null, first.Offset,
            System.Text.Json.JsonSerializer.Serialize(first.State, SearchJsonContext.Default.JournalReadState), Start);
        journal.Assistant("r1", "a:2", "final answer").Idle("r1");
        var second = Read(journal.Text, cursor);

        Assert.IsFalse(second.Restarted);
        CollectionAssert.AreEqual(new[] { "msg:s1/a:2" }, second.Documents.Where(static document => document.Kind == SearchKind.Message).Select(static document => document.Ref).ToArray());
    }

    [TestMethod]
    public void Read_TakesALastAnswerAsFinal_WhenItsTurnIsQuietForAnHour()
    {
        var journal = new JournalWriter("s1", Start).User("r1", "user:1", "prompt").Assistant("r1", "a:1", "the last words before the application closed");
        var result = Read(journal.Text, now: Start.AddHours(2));
        Assert.IsTrue(result.Documents.Any(static document => document.Ref == "msg:s1/a:1"));
        Assert.IsNull(result.State.OpenRun);
    }

    [TestMethod]
    public void Read_StartsAgain_WhenTheJournalWasRewritten()
    {
        var journal = new JournalWriter("s1", Start).User("r1", "user:1", "first prompt").Assistant("r1", "a:1", "answer").Idle("r1");
        var first = Read(journal.Text);
        var cursor = new SearchSourceState("journal:s1", null, first.Fingerprint, null, first.Offset, null, Start);

        var rewound = new JournalWriter("s1", Start.AddSeconds(1)).User("r9", "user:9", "another start");
        var second = Read(rewound.Text, cursor);

        Assert.IsTrue(second.Restarted);
        Assert.IsTrue(second.Documents.Any(static document => document.Ref == "msg:s1/user:9"));
    }

    [TestMethod]
    public void Read_LeavesOutTheRecordsAForkCopied()
    {
        var journal = new JournalWriter("s1", Start, countedFrom: Start.AddSeconds(25))
            .User("r1", "user:1", "copied from the source")
            .Assistant("r1", "a:1", "copied answer")
            .Idle("r1")
            .User("r2", "user:2", "the fork's own prompt");

        var refs = Read(journal.Text).Documents.Where(static document => document.Kind == SearchKind.Message).Select(static document => document.Ref).ToList();

        CollectionAssert.AreEqual(new[] { "msg:s1/user:2" }, refs);
    }

    [TestMethod]
    public void Read_KeepsTheNotes_AndStopsAtAnIncompleteLastLine()
    {
        var journal = new JournalWriter("s1", Start).Notes("## Plan\n- [x] index").User("r1", "user:1", "prompt");
        var text = journal.Text + "{\"$type\":\"contentCompleted\",\"kind\":\"User\",\"content\":\"half writ";

        var result = Read(text);

        Assert.AreEqual(SearchKind.Note, result.Documents.Single(static document => document.Ref == "note:s1").Kind);
        Assert.AreEqual(journal.Text.Length, result.Offset, "the cursor stays before the incomplete line");
        Assert.IsTrue(result.ReachedEnd);
    }
}

[TestClass]
public sealed class SearchIndexerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(-1);

    [TestMethod]
    public async Task Pass_IndexesJournals_ThenOnlyWhatIsNew()
    {
        await using var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new("s1", "Timeline work", "p1", "default", false);
        var journal = new JournalWriter("s1", Start).User("r1", "user:1", "Fix the scroll jump of the timeline").Assistant("r1", "a:1", "Kept the anchor message.").Idle("r1");
        fixture.Journals.Write(journal);

        await fixture.PassAsync();
        var hit = (await fixture.SearchAsync("scroll jump")).Hits.First();
        Assert.AreEqual("msg:s1/user:1", hit.Ref);
        Assert.AreEqual("Timeline work", hit.Title);
        Assert.AreEqual("s1", hit.SessionId);

        journal.User("r2", "user:2", "Now the fingerprint of rewritten journals").Idle("r2");
        fixture.Journals.Write(journal);
        fixture.Indexer.SignalJournal("s1");
        await fixture.Indexer.WaitIdleAsync(default);
        Assert.AreEqual("msg:s1/user:2", (await fixture.SearchAsync("fingerprint")).Hits.First().Ref);
        Assert.AreEqual(1, (await fixture.Store.ListSourcesAsync("journal:")).Count);
    }

    [TestMethod]
    public async Task Pass_RemovesDeletedSessions_ExcludedProjects_AndRenamesSessions()
    {
        await using var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new("s1", "Alpha work", "p1", "default", false);
        fixture.Host.Sessions["s2"] = new("s2", "Beta work", "p2", "default", false);
        fixture.Journals.Write(new JournalWriter("s1", Start, "p1").User("r1", "user:1", "release notes of alpha").Idle("r1"));
        fixture.Journals.Write(new JournalWriter("s2", Start, "p2").User("r1", "user:1", "release notes of beta").Idle("r1"));
        await fixture.PassAsync();
        Assert.HasCount(2, (await fixture.SearchAsync("release", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Message } })).Hits);

        fixture.Host.Settings = SearchSettings.Default with { ExcludedProjects = ["p2"] };
        fixture.Host.Sessions["s1"] = fixture.Host.Sessions["s1"] with { Title = "Renamed alpha" };
        await fixture.PassAsync();
        var hits = (await fixture.SearchAsync("release", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Message } })).Hits;
        Assert.AreEqual("s1", hits.Single().SessionId, "an excluded project is removed from the index");
        Assert.AreEqual("Renamed alpha", hits.Single().Title, "a renamed session gets its new title");
        Assert.IsGreaterThanOrEqualTo(1, (await fixture.SearchAsync("renamed")).Hits.Count(static hit => hit.SessionId == "s1"), "the new title is found by its words");

        // Let in again, the project gets its messages back at the next pass.
        fixture.Host.Settings = SearchSettings.Default;
        await fixture.PassAsync();
        Assert.HasCount(2, (await fixture.SearchAsync("release", new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Message } })).Hits);

        File.Delete(fixture.Journals.Path("s1"));
        await fixture.PassAsync();
        Assert.IsFalse((await fixture.SearchAsync("release")).Hits.Any(static hit => hit.SessionId == "s1"), "a journal that is gone takes its items with it");
    }

    [TestMethod]
    public async Task ForgetSession_TakesItsItemsAway_WithoutWaitingForAPass()
    {
        await using var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new("s1", "Alpha work", "p1", "default", false);
        fixture.Host.Sessions["s2"] = new("s2", "Beta work", "p2", "default", false);
        fixture.Journals.Write(new JournalWriter("s1", Start, "p1").User("r1", "user:1", "release notes of alpha").Idle("r1"));
        fixture.Journals.Write(new JournalWriter("s2", Start, "p2").User("r1", "user:1", "release notes of beta").Idle("r1"));
        await fixture.PassAsync();

        // A deleted session: the window forgets it as the deletion is done, before any pass.
        await fixture.Indexer.ForgetSessionAsync("s1");
        var hits = (await fixture.SearchAsync("release alpha")).Hits;
        Assert.IsFalse(hits.Any(static hit => hit.SessionId == "s1"), "nothing of the deleted session comes back");
        Assert.IsTrue((await fixture.SearchAsync("release beta")).Hits.Any(static hit => hit.SessionId == "s2"));
    }

    [TestMethod]
    public async Task Show_GivesTheTurn_AndTheTurnsAround_AndTheFullTurn()
    {
        await using var fixture = new SearchFixture();
        fixture.Host.Sessions["s1"] = new("s1", "Work", "p1", "default", false);
        fixture.Journals.Write(new JournalWriter("s1", Start)
            .User("r1", "user:1", "first").Assistant("r1", "a:1", "first answer").Idle("r1")
            .User("r2", "user:2", "second prompt about caching").Assistant("r2", "a:2a", "working").User("r2", "user:2b", "steer").Assistant("r2", "a:2", "second answer").Idle("r2")
            .User("r3", "user:3", "third").Assistant("r3", "a:3", "third answer").Idle("r3"));
        await fixture.PassAsync();

        var shown = await fixture.Service.ShowAsync("msg:s1/user:2", context: 1, full: false);
        Assert.IsNotNull(shown);
        Assert.HasCount(3, shown.Turns);
        Assert.AreEqual(1, shown.HitTurn);
        CollectionAssert.AreEqual(new[] { "second prompt about caching", "second answer" }, shown.Turns[1].Messages.Select(static message => message.Text).ToArray());
        var full = await fixture.Service.ShowAsync("msg:s1/user:2", context: 0, full: true);
        CollectionAssert.AreEqual(new[] { "second prompt about caching", "working", "steer", "second answer" }, full!.Turns.Single().Messages.Select(static message => message.Text).ToArray());
        Assert.IsNull(await fixture.Service.ShowAsync("msg:s1/none", 0, false));
    }

    [TestMethod]
    public async Task CatalogSources_AreReadAtThePass_AndCheckedBeforeAQueryOfTheirKinds()
    {
        var documents = new List<SearchDocument> { new("tool:builtin/read_file", SearchKind.Tool, "read_file", "Reads a file of the project.", DateTimeOffset.UtcNow) };
        var source = new ListSearchSource("tools", [SearchKind.Tool], _ => ValueTask.FromResult<IReadOnlyList<SearchDocument>>([.. documents]));
        await using var fixture = new SearchFixture(source);
        await fixture.PassAsync();
        Assert.HasCount(1, (await fixture.SearchAsync("file")).Hits);

        documents.Add(new("tool:builtin/web", SearchKind.Tool, "webget", "Gets a page of the web.", DateTimeOffset.UtcNow));
        // The check before a query reads a catalog source again once its last check is older than its freshness.
        var stale = new SearchIndexer(fixture.Store, fixture.Journals, fixture.Host, () => [source], TimeProvider.System, new SearchIndexerOptions { FreshnessBeforeQuery = TimeSpan.Zero });
        await stale.EnsureFreshAsync(new HashSet<SearchKind> { SearchKind.Tool }, TimeSpan.FromSeconds(5), default);
        await stale.DisposeAsync();
        Assert.HasCount(1, (await fixture.SearchAsync("web page")).Hits);
    }
}
