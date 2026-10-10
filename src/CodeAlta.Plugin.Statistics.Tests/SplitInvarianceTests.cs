using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>Reading a journal in two halves must give exactly the facts of reading it at once, wherever it is cut.</summary>
[TestClass]
public sealed class SplitInvarianceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 23, 40, 0, TimeSpan.Zero);

    /// <summary>A journal with every kind of record the reducer reads, across a quarter hour and a midnight.</summary>
    internal static JournalBuilder RichJournal()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "codex_cli");
        var parent = "44444444-4444-4444-4444-444444444444";
        b.Header(T0, parent: parent, createdByKind: "agent", createdBySession: parent, title: "A rich session")
            .State(T0.AddSeconds(1), permission: "ask", parent: parent, createdByKind: "agent")
            .LocalSnapshot(T0.AddSeconds(1.5), padding: 300)
            .SystemPrompt(T0.AddSeconds(2), "r1", 300, 900)
            // run 1: a prompt from a reminder whose provenance comes after it, tools, a failed tool, a steer, a question
            .ModelChanged(T0.AddSeconds(2), "r1", "codex_cli", "gpt-a", "High")
            .User(T0.AddSeconds(3), "r1", "remind me to check the build please", ["file"])
            .State(T0.AddSeconds(3.1), provenance: [JournalBuilder.Provenance("p-reminder", "send", T0.AddSeconds(3.1), submittedByKind: "reminder")])
            .Reasoning(T0.AddSeconds(4), "r1", "thinking about the build")
            .Usage(T0.AddSeconds(5), "r1", model: "gpt-a", input: 4000, output: 300, cached: 1000, reasoning: 120, window: 9000, limit: 200000, provider: "codex_cli")
            .Requested(T0.AddSeconds(6), "r1", "t1", "shell_command", "{\"command\":\"dotnet build\"}")
            .ToolStarted(T0.AddSeconds(6), "r1", "t1", "shell_command", "{\"command\":\"dotnet build\"}")
            .ToolOutput(T0.AddSeconds(8), "r1", "t1", "build output")
            .ToolDone(T0.AddSeconds(8.2), "r1", "t1", "shell_command", argsJson: "{\"command\":\"dotnet build\"}")
            .ToolStarted(T0.AddSeconds(9), "r1", "t2", "apply_patch", "{\"input\":\"p\"}", modifiedFiles: ["C:\\x\\a.cs"])
            .User(T0.AddSeconds(10), "r1", "also check the tests", sourceSession: parent)
            .ToolDone(T0.AddSeconds(11), "r1", "t2", "apply_patch", argsJson: "{\"input\":\"p\"}", modifiedFiles: ["C:\\x\\a.cs"], diff: "--- a\n+++ b\n+one\n+two\n-three")
            .Usage(T0.AddSeconds(12), "r1", model: "gpt-a", input: 6000, output: 200, cached: 2000, window: 15000, limit: 200000, provider: "codex_cli")
            .DiffUpdated(T0.AddSeconds(13), "r1")
            .Assistant(T0.AddSeconds(14), "r1", "the build and the tests pass")
            .Idle(T0.AddSeconds(15), "r1")
            // run 2: crosses the quarter hour and midnight, switches model, compacts, fails a tool, ends with an error
            .ModelChanged(T0.AddMinutes(10), "r2", "codex_cli", "gpt-a", "High")
            .User(T0.AddMinutes(10).AddSeconds(1), "r2", "now refactor everything", ["directory"])
            .Usage(T0.AddMinutes(11), "r2", model: "gpt-a", input: 90000, output: 900, cached: 80000, window: 120000, limit: 200000, provider: "codex_cli")
            .Usage(T0.AddMinutes(12), "r2", model: "gpt-a", input: 100000, output: 50, initiator: "compaction", provider: "codex_cli")
            .Compaction(T0.AddMinutes(12).AddSeconds(5), "r2", "threshold", 150000, 20000)
            .ModelChanged(T0.AddMinutes(13), "r2", "claude-code", "opus", "Medium")
            .Usage(T0.AddMinutes(14), "r2", model: "claude-opus-5-5", input: 3, output: 500, cacheRead: 15000, cacheWrite: 2000, cached: 15000, cost: 1.25, duration: 12345.0, provider: "claude-code")
            .ToolStarted(T0.AddMinutes(15), "r2", "t3", "mcp__srv__lookup", "{\"q\":\"x\"}")
            .ToolDone(T0.AddMinutes(15).AddSeconds(2), "r2", "t3", "mcp__srv__lookup", phase: "Failed", argsJson: "{\"q\":\"x\"}")
            .ToolStarted(T0.AddMinutes(16), "r2", "t4", "alta", "{\"args\":[\"session\",\"list\"]}")
            .Error(T0.AddMinutes(20).AddSeconds(30), "r2")
            // run 3: starts while nothing is open, steered by a queued agent prompt whose provenance came first, answered question
            .State(T0.AddMinutes(21), provenance: [JournalBuilder.Provenance("p-queued", "parent-notify", T0.AddMinutes(21).AddSeconds(5), queued: true, submittedByKind: "agent", sourceSession: parent)], queued: 1)
            .ModelChanged(T0.AddMinutes(21).AddSeconds(5.02), "r3", "claude-code", "opus", "Medium")
            .User(T0.AddMinutes(21).AddSeconds(5.05), "r3", "child report arrives")
            .User(T0.AddMinutes(21).AddSeconds(30), "r3", "yes please", answer: true)
            .LocalMessage(T0.AddMinutes(21).AddSeconds(31), "r3")
            .Usage(T0.AddMinutes(22), "r3", model: "claude-opus-5-5", input: 2, output: 80, cacheRead: 100, cached: 100, provider: "claude-code")
            .Idle(T0.AddMinutes(22).AddSeconds(2), "r3")
            // out of order and malformed records
            .Usage(T0.AddMinutes(22).AddSeconds(1), "r3", model: "claude-opus-5-5", input: 5, output: 5, provider: "claude-code")
            .Add("this line is not json")
            // run 4: never ends
            .ModelChanged(T0.AddMinutes(30), "r4", "claude-code", "opus", "Medium")
            .User(T0.AddMinutes(30).AddSeconds(1), "r4", "last prompt of the session")
            .ToolStarted(T0.AddMinutes(30).AddSeconds(2), "r4", "t5", "read_file", "{\"path\":\"p\"}");
        return b;
    }

    private static (string Facts, string Runs, SessionRow? Session, string State, long Offset) Whole(JournalBuilder journal)
    {
        var whole = SessionFactsReducerTests.CatchUp(journal);
        return (whole.Batch.ToCanonicalText(), whole.Batch.ToCanonicalRunsText(), whole.Batch.Session, whole.Cursor.State.ToJson(), whole.Cursor.Offset);
    }

    private static void AssertSplitsMatch(JournalBuilder journal, IEnumerable<int> splitPoints)
    {
        var whole = Whole(journal);
        var all = journal.ToBytes();
        foreach (var split in splitPoints)
        {
            var head = new JournalBuilder(journal.SessionId, journal.Provider);
            foreach (var line in journal.Lines.Take(split))
            {
                head.Add(line);
            }

            using var catchUp = new SessionCatchUp();
            using var firstStream = head.ToStream();
            var first = catchUp.CatchUp(journal.SessionId, firstStream, null, long.MaxValue, CancellationToken.None);
            using var fullStream = new MemoryStream(all);
            var second = catchUp.CatchUp(journal.SessionId, fullStream, first.Cursor, long.MaxValue, CancellationToken.None);

            var merged = new FactBatch(journal.SessionId);
            merged.Merge(first.Batch);
            merged.Merge(second.Batch);
            Assert.AreEqual(whole.Facts, merged.ToCanonicalText(), $"facts differ when the journal is cut after {split} lines");
            Assert.AreEqual(whole.Runs, merged.ToCanonicalRunsText(), $"runs differ when the journal is cut after {split} lines");
            Assert.AreEqual(whole.State, second.Cursor.State.ToJson(), $"state differs when the journal is cut after {split} lines");
            Assert.AreEqual(whole.Offset, second.Cursor.Offset, $"offset differs when the journal is cut after {split} lines");
            Assert.IsFalse(second.Restarted);
            AssertSessionRowsEqual(whole.Session, merged.Session, split);
        }
    }

    private static void AssertSessionRowsEqual(SessionRow? expected, SessionRow? actual, int split)
    {
        Assert.AreEqual(expected is null, actual is null, $"session row presence at {split}");
        if (expected is null || actual is null)
        {
            return;
        }

        Assert.AreEqual(expected.ProjectRef, actual.ProjectRef, $"project at {split}");
        Assert.AreEqual(expected.SessionKind, actual.SessionKind);
        Assert.AreEqual(expected.ParentSessionId, actual.ParentSessionId);
        Assert.AreEqual(expected.CreatedByKind, actual.CreatedByKind);
        Assert.AreEqual(expected.CreatedBySessionId, actual.CreatedBySessionId);
        Assert.AreEqual(expected.Title, actual.Title);
        Assert.AreEqual(expected.Provider, actual.Provider);
        Assert.AreEqual(expected.PermissionMode, actual.PermissionMode);
        Assert.AreEqual(expected.FirstRecord, actual.FirstRecord, $"first record at {split}");
        Assert.AreEqual(expected.LastRecord, actual.LastRecord, $"last record at {split}");
        Assert.AreEqual(expected.HasHeader, actual.HasHeader);
    }

    [TestMethod]
    public void RichJournal_ReadsEveryKindOfRecord()
    {
        var journal = RichJournal();

        var result = SessionFactsReducerTests.CatchUp(journal);

        Assert.AreEqual(1, result.Scan.UnknownLines, "the line that is not JSON");
        Assert.AreEqual(0, result.Scan.MalformedLines);
        var batch = result.Batch;
        Assert.AreEqual(4, batch.Runs.Count);
        Assert.AreEqual(RunOutcome.Completed, batch.Runs["r1"].Outcome);
        Assert.AreEqual(RunOutcome.Failed, batch.Runs["r2"].Outcome);
        Assert.AreEqual(RunOutcome.Completed, batch.Runs["r3"].Outcome);
        Assert.AreEqual(RunOutcome.Running, batch.Runs["r4"].Outcome);
        Assert.AreEqual(PromptSender.Reminder, batch.Runs["r1"].Sender);
        Assert.AreEqual(PromptSender.Agent, batch.Runs["r3"].Sender);
        Assert.AreEqual(PromptKind.Queued, batch.Runs["r3"].PromptKind);
        Assert.IsTrue(batch.Content.Any(static pair => pair.Key is { Kind: ContentKind.Prompt, PromptKind: PromptKind.Answer }));
        Assert.IsTrue(batch.Content.Any(static pair => pair.Key is { Kind: ContentKind.Prompt, PromptKind: PromptKind.Steer }));
        Assert.IsTrue(batch.Usage.Keys.Any(static key => key.Purpose == UsagePurpose.Compaction));
        Assert.IsTrue(batch.Cost.Count == 1);
        Assert.IsTrue(batch.Activity.Keys.Select(static key => key.Quarter.Start.UtcDateTime.Date).Distinct().Count() == 2, "the journal crosses midnight");
        CollectionAssert.AreEquivalent(new[] { "t4", "t5" }, result.Cursor.State.OpenTools.Select(static tool => tool.ActivityId).ToArray());
    }

    [TestMethod]
    public void SplittingASyntheticJournalAtEveryRecordBoundaryGivesTheSameFacts()
    {
        var journal = RichJournal();

        AssertSplitsMatch(journal, Enumerable.Range(0, journal.Lines.Count + 1));
    }

    [TestMethod]
    public void ReadingInSmallSlicesGivesTheSameFactsAsReadingAtOnce()
    {
        var journal = RichJournal();
        var whole = Whole(journal);

        // A slice of a few hundred bytes: many catch-ups in a row, each resuming from the cursor of the one before.
        using var catchUp = new SessionCatchUp();
        using var stream = journal.ToStream();
        JournalCursor? cursor = null;
        var merged = new FactBatch(journal.SessionId);
        var slices = 0;
        while (true)
        {
            var result = catchUp.CatchUp(journal.SessionId, stream, cursor, 700, CancellationToken.None);
            merged.Merge(result.Batch);
            cursor = result.Cursor;
            slices++;
            if (result.ReachedEnd)
            {
                break;
            }
        }

        Assert.IsTrue(slices > 5);
        Assert.AreEqual(whole.Facts, merged.ToCanonicalText());
        Assert.AreEqual(whole.Runs, merged.ToCanonicalRunsText());
        Assert.AreEqual(whole.State, cursor!.State.ToJson());
    }

    [TestMethod]
    public void ReadingWhileTheSessionWrites_ASliceAtATimeGivesTheSameFacts()
    {
        var journal = RichJournal();
        var whole = Whole(journal);
        var lines = journal.Lines;

        // The session appends records between the reads, and a read always finds the file as it was at that moment.
        using var catchUp = new SessionCatchUp();
        var merged = new FactBatch(journal.SessionId);
        JournalCursor? cursor = null;
        for (var count = 0; count <= lines.Count; count += 3)
        {
            var written = new JournalBuilder(journal.SessionId, journal.Provider);
            foreach (var line in lines.Take(Math.Min(count + 3, lines.Count)))
            {
                written.Add(line);
            }

            using var stream = written.ToStream();
            var result = catchUp.CatchUp(journal.SessionId, stream, cursor, long.MaxValue, CancellationToken.None);
            merged.Merge(result.Batch);
            cursor = result.Cursor;
        }

        Assert.AreEqual(whole.Facts, merged.ToCanonicalText());
        Assert.AreEqual(whole.Runs, merged.ToCanonicalRunsText());
        Assert.AreEqual(whole.State, cursor!.State.ToJson());
    }

    [TestMethod]
    public void SplitsThroughTheStateJsonGiveTheSameFacts()
    {
        var journal = RichJournal();
        var whole = Whole(journal);
        var all = journal.ToBytes();
        var split = journal.Lines.Count / 2;
        var head = new JournalBuilder(journal.SessionId, journal.Provider);
        foreach (var line in journal.Lines.Take(split))
        {
            head.Add(line);
        }

        using var catchUp = new SessionCatchUp();
        using var firstStream = head.ToStream();
        var first = catchUp.CatchUp(journal.SessionId, firstStream, null, long.MaxValue, CancellationToken.None);

        // What the store keeps: the offset, the mark of the first line, and the state as JSON text.
        var saved = new JournalCursor(first.Cursor.Offset, first.Cursor.FirstLine, SessionFactsState.FromJson(first.Cursor.State.ToJson()));
        using var fullStream = new MemoryStream(all);
        var second = catchUp.CatchUp(journal.SessionId, fullStream, saved, long.MaxValue, CancellationToken.None);

        var merged = new FactBatch(journal.SessionId);
        merged.Merge(first.Batch);
        merged.Merge(second.Batch);
        Assert.AreEqual(whole.Facts, merged.ToCanonicalText());
        Assert.AreEqual(whole.State, second.Cursor.State.ToJson());
    }
    private sealed class CancelingSink(IJournalRecordSink inner, CancellationTokenSource source, int after) : IJournalRecordSink
    {
        private int _count;

        public void OnRecord(JournalRecord record)
        {
            inner.OnRecord(record);
            if (++_count == after)
            {
                source.Cancel();
            }
        }

        public bool IsPromptSeen(ulong idHash) => inner.IsPromptSeen(idHash);
    }

    [TestMethod]
    public void ACanceledReadLeavesACursorThatResumesToTheSameFacts()
    {
        // A long session: the rich journal repeated with new run identifiers, so that a read has hundreds of lines to cancel in.
        var journal = new JournalBuilder("33333333-3333-3333-3333-333333333333", "codex");
        for (var round = 0; round < 40; round++)
        {
            var start = T0.AddMinutes(round * 3);
            var run = $"run-{round}";
            journal.ModelChanged(start, run, "codex", "gpt-a", "High")
                .User(start.AddSeconds(1), run, "a prompt of this round")
                .Usage(start.AddSeconds(5), run, model: "gpt-a", input: 1000 + round, output: 10, provider: "codex")
                .ToolStarted(start.AddSeconds(6), run, $"t{round}", "read_file", "{\"path\":\"p\"}")
                .ToolDone(start.AddSeconds(7), run, $"t{round}", "read_file", readFiles: ["p"])
                .LocalSnapshot(start.AddSeconds(8), padding: 50)
                .Assistant(start.AddSeconds(9), run, "done")
                .Idle(start.AddSeconds(10), run);
        }

        var whole = Whole(journal);
        using var source = new CancellationTokenSource();
        var reducer = new SessionFactsReducer(journal.SessionId);
        using var scanner = new JournalScanner();
        using var stream = journal.ToStream();
        var first = scanner.Scan(stream, 0, null, new CancelingSink(reducer, source, 150), long.MaxValue, source.Token);
        Assert.IsTrue(first.Canceled);
        Assert.IsFalse(first.ReachedEnd);
        var head = reducer.TakeBatch();
        var cursor = new JournalCursor(first.EndOffset, first.FirstLine, reducer.State);

        using var catchUp = new SessionCatchUp();
        var second = catchUp.CatchUp(journal.SessionId, stream, cursor, long.MaxValue, CancellationToken.None);
        var merged = new FactBatch(journal.SessionId);
        merged.Merge(head);
        merged.Merge(second.Batch);

        Assert.IsTrue(first.EndOffset > 0 && first.EndOffset < journal.ToBytes().Length);
        Assert.AreEqual(whole.Facts, merged.ToCanonicalText());
        Assert.AreEqual(whole.Runs, merged.ToCanonicalRunsText());
        Assert.AreEqual(whole.State, second.Cursor.State.ToJson());
    }
}
