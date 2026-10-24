using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

/// <summary>
/// The session index of the application database (<c>session_journal_index</c>): the notes, the turns and the usage of a
/// session are read from it, never from the journal, and it stays what a read of the whole journal finds.
/// </summary>
[TestClass]
public sealed class SessionJournalIndexTests
{
    private const string Source = "0199b000-0000-7000-8000-000000000001";
    private const string Target = "0199b000-0000-7000-8000-000000000002";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    [TestMethod]
    public async Task NotesAfterARestart_AreReadFromTheIndex_NeverFromTheJournal()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1").ConfigureAwait(false);
        await store.AppendNotesAsync(Notes(session, "first"), () => Task.CompletedTask).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2").ConfigureAwait(false);
        // Notes are also written as an event of a run.
        await store.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, Source, [Notes(session, "second")]).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-3").ConfigureAwait(false);

        // As at a start of the application: nothing is remembered in memory, and no journal can be opened.
        using (fixture.LockJournals())
        {
            var fresh = fixture.CreateStore();
            Assert.AreEqual("second", (await fresh.ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
            Assert.AreEqual("second", (await fixture.CreateStore().ReadLatestNotesContainedAsync(Source).ConfigureAwait(false))!.Markdown);
        }
    }

    [TestMethod]
    public async Task TurnsAndUsageAfterARestart_AreReadFromTheIndex_NeverFromTheJournal()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        for (var run = 1; run <= 4; run++)
        {
            await AppendTurnAsync(store, session, $"run-{run}", tools: run).ConfigureAwait(false);
        }

        await fixture.Journals.AppendStateAsync(fixture.Descriptor, new SessionViewLocalState { ModelId = "fixture-model-2" }).ConfigureAwait(false);
        var expected = await ReadWithoutIndexAsync(fixture).ConfigureAwait(false);

        using (fixture.LockJournals())
        {
            var fresh = fixture.CreateStore();
            var turns = await fresh.ReadTurnsAsync(Source).ConfigureAwait(false);
            CollectionAssert.AreEqual(expected.Turns.Turns.ToArray(), turns.Turns.ToArray());
            Assert.AreEqual(expected.Turns.EndOffset, turns.EndOffset);
            Assert.AreEqual(expected.Turns.Revision, turns.Revision);
            Assert.AreEqual(expected.HistoryCount, turns.HistoryCount);
            CollectionAssert.AreEqual(Json(expected.Usage), Json(await fresh.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)));
        }
    }

    [TestMethod]
    public async Task ForkPointAndMessageLookup_ReadOnlyTheRecordsOfTheTurnTheyNeed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A range of a file is locked against the reads of other handles on Windows only.");
        }

        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        for (var run = 1; run <= 3; run++)
        {
            await AppendTurnAsync(store, session, $"run-{run}", toolOutput: new string('o', 64 * 1024)).ConfigureAwait(false);
        }

        var turns = (await ReadWithoutIndexAsync(fixture).ConfigureAwait(false)).Turns.Turns;
        var fresh = fixture.CreateStore();

        // The point before the third turn needs the state written in the second one and the bytes that end it: the
        // first turn is never read.
        using (fixture.LockRange(turns[0].StartOffset, turns[0].EndOffset - turns[0].StartOffset))
        {
            var point = await fresh.ReadBranchPointAsync(Source, "run-3", activeRunId: null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, point.KeptTurnCount);
            Assert.AreEqual(turns[2].StartOffset, point.CutOffset);
            Assert.AreEqual("resp-run-2", point.State.ProviderSessionId, "The state of the point is the last one written before it.");
        }

        // The answer of the third turn is looked for in that turn alone.
        using (fixture.LockRange(0, turns[2].StartOffset))
        {
            var found = await fresh.FindContentRecordAsync(Source, "run-3", AgentContentKind.Assistant, contentId: null).ConfigureAwait(false);
            Assert.AreEqual("Answer to run-3", ((AgentContentCompletedEvent)found!.Event).Content);
        }
    }

    [TestMethod]
    public async Task IndexOfAJournalAnotherWriterAppendedTo_ReadsOnlyWhatFollowsItsEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A range of a file is locked against the reads of other handles on Windows only.");
        }

        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", toolOutput: new string('o', 64 * 1024)).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2").ConfigureAwait(false);
        var indexedLength = new FileInfo(fixture.JournalPath).Length;

        // Another program appends a turn: the index does not cover the journal any more.
        var other = new FileSystemAgentSessionStore(fixture.Layout);
        await AppendTurnAsync(other, session, "run-3").ConfigureAwait(false);
        await other.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, Source, [Notes(session, "from elsewhere")]).ConfigureAwait(false);
        var expected = await ReadWithoutIndexAsync(fixture).ConfigureAwait(false);
        // The list of sessions takes the changed journal (its first and last records); the index is not part of it.
        Assert.HasCount(1, await fixture.CreateStore().ListSessionsAsync().ToArrayAsync().ConfigureAwait(false));

        // What the index read is not read again, but for the bytes that end it, which tell it is the same journal.
        using (fixture.LockRange(0, indexedLength - AgentJournalIndexer.GuardBytes))
        {
            var fresh = fixture.CreateStore();
            var turns = await fresh.ReadTurnsAsync(Source).ConfigureAwait(false);
            CollectionAssert.AreEqual(expected.Turns.Turns.ToArray(), turns.Turns.ToArray());
            Assert.AreEqual("from elsewhere", (await fresh.ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
        }

        // It was written back: the next read reads no journal.
        using (fixture.LockJournals())
        {
            Assert.AreEqual(3, (await fixture.CreateStore().ReadTurnsAsync(Source).ConfigureAwait(false)).Turns.Count);
        }
    }

    [TestMethod]
    public async Task IndexAfterAppendsRewindAndFork_IsWhatAReadOfTheWholeJournalFinds()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        for (var run = 1; run <= 4; run++)
        {
            await AppendTurnAsync(store, session, $"run-{run}", tools: 1, steer: run == 2 ? "and more" : null).ConfigureAwait(false);
        }

        await store.AppendNotesAsync(Notes(session, "kept"), () => Task.CompletedTask).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-5").ConfigureAwait(false);
        await store.AppendNotesAsync(Notes(session, "rewound away"), () => Task.CompletedTask).ConfigureAwait(false);
        await AssertIndexedAsync(fixture, Source, "rewound away").ConfigureAwait(false);

        // A rewind before the fifth turn removes it and the notes written after it.
        var point = await store.ReadBranchPointAsync(Source, "run-5", activeRunId: null, CancellationToken.None).ConfigureAwait(false);
        var now = Start.AddDays(1);
        await store.CommitRewindAsync(point, point.Summary with { UpdatedAt = now, RewindCount = 1 }, point.State with { UpdatedAt = now }, header: null, [],
            CancellationToken.None).ConfigureAwait(false);
        await AssertIndexedAsync(fixture, Source, "kept").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-6").ConfigureAwait(false);
        await AssertIndexedAsync(fixture, Source, "kept").ConfigureAwait(false);

        // A fork before the third turn: a journal of its own, with offsets of its own.
        point = await store.ReadBranchPointAsync(Source, "run-3", activeRunId: null, CancellationToken.None).ConfigureAwait(false);
        var created = Start.AddDays(2);
        await store.CommitForkAsync(point, point.Summary with { SessionId = Target, CreatedAt = created, UpdatedAt = created },
            point.State with { SessionId = Target, UpdatedAt = created }, header: null, [], includeRemainder: false, CancellationToken.None).ConfigureAwait(false);
        await AssertIndexedAsync(fixture, Target, notes: null).ConfigureAwait(false);
        Assert.AreEqual(2, (await fixture.CreateStore().ReadTurnsAsync(Target).ConfigureAwait(false)).Turns.Count);
    }

    [TestMethod]
    public async Task IndexWithoutItsTables_IsFilledOnceFromTheJournals_ThenReadWithoutThem()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = new Fixture(temp.Path);
        var store = fixture.CreateStore();
        var session = await CreateSessionAsync(fixture, store).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1").ConfigureAwait(false);
        await store.AppendNotesAsync(Notes(session, "noted"), () => Task.CompletedTask).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2").ConfigureAwait(false);

        // A database of a build before the index: its tables are missing.
        var dropped = await fixture.Journals.ApplicationDatabase.DropTablesAsync("session_journal_index", "session_journal_index")
            .ConfigureAwait(false);
        CollectionAssert.Contains(dropped.ToArray(), "session_journal_index");

        // The first read reads the journal once and writes the index back; the reads after it read no journal.
        var expected = await ReadWithoutIndexAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual("noted", (await new Fixture(temp.Path).CreateStore().ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
        using (fixture.LockJournals())
        {
            var fresh = new Fixture(temp.Path).CreateStore();
            Assert.AreEqual("noted", (await fresh.ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
            CollectionAssert.AreEqual(expected.Turns.Turns.ToArray(), (await fresh.ReadTurnsAsync(Source).ConfigureAwait(false)).Turns.ToArray());
            CollectionAssert.AreEqual(Json(expected.Usage), Json(await fresh.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)));
        }
    }

    // The index of a session is what a read of its whole journal finds, and is read without the journal.
    private static async Task AssertIndexedAsync(Fixture fixture, string sessionId, string? notes)
    {
        var whole = new FileSystemAgentSessionStore(fixture.Layout);
        var expectedTurns = await whole.ReadTurnsAsync(sessionId).ConfigureAwait(false);
        var expectedUsage = await whole.ReadUsageUpdatesAsync(sessionId).ConfigureAwait(false);
        var expectedNotes = await whole.ReadLatestNotesAsync(sessionId).ConfigureAwait(false);
        var historyCount = (await whole.ReadEventsAsync(sessionId).ConfigureAwait(false)).Count;
        Assert.AreEqual(notes, expectedNotes?.Markdown);
        Assert.AreEqual(historyCount, expectedTurns.HistoryCount);
        using (fixture.LockJournals())
        {
            var fresh = fixture.CreateStore();
            var turns = await fresh.ReadTurnsAsync(sessionId).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectedTurns.Turns.ToArray(), turns.Turns.ToArray());
            Assert.AreEqual(expectedTurns.EndOffset, turns.EndOffset);
            Assert.AreEqual(expectedTurns.Revision, turns.Revision);
            Assert.AreEqual(historyCount, turns.HistoryCount);
            CollectionAssert.AreEqual(Json(expectedUsage), Json(await fresh.ReadUsageUpdatesAsync(sessionId).ConfigureAwait(false)));
            Assert.AreEqual(notes, (await fresh.ReadLatestNotesAsync(sessionId).ConfigureAwait(false))?.Markdown);
        }
    }

    // What a store without an index reads of the whole journal.
    private static async Task<(AgentSessionTurns Turns, IReadOnlyList<AgentSessionUsageUpdate> Usage, int HistoryCount)> ReadWithoutIndexAsync(Fixture fixture)
    {
        var whole = new FileSystemAgentSessionStore(fixture.Layout);
        return (await whole.ReadTurnsAsync(Source).ConfigureAwait(false), await whole.ReadUsageUpdatesAsync(Source).ConfigureAwait(false),
            (await whole.ReadEventsAsync(Source).ConfigureAwait(false)).Count);
    }

    private static string[] Json(IEnumerable<AgentSessionUsageUpdate> usage)
        => usage.Select(static update => update.RunId + " " + JsonSerializer.Serialize(update.Operation)).ToArray();

    private static AgentNotesEvent Notes(AgentSessionSummary session, string markdown)
        => new(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, AgentNotesUpdateKind.Set, markdown);

    private static async Task<AgentSessionSummary> CreateSessionAsync(Fixture fixture, FileSystemAgentSessionStore store)
    {
        await fixture.Journals.EnsureHeaderAsync(fixture.Descriptor).ConfigureAwait(false);
        var session = new AgentSessionSummary
        {
            SessionId = Source,
            ProviderId = new ModelProviderId("fixture"),
            ProtocolFamily = "openai",
            ProviderKey = "fixture",
            ModelId = "fixture-model",
            WorkingDirectory = fixture.Root,
            Title = "Indexed session",
            CreatedAt = Start,
            UpdatedAt = Start,
        };
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState
        {
            SessionId = Source,
            ProtocolFamily = session.ProtocolFamily,
            ProviderKey = session.ProviderKey,
            UpdatedAt = Start,
        }).ConfigureAwait(false);
        return session;
    }

    // Writes what a run writes: the prompt, a state, tools with their usage, the answer and the end.
    private static async Task AppendTurnAsync(FileSystemAgentSessionStore store, AgentSessionSummary session, string run, int tools = 0,
        string? steer = null, string? toolOutput = null)
    {
        var provider = session.ProviderId;
        var id = session.SessionId;
        var runId = new AgentRunId(run);
        var time = DateTimeOffset.UtcNow;
        await store.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, id,
        [
            new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.User, $"user:{Guid.CreateVersion7()}", null, "Prompt of " + run),
        ]).ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState
        {
            SessionId = id,
            ProtocolFamily = session.ProtocolFamily,
            ProviderKey = session.ProviderKey,
            ProviderSessionId = "resp-" + run,
            UpdatedAt = time,
        }).ConfigureAwait(false);
        var events = new List<AgentEvent>();
        for (var index = 0; index < tools; index++)
        {
            events.Add(new AgentActivityEvent(provider, id, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started, $"{run}-tool-{index}", null, "read_file", null));
            events.Add(new AgentSessionUpdateEvent(provider, id, time, runId, AgentSessionUpdateKind.UsageUpdated, "usage", Usage: new AgentSessionUsage(
                LastOperation: new AgentOperationUsageSnapshot(Model: "fixture-model", InputTokens: 100 + index, OutputTokens: 10, Cost: 0.25, CostUnit: "USD"),
                Scope: AgentUsageScope.CurrentWindow, Source: AgentUsageSource.ProviderUsage, UpdatedAt: time)));
        }

        if (toolOutput is not null)
        {
            events.Add(new AgentRawEvent(provider, id, time, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = toolOutput }), runId));
        }

        if (steer is not null)
        {
            events.Add(new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.User, $"user:{Guid.CreateVersion7()}", null, steer));
        }

        events.Add(new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.Assistant, $"assistant:{run}", null, "Answer to " + run));
        events.Add(new AgentSessionUpdateEvent(provider, id, time, runId, AgentSessionUpdateKind.Idle, null));
        await store.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, id, events).ConfigureAwait(false);
    }

    private sealed class Fixture
    {
        internal Fixture(string root)
        {
            Root = root;
            Options = new CatalogOptions { GlobalRoot = root };
            Layout = new AgentRuntimePathLayout(Options.StateRoot);
            Journals = new SessionViewJournalStore(Options);
            Descriptor = new SessionViewDescriptor
            {
                SessionId = Source, Kind = SessionViewKind.GlobalSession, ProviderId = "fixture", ProviderKey = "fixture",
                WorkingDirectory = root, Title = "Indexed session", CreatedAt = Start, UpdatedAt = Start,
            };
        }

        internal string Root { get; }

        internal CatalogOptions Options { get; }

        internal AgentRuntimePathLayout Layout { get; }

        internal SessionViewJournalStore Journals { get; }

        internal SessionViewDescriptor Descriptor { get; }

        internal string JournalPath => Layout.GetSessionFilePath(Source, Start);

        // A store of a new start of the application: it shares nothing in memory with the others, only the database.
        internal FileSystemAgentSessionStore CreateStore() => new SessionViewJournalStore(Options).CreateSessionStore();

        // Holds every journal open without sharing, as long as it is not disposed.
        internal IDisposable LockJournals()
        {
            var locks = Directory.EnumerateFiles(Layout.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories)
                .Select(static journal => new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
                .ToList();
            Assert.IsNotEmpty(locks);
            return new Locks(locks);
        }

        // Locks a range of the journal of the session against the reads of other handles (Windows).
        internal IDisposable LockRange(long offset, long length)
        {
            var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (OperatingSystem.IsWindows())
            {
                stream.Lock(offset, length);
            }

            return new Locks([stream]);
        }

        private sealed class Locks(List<FileStream> streams) : IDisposable
        {
            public void Dispose()
            {
                foreach (var stream in streams)
                {
                    stream.Dispose();
                }
            }
        }
    }
}
