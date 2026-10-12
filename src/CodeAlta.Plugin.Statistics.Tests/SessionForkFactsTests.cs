using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// A session that starts with the turns of another one (a fork, or the session that keeps what a rewind removed) holds a copy of
/// their records. They were counted in the source session: the new session counts its own records only.
/// </summary>
[TestClass]
public sealed class SessionForkFactsTests
{
    private const string SourceId = "33333333-3333-3333-3333-333333333333";
    private const string ForkId = "55555555-5555-5555-5555-555555555555";
    private const string KeptId = "66666666-6666-6666-6666-666666666666";

    private static DateTimeOffset Time(int minute, double seconds = 0) => JournalBuilder.Time(minute, seconds);

    // A turn as a session writes it: the model, a prompt, a request with a cost, a tool call that changes a file, an answer, the
    // end, and the snapshots that follow a turn.
    private static JournalBuilder Turn(JournalBuilder journal, string run, int minute, long input, double cost)
        => journal.ModelChanged(Time(minute, 2), run, "codex", "gpt-6.1-sol")
            .User(Time(minute, 3), run, "a prompt of some words")
            .Usage(Time(minute, 5), run, input: input, output: input / 10, cost: cost, duration: input)
            .ToolStarted(Time(minute, 6), run, run + "-t", "apply_patch", "{\"input\":\"p\"}", modifiedFiles: ["C:\\x\\a.cs"])
            .ToolDone(Time(minute, 8), run, run + "-t", "apply_patch", argsJson: "{\"input\":\"p\"}", modifiedFiles: ["C:\\x\\a.cs"], diff: "--- a\n+++ b\n+one\n+two\n-three")
            .Assistant(Time(minute, 10), run, "an answer of five words")
            .Idle(Time(minute, 20), run)
            .LocalSnapshot(Time(minute, 21))
            .State(Time(minute, 21));

    private static JournalBuilder Source()
    {
        var journal = new JournalBuilder(SourceId);
        journal.Header(Time(0), title: "A source").State(Time(0, 1));
        Turn(journal, "r1", 0, 1000, 0.25);
        Turn(journal, "r2", 10, 2000, 0.5);
        return Turn(journal, "r3", 20, 4000, 1);
    }

