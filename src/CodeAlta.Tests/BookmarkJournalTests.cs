using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;

namespace CodeAlta.Tests;

/// <summary>
/// The bookmarks of messages over the journals of sessions: a message is found at the offset it was bookmarked
/// at, and found again by its run and its content identifier once the journal was rewritten around it.
/// </summary>
[TestClass]
public sealed class BookmarkJournalTests
{
    private const string Source = "0199b000-0000-7000-8000-000000000001";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00");
    private static readonly BookmarkTarget Target = new(Source, "Sample session", "project-1");

    [TestMethod]
    public async Task AMessage_IsBookmarkedAtItsOffset_AndFoundThere()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AppendTurnAsync("run-1", "First prompt\nwith a second line", ["A first answer", "The answer to keep"]);
        await fixture.AppendTurnAsync("run-2", "Second prompt", ["Another answer"]);
        var answer = await fixture.OffsetOfAsync(AgentContentKind.Assistant, "The answer to keep");
        var prompt = await fixture.OffsetOfAsync(AgentContentKind.User, "Second prompt");

        var added = await fixture.Bookmarks.AddMessageAsync(Target, answer, "Kept", createdBy: null);
        var second = await fixture.Bookmarks.AddMessageAsync(Target, prompt, null, createdBy: null);

        var bookmark = added.Bookmark!;
        Assert.AreEqual((BookmarkStatus.Ok, BookmarkRoles.Assistant, "run-1", "assistant:run-1:1", answer, "The answer to keep"),
            (added.Status, bookmark.Role, bookmark.RunId, bookmark.ContentId, bookmark.Offset!.Value, bookmark.Excerpt));
        Assert.IsNotNull(bookmark.MessageAt);
        Assert.AreEqual((BookmarkRoles.User, "run-2", "Second prompt"), (second.Bookmark!.Role, second.Bookmark.RunId, second.Bookmark.Excerpt));