    // What a session did, as the numbers of the statistics say it.
    private sealed record Spent(
        long Runs, long RunsStarted, long RunsCompleted, long ActiveMs, long Requests, long InputTokens, long OutputTokens, double Cost,
        long ToolCalls, long LinesAdded, long LinesRemoved, long FilesChanged, long Prompts, long PromptChars, long Answers, long AnswerWords)
    {
        public static Spent Of(FactBatch batch) => new(
            batch.Runs.Count,
            batch.Activity.Values.Sum(static value => (long)value.RunsStarted),
            batch.Activity.Values.Sum(static value => (long)value.RunsCompleted),
            batch.Activity.Values.Sum(static value => value.ActiveMs),
            batch.Usage.Values.Sum(static value => value.Requests),
            batch.Usage.Values.Sum(static value => value.InputTokens),
            batch.Usage.Values.Sum(static value => value.OutputTokens),
            batch.Cost.Values.Sum(static value => value.Total),
            batch.Tools.Values.Sum(static value => value.Calls),
            batch.Tools.Values.Sum(static value => value.LinesAdded),
            batch.Tools.Values.Sum(static value => value.LinesRemoved),
            batch.Tools.Values.Sum(static value => value.FilesChanged),
            batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt).Sum(static pair => pair.Value.Count),
            batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt).Sum(static pair => pair.Value.Chars),
            batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Answer).Sum(static pair => pair.Value.Count),
            batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Answer).Sum(static pair => pair.Value.Words));

        public Spent Plus(Spent other) => new(
            Runs + other.Runs, RunsStarted + other.RunsStarted, RunsCompleted + other.RunsCompleted, ActiveMs + other.ActiveMs,
            Requests + other.Requests, InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, Cost + other.Cost,
            ToolCalls + other.ToolCalls, LinesAdded + other.LinesAdded, LinesRemoved + other.LinesRemoved, FilesChanged + other.FilesChanged,
            Prompts + other.Prompts, PromptChars + other.PromptChars, Answers + other.Answers, AnswerWords + other.AnswerWords);
    }

    [TestMethod]
    public void AFork_CountsOnlyItsOwnTurns()
    {
        var source = Source();
        // Forked before the third turn, twenty minutes after it: the fork starts with the first two turns, then has one of its own.
        var fork = source.ForkTo(ForkId, source.FirstLineOf("r3"), Time(40), Time(40));
        Turn(fork, "f1", 45, 8000, 2);
        // The same session without what it inherited: what the fork itself did.
        var own = new JournalBuilder(ForkId);
        own.Header(Time(40), title: "A fork").LocalSnapshot(Time(40)).State(Time(40));
        Turn(own, "f1", 45, 8000, 2);

        var result = SessionFactsReducerTests.CatchUp(fork);
        var expected = SessionFactsReducerTests.Reduce(own);

        var batch = result.Batch;
        CollectionAssert.AreEqual(new[] { "f1" }, batch.Runs.Keys.ToArray(), "The runs of the source are not runs of the fork.");
        Assert.AreEqual(expected.ToCanonicalText(), batch.ToCanonicalText());
        Assert.AreEqual(expected.ToCanonicalRunsText(), batch.ToCanonicalRunsText());
        Assert.AreEqual(new Spent(1, 1, 1, 18_000, 1, 8000, 800, 2, 1, 2, 1, 1, 1, 22, 1, 5), Spent.Of(batch));
        // The session is the fork: it starts when it was created, and the kind of its lineage is not its kind.
        var session = batch.Session!;
        Assert.AreEqual(Time(40), session.FirstRecord);
        Assert.AreEqual(Time(45, 21), session.LastRecord);
        Assert.AreEqual("A fork", session.Title);
        Assert.AreEqual("ProjectSession", session.SessionKind);
        Assert.IsTrue(session.HasHeader);
        Assert.AreEqual(1, batch.Details.Where(static pair => pair.Key.List == DetailList.SessionOrigin).Sum(static pair => pair.Value), "The fork is one session more.");
        Assert.AreEqual(Time(40), result.Cursor.State.Inherited!.CountedFrom);
        Assert.AreEqual(0, result.Scan.MalformedLines);
    }

    [TestMethod]
    public void TheTotalsOfASourceAndItsFork_AreWhatWasSpent()
    {
        var source = Source();
        var fork = source.ForkTo(ForkId, source.FirstLineOf("r3"), Time(40), Time(40));
        Turn(fork, "f1", 45, 8000, 2);
        // The source goes on after the fork.
        Turn(source, "r4", 50, 16000, 4);

        var total = Spent.Of(SessionFactsReducerTests.Reduce(source)).Plus(Spent.Of(SessionFactsReducerTests.Reduce(fork)));

        // Five turns were made: four in the source, one in the fork.
        Assert.AreEqual(new Spent(5, 5, 5, 90_000, 5, 31_000, 3100, 7.75, 5, 10, 5, 5, 5, 110, 5, 25), total);
    }

    [TestMethod]
    public void TheSessionThatKeepsARewind_CountsTheRemovedTurns_AndTheRewoundSessionTheOthers()
    {
        var source = Source();
        var before = Spent.Of(SessionFactsReducerTests.Reduce(source));
        // Rewound to before its second turn an hour later: a new session keeps every turn, and counts from the first removed one.
        var removedFrom = Time(10, 2);
        var kept = source.ForkTo(KeptId, source.Lines.Count, Time(60), removedFrom, kind: "Rewind");
        var rewound = source.RewindTo(source.FirstLineOf("r2"), 1, Time(60));

        var keptResult = SessionFactsReducerTests.CatchUp(kept);
        var rewoundBatch = SessionFactsReducerTests.Reduce(rewound);

        CollectionAssert.AreEqual(new[] { "r2", "r3" }, keptResult.Batch.Runs.Keys.Order().ToArray());
        CollectionAssert.AreEqual(new[] { "r1" }, rewoundBatch.Runs.Keys.ToArray());
        Assert.AreEqual(new Spent(2, 2, 2, 36_000, 2, 6000, 600, 1.5, 2, 4, 2, 2, 2, 44, 2, 10), Spent.Of(keptResult.Batch));
        Assert.AreEqual(before, Spent.Of(keptResult.Batch).Plus(Spent.Of(rewoundBatch)), "Nothing is counted twice and nothing is lost.");
        // The removed turns keep their times: the session that holds them was active when they were made.
        Assert.AreEqual(removedFrom, keptResult.Batch.Session!.FirstRecord);
        Assert.AreEqual(Time(60), keptResult.Batch.Session.LastRecord);
        Assert.AreEqual(Time(10, 2), keptResult.Batch.Runs["r2"].Start);
        Assert.AreEqual("A source", rewoundBatch.Session!.Title);
    }

    [TestMethod]
    public void TheChoicesInForce_AreFollowedThroughTheInheritedRecords()
    {
        // The source chose an alias, and its provider reported the model: the turn after counts under the reported name.
        var source = new JournalBuilder(SourceId, "claude-code");
        source.Header(Time(0)).State(Time(0, 1), model: "opus", effort: "Medium")
            .ModelChanged(Time(0, 2), "r1", "claude-code", "opus", "Medium", agentPrompt: "plan")
            .User(Time(0, 3), "r1")
            .Usage(Time(0, 5), "r1", model: "claude-opus-5-5", provider: "claude-code")
            .Idle(Time(0, 20), "r1")
            // A turn that does not say its model again: it runs with the one in force.
            .User(Time(10, 3), "r2")
            .Usage(Time(10, 5), "r2", model: "claude-opus-5-5", provider: "claude-code")
            .Idle(Time(10, 20), "r2");
        var inSource = SessionFactsReducerTests.Reduce(source).Runs["r2"];

        // The session that keeps a rewind to before the second turn counts it as the source did.
        var kept = source.ForkTo(KeptId, source.Lines.Count, Time(60), Time(10, 3), kind: "Rewind", model: "opus");
        var removed = SessionFactsReducerTests.Reduce(kept).Runs["r2"];
        Assert.AreEqual("claude-opus-5-5", removed.Model);
        Assert.AreEqual((inSource.Provider, inSource.Model, inSource.Effort), (removed.Provider, removed.Model, removed.Effort));

        // A fork starts its own turn with the alias: the name its provider reported in the source is known.
        var fork = source.ForkTo(ForkId, source.Lines.Count, Time(40), Time(40), model: "opus");
        fork.ModelChanged(Time(45, 2), "f1", "claude-code", "opus", "Medium", agentPrompt: "plan")
            .User(Time(45, 3), "f1")
            .Usage(Time(45, 5), "f1", model: "claude-opus-5-5", provider: "claude-code")
            .Idle(Time(45, 20), "f1");
        var batch = SessionFactsReducerTests.Reduce(fork);
        var own = batch.Runs["f1"];
        Assert.AreEqual(("claude-code", "claude-opus-5-5", "medium"), (own.Provider, own.Model, own.Effort));
        Assert.IsTrue(batch.Activity.Keys.All(static key => key.Model == "claude-opus-5-5"), "No time of the fork is counted under the alias.");
        Assert.IsTrue(batch.Usage.Keys.All(static key => key.AgentPrompt == "plan"));
    }

    [TestMethod]
    public void ARun_IsCountedWholeInTheSessionItsFirstRecordBelongsTo_WhateverTheTimesOfItsRecords()
    {
        // The end of the first turn is stamped after the start of the second one, and the prompt of the second before its first record.
        var source = new JournalBuilder(SourceId);
        source.Header(Time(0)).State(Time(0, 1))
            .ModelChanged(Time(0, 2), "r1", "codex", "gpt-6.1-sol")
            .User(Time(0, 3), "r1")
            .Usage(Time(0, 5), "r1")
            .Assistant(Time(10, 2.5), "r1")
            .Idle(Time(10, 3), "r1")
            .ModelChanged(Time(10, 2), "r2", "codex", "gpt-6.1-sol")
            .User(Time(10, 1), "r2", "a prompt of some words")
            .Usage(Time(10, 5), "r2")
            .Idle(Time(10, 20), "r2");
        // The rewind cuts at the first record of the second turn, and counts from its time.
        var kept = source.ForkTo(KeptId, source.Lines.Count, Time(60), Time(10, 2), kind: "Rewind");
        var rewound = source.RewindTo(source.FirstLineOf("r2"), 1, Time(60));

        var batch = SessionFactsReducerTests.Reduce(kept);

        CollectionAssert.AreEqual(new[] { "r2" }, batch.Runs.Keys.ToArray(), "A late record of a run of the source opens no run here.");
        var spent = Spent.Of(batch);
        Assert.AreEqual((1L, 1L, 1L, 1L, 1L, 0L), (spent.RunsStarted, spent.RunsCompleted, spent.Requests, spent.Prompts, spent.Runs, spent.Answers));
        Assert.AreEqual(22, batch.Runs["r2"].PromptChars, "The prompt of the run is counted though it is stamped before the run.");
        Assert.AreEqual(Spent.Of(SessionFactsReducerTests.Reduce(source)), spent.Plus(Spent.Of(SessionFactsReducerTests.Reduce(rewound))));
    }

    [TestMethod]
    public void ReadingAForkInTwoHalves_GivesTheFactsOfReadingItAtOnce()
    {
        var source = Source();
        var fork = source.ForkTo(ForkId, source.FirstLineOf("r3"), Time(40), Time(40));
        Turn(fork, "f1", 45, 8000, 2);
        var kept = source.ForkTo(KeptId, source.Lines.Count, Time(60), Time(10, 2), kind: "Rewind");

        SplitInvarianceTests.AssertSplitsMatch(fork, Enumerable.Range(0, fork.Lines.Count + 1));
        SplitInvarianceTests.AssertSplitsMatch(kept, Enumerable.Range(0, kept.Lines.Count + 1));
    }

    [TestMethod]
    public void ASessionThatIsNotAFork_IsCountedAsBefore()
    {
        var source = Source();

        var result = SessionFactsReducerTests.CatchUp(source);

        Assert.AreEqual(new Spent(3, 3, 3, 54_000, 3, 7000, 700, 1.75, 3, 6, 3, 3, 3, 66, 3, 15), Spent.Of(result.Batch));
        Assert.IsNull(result.Cursor.State.Inherited);
        Assert.IsFalse(result.Cursor.State.ToJson().Contains("Inherited", StringComparison.Ordinal), "The state of such a session is the one it always had.");
        Assert.IsNull(SessionFactsState.FromJson("{\"Version\":" + SessionFactsState.CurrentVersion + "}").Inherited, "A state saved before forks existed.");

        // A header that names no source, or one whose lineage has no time, tells nothing apart.
        foreach (var lineage in new[] { "null", "{\"session_id\":\"s\",\"kind\":\"Fork\"}", "{\"counted_from\":12}", "\"x\"" })
        {
            var journal = new JournalBuilder(SourceId);
            journal.AddRaw("codealta.sessionHeader",
                $"{{\"schema_version\":1,\"session_id\":\"{SourceId}\",\"kind\":\"ProjectSession\",\"backend_id\":\"codex\",\"provider_key\":\"codex\",\"forked_from\":{lineage},\"working_directory\":\"C:\\\\placeholder\",\"title\":\"A title\",\"created_at\":\"{JournalBuilder.Json(Time(30))}\"}}",
                Time(30));
            Turn(journal, "r1", 0, 1000, 0.25);

            var plain = SessionFactsReducerTests.CatchUp(journal);

            Assert.IsNull(plain.Cursor.State.Inherited, lineage);
            Assert.AreEqual(1, plain.Batch.Runs.Count, lineage);
            Assert.AreEqual("ProjectSession", plain.Batch.Session!.SessionKind, lineage);
            Assert.AreEqual("A title", plain.Batch.Session.Title, lineage);
        }
    }
}