        var location = await fixture.Bookmarks.ResolveAsync(bookmark.Id);
        Assert.AreEqual((BookmarkStatus.Ok, answer), (location.Status, location.Offset!.Value));
        var text = await fixture.Bookmarks.ReadTextAsync(second.Bookmark.Id, 6);
        Assert.AreEqual((BookmarkStatus.Ok, "Second", true, prompt), (text.Status, text.Text, text.Truncated, text.Offset!.Value));
        Assert.AreEqual("The answer to keep", (await fixture.Bookmarks.ReadTextAsync(bookmark.Id, 1000)).Text);
    }

    [TestMethod]
    public async Task AMessage_IsFoundAgain_AfterTheFirstLineOfTheJournalWasReplaced()
    {
        await using var fixture = await Fixture.CreateAsync(header: true);
        await fixture.AppendTurnAsync("run-1", "First prompt", ["First answer"]);
        await fixture.AppendTurnAsync("run-2", "Second prompt", ["Second answer"]);
        var answer = await fixture.OffsetOfAsync(AgentContentKind.Assistant, "First answer");
        var prompt = await fixture.OffsetOfAsync(AgentContentKind.User, "Second prompt");
        var first = (await fixture.Bookmarks.AddMessageAsync(Target, answer, null, null)).Bookmark!;
        var second = (await fixture.Bookmarks.AddMessageAsync(Target, prompt, null, null)).Bookmark!;

        // What a change of provider on an idle session does: the first line is another one, longer, and every record moved.
        var moved = await fixture.ReplaceHeaderAsync(beforeRunId: null);
        Assert.IsTrue(moved > 0);
        var changes = 0;
        fixture.Bookmarks.Changed += () => changes++;

        var location = await fixture.Bookmarks.ResolveAsync(first.Id);

        Assert.AreEqual((BookmarkStatus.Ok, answer + moved), (location.Status, location.Offset!.Value));
        Assert.AreEqual(answer + moved, await fixture.OffsetOfAsync(AgentContentKind.Assistant, "First answer"));
        Assert.AreEqual(answer + moved, (await fixture.Bookmarks.GetAsync(first.Id))!.Offset, "Where it is now is where to look first the next time.");
        Assert.AreEqual(0, changes, "The bookmark is the same for who lists it.");
        var text = await fixture.Bookmarks.ReadTextAsync(second.Id, 1000);
        Assert.AreEqual((BookmarkStatus.Ok, "Second prompt", prompt + moved), (text.Status, text.Text, text.Offset!.Value));
        // The message is still the one that is bookmarked: asking again adds nothing.
        Assert.IsFalse((await fixture.Bookmarks.AddMessageAsync(Target, answer + moved, null, null)).Created);
    }

    [TestMethod]
    public async Task AMessage_IsGone_AfterARewindRemovedItsTurn()
    {
        await using var fixture = await Fixture.CreateAsync(header: true);
        await fixture.AppendTurnAsync("run-1", "First prompt", ["First answer"]);
        await fixture.AppendTurnAsync("run-2", "Second prompt", ["Second answer"]);
        var kept = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.User, "First prompt"), null, null)).Bookmark!;
        var removed = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.Assistant, "Second answer"), null, null)).Bookmark!;

        var moved = await fixture.ReplaceHeaderAsync(beforeRunId: "run-2");

        var gone = await fixture.Bookmarks.ResolveAsync(removed.Id);
        Assert.AreEqual((BookmarkStatus.Gone, (long?)null), (gone.Status, gone.Offset));
        Assert.AreEqual(BookmarkStatus.Gone, (await fixture.Bookmarks.ReadTextAsync(removed.Id, 100)).Status);
        Assert.IsNotNull(await fixture.Bookmarks.GetAsync(removed.Id), "The bookmark stays: the user removes it.");
        var still = await fixture.Bookmarks.ResolveAsync(kept.Id);
        Assert.AreEqual((BookmarkStatus.Ok, kept.Offset!.Value + moved), (still.Status, still.Offset!.Value));

        // The turn that was removed is run again under another run: it is another message.
        await fixture.AppendTurnAsync("run-2b", "Second prompt", ["Second answer"]);
        Assert.AreEqual(BookmarkStatus.Gone, (await fixture.Bookmarks.ResolveAsync(removed.Id)).Status);
    }

    [TestMethod]
    public async Task ARecordThatIsNoMessage_IsNotBookmarkable()
    {
        await using var fixture = await Fixture.CreateAsync(header: true);
        await fixture.AppendTurnAsync("run-1", "First prompt", ["First answer"], tools: 1, reasoning: "Thinking about it");
        var records = await fixture.RecordsAsync();
        var tool = records.First(static record => record.Event is AgentActivityEvent).Offset;
        var update = records.First(static record => record.Event is AgentSessionUpdateEvent).Offset;
        var raw = records.First(static record => record.Event is AgentRawEvent).Offset;
        var reasoning = records.First(static record => record.Event is AgentContentCompletedEvent { Kind: AgentContentKind.Reasoning }).Offset;
        var prompt = records.First(static record => record.Event is AgentContentCompletedEvent { Kind: AgentContentKind.User }).Offset;

        foreach (var offset in new[] { tool, update, raw, reasoning, 0, prompt + 5, new FileInfo(fixture.JournalPath).Length, long.MaxValue })
        {
            Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await fixture.Bookmarks.AddMessageAsync(Target, offset, null, null)).Status, $"Offset {offset}");
        }

        Assert.IsEmpty(await fixture.Bookmarks.ListAsync());
        Assert.IsTrue((await fixture.Bookmarks.AddMessageAsync(Target, prompt, null, null)).Created);
    }

    [TestMethod]
    public async Task AMessageOfATurn_IsBookmarkedByTheNumberOrTheRunOfTheTurn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AppendTurnAsync("run-1", "First prompt", ["First answer"]);
        await fixture.AppendTurnAsync("run-2", "Second prompt", ["A first part", "The last word"], steer: "And one more thing");

        var prompt = await fixture.Bookmarks.AddTurnMessageAsync(Target, "2", BookmarkRoles.User, null, "agent-session");
        var answer = await fixture.Bookmarks.AddTurnMessageAsync(Target, "run-2", "Assistant", "The end", null);

        Assert.AreEqual((BookmarkStatus.Ok, "Second prompt", "run-2", "agent-session"), (prompt.Status, prompt.Bookmark!.Excerpt, prompt.Bookmark.RunId, prompt.Bookmark.CreatedBy));
        Assert.AreEqual(await fixture.OffsetOfAsync(AgentContentKind.User, "Second prompt"), prompt.Bookmark.Offset);
        Assert.AreEqual((BookmarkStatus.Ok, "The last word", "The end"), (answer.Status, answer.Bookmark!.Excerpt, answer.Bookmark.Label));
        Assert.AreEqual(await fixture.OffsetOfAsync(AgentContentKind.Assistant, "The last word"), answer.Bookmark.Offset);
        Assert.IsFalse((await fixture.Bookmarks.AddMessageAsync(Target, answer.Bookmark.Offset!.Value, null, null)).Created, "By its turn or by its offset, it is the same message.");
        Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await fixture.Bookmarks.AddTurnMessageAsync(Target, "3", BookmarkRoles.User, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await fixture.Bookmarks.AddTurnMessageAsync(Target, "run-9", BookmarkRoles.Assistant, null, null)).Status);
    }

    [TestMethod]
    public async Task AMessage_IsFoundBehindALargeRecord_InAJournalWithWindowsLineEnds_AndWithoutARun()
    {
        await using var fixture = await Fixture.CreateAsync(header: true);
        await fixture.AppendTurnAsync("run-1", "First prompt", ["Behind the large record"], toolOutput: new string('x', 700 * 1024));
        await fixture.AppendAsync(new AgentContentCompletedEvent(Fixture.Provider, Source, Start, null, AgentContentKind.Assistant, "assistant:no-run", null, "Said between two turns"));
        // What a provider does between two turns runs under a name of its own, which is the run of no turn.
        await fixture.AppendAsync(new AgentContentCompletedEvent(Fixture.Provider, Source, Start, new AgentRunId("provider-claude:remote-1"), AgentContentKind.User, "user:remote", null, "Typed from elsewhere"));
        await fixture.AppendTurnAsync("run-2", "Second prompt", ["Second answer"]);
        var text = await File.ReadAllTextAsync(fixture.JournalPath);
        await File.WriteAllTextAsync(fixture.JournalPath, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        var behind = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.Assistant, "Behind the large record"), null, null)).Bookmark!;
        var between = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.Assistant, "Said between two turns"), null, null)).Bookmark!;
        Assert.IsNull(between.RunId);
        var remote = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.User, "Typed from elsewhere"), null, null)).Bookmark!;
        Assert.AreEqual("provider-claude:remote-1", remote.RunId);

        var moved = await fixture.ReplaceHeaderAsync(beforeRunId: null);

        var found = await fixture.Store.FindContentRecordAsync(Source, "run-1", AgentContentKind.Assistant, "assistant:run-1:0");
        Assert.AreEqual((behind.Offset!.Value + moved, "Behind the large record"), (found!.Offset, ((AgentContentCompletedEvent)found.Event).Content));
        Assert.AreEqual(behind.Offset.Value + moved, (await fixture.Bookmarks.ResolveAsync(behind.Id)).Offset);
        var free = await fixture.Bookmarks.ResolveAsync(between.Id);
        Assert.AreEqual((BookmarkStatus.Ok, between.Offset!.Value + moved), (free.Status, free.Offset!.Value));
        var outside = await fixture.Bookmarks.ResolveAsync(remote.Id);
        Assert.AreEqual((BookmarkStatus.Ok, remote.Offset!.Value + moved), (outside.Status, outside.Offset!.Value));

        // The last message of a run, a run that does not exist, a message that does not, and what is not a message.
        Assert.AreEqual("assistant:run-2:0", ((AgentContentCompletedEvent)(await fixture.Store.FindContentRecordAsync(Source, "run-2", AgentContentKind.Assistant, null))!.Event).ContentId);
        Assert.IsNull(await fixture.Store.FindContentRecordAsync(Source, "run-9", AgentContentKind.Assistant, null));
        Assert.IsNull(await fixture.Store.FindContentRecordAsync(Source, "run-1", AgentContentKind.User, "assistant:run-1:0"));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Store.FindContentRecordAsync(Source, "run-1", AgentContentKind.Reasoning, "x"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Store.FindContentRecordAsync(Source, null, AgentContentKind.User, null));
    }

    [TestMethod]
    public async Task ABookmark_OfASessionWhoseJournalIsGone_SaysSo()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AppendTurnAsync("run-1", "First prompt", ["First answer"]);
        var message = (await fixture.Bookmarks.AddMessageAsync(Target, await fixture.OffsetOfAsync(AgentContentKind.User, "First prompt"), null, null)).Bookmark!;
        var session = (await fixture.Bookmarks.AddSessionAsync(Target, null, null)).Bookmark!;

        Assert.IsTrue(await fixture.Store.DeleteSessionAsync(Source));

        Assert.AreEqual(BookmarkStatus.SessionMissing, (await fixture.Bookmarks.ResolveAsync(message.Id)).Status);
        Assert.AreEqual(BookmarkStatus.SessionMissing, (await fixture.Bookmarks.ReadTextAsync(message.Id, 100)).Status);
        Assert.AreEqual(BookmarkStatus.SessionMissing, (await fixture.Bookmarks.AddMessageAsync(Target, message.Offset!.Value, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.SessionMissing, (await fixture.Bookmarks.AddTurnMessageAsync(Target, "1", BookmarkRoles.User, null, null)).Status);
        // Whether a session exists is what the host says: the bookmark of a session names no record.
        Assert.AreEqual((BookmarkStatus.Ok, (long?)null), ((await fixture.Bookmarks.ResolveAsync(session.Id)).Status, (await fixture.Bookmarks.ResolveAsync(session.Id)).Offset));
        Assert.AreEqual(BookmarkStatus.NotFound, (await fixture.Bookmarks.ResolveAsync("no-such-bookmark")).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly ModelProviderId Provider = ModelProviderIds.OpenAIResponses;
        private readonly TestTempDirectory _temp;
        private readonly ApplicationDatabase _database;
        private readonly AgentSessionSummary _session;
        private int _headers;

        private Fixture(TestTempDirectory temp, FileSystemAgentSessionStore store, AgentSessionSummary session)
        {
            (_temp, Store, _session) = (temp, store, session);
            _database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });
            Bookmarks = new BookmarkService(_database, new SessionJournalBookmarks(store));
            JournalPath = new AgentRuntimePathLayout(temp.Path).GetSessionFilePath(Source, Start);
        }

        internal FileSystemAgentSessionStore Store { get; }

        internal BookmarkService Bookmarks { get; }

        internal string JournalPath { get; }

        internal static async Task<Fixture> CreateAsync(bool header = false)
        {
            var temp = TestTempDirectory.Create();
            var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
            var session = new AgentSessionSummary
            {
                SessionId = Source,
                ProviderId = Provider,
                ProtocolFamily = "openai",
                ProviderKey = "openai",
                ModelId = "gpt-5.4",
                WorkingDirectory = "/repo/sample",
                Title = "Sample session",
                CreatedAt = Start,
                UpdatedAt = Start,
            };
            await store.UpsertSessionAsync(session);
            await store.UpsertStateAsync(new AgentSessionState { SessionId = Source, ProtocolFamily = "openai", ProviderKey = "openai", UpdatedAt = Start });
            var fixture = new Fixture(temp, store, session);
            if (header)
            {
                await File.WriteAllTextAsync(fixture.JournalPath, Header(0) + "\n" + await File.ReadAllTextAsync(fixture.JournalPath));
            }

            return fixture;
        }

        // Writes what a run writes: its prompt, what it did, the messages of the assistant, and its end.
        internal async Task AppendTurnAsync(string run, string prompt, string[] answers, int tools = 0, string? steer = null, string? reasoning = null, string? toolOutput = null)
        {
            var runId = new AgentRunId(run);
            var time = DateTimeOffset.UtcNow;
            var events = new List<AgentEvent>
            {
                new AgentSessionUpdateEvent(Provider, Source, time, runId, AgentSessionUpdateKind.ModelChanged, "Model selected"),
                new AgentRawEvent(Provider, Source, time, "local.userMessage", JsonSerializer.SerializeToElement(new { text = prompt }), runId),
                new AgentContentCompletedEvent(Provider, Source, time, runId, AgentContentKind.User, $"user:{run}", null, prompt),
            };
            for (var index = 0; index < tools; index++)
            {
                events.Add(new AgentActivityEvent(Provider, Source, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started, $"{run}-tool-{index}", null, "read_file", null));
                events.Add(new AgentActivityEvent(Provider, Source, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, $"{run}-tool-{index}", null, "read_file", null));
            }

            if (toolOutput is not null) events.Add(new AgentRawEvent(Provider, Source, time, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = toolOutput }), runId));
            if (reasoning is not null) events.Add(new AgentContentCompletedEvent(Provider, Source, time, runId, AgentContentKind.Reasoning, $"reasoning:{run}", null, reasoning));
            if (steer is not null) events.Add(new AgentContentCompletedEvent(Provider, Source, time, runId, AgentContentKind.User, $"user:{run}:steer", null, steer));
            for (var index = 0; index < answers.Length; index++)
            {
                events.Add(new AgentContentCompletedEvent(Provider, Source, time, runId, AgentContentKind.Assistant, $"assistant:{run}:{index}", null, answers[index]));
            }

            events.Add(new AgentSessionUpdateEvent(Provider, Source, time, runId, AgentSessionUpdateKind.Idle, null));
            await Store.AppendEventsAsync(_session.ProtocolFamily, _session.ProviderKey, Source, events);
        }

        internal Task AppendAsync(AgentEvent value) => Store.AppendEventsAsync(_session.ProtocolFamily, _session.ProviderKey, Source, [value]);

        // Every record of the journal with its offset, as the timeline pages read them.
        internal async Task<List<AgentSessionHistoryEntry>> RecordsAsync()
        {
            var records = new List<AgentSessionHistoryEntry>();
            AgentSessionHistoryCursor? cursor = null;
            do
            {
                var page = await Store.ReadTimelinePageAsync(Source, cursor, CancellationToken.None);
                records.InsertRange(0, page.Entries);
                cursor = page.Next;
            }
            while (cursor is not null);
            return records;
        }

        internal async Task<long> OffsetOfAsync(AgentContentKind kind, string content)
            => (await RecordsAsync()).Single(record => record.Event is AgentContentCompletedEvent message && message.Kind == kind && message.Content == content).Offset;

        // Rewinds the session to a point, or to its end, with another first line: every record that stays moves by what is returned.
        internal async Task<long> ReplaceHeaderAsync(string? beforeRunId)
        {
            var before = await FirstLineBytesAsync();
            var point = await Store.ReadBranchPointAsync(Source, beforeRunId, activeRunId: null, CancellationToken.None);
            await Store.CommitRewindAsync(point, point.Summary, point.State, Header(++_headers), [], CancellationToken.None);
            return await FirstLineBytesAsync() - before;
        }

        private async Task<long> FirstLineBytesAsync() => Array.IndexOf(await File.ReadAllBytesAsync(JournalPath), (byte)'\n') + 1;

        private static string Header(int rewrites)
            => new AgentRawEvent(Provider, Source, Start, "codealta.sessionHeader",
                JsonSerializer.SerializeToElement(new { session_id = Source, rewind_count = rewrites, provider_key = new string('p', rewrites * 37) })).ToJson();

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            _temp.Dispose();
        }
    }
}
