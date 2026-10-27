using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The runs of scripts of Code Mode: what their records count, under which name, and what is never kept.</summary>
[TestClass]
public sealed class ScriptFactsTests
{
    private static readonly DateTimeOffset T0 = JournalBuilder.Time(0);

    /// <summary>A turn of yours that sends a script: it loads a library script, makes calls, asks, sleeps, starts sessions, and ends after the turn.</summary>
    internal static JournalBuilder ScriptJournal(string execName = "exec", string title = "Review the pull request")
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .ModelChanged(T0.AddSeconds(2), "r1", "codex", "gpt-6.1-sol", "High")
            .User(T0.AddSeconds(3), "r1", "review it")
            .ToolStarted(T0.AddSeconds(4), "r1", "t1", execName, "{\"code\":\"return await workflow('review-pr')\"}")
            .Workflow(T0.AddSeconds(5), AgentWorkflowEventKind.RunStarted, "run-1", record => record with { Title = title, RecordsScripts = true }, callId: "t1")
            .Workflow(T0.AddSeconds(5.5), AgentWorkflowEventKind.ScriptLoaded, "run-1", record => record with { ScriptName = "review-pr", ScriptScope = "project" }, callId: "t1")
            .ScriptCall(T0.AddSeconds(6), "r1", "t1", "t1/1", "read_file")
            .ScriptCall(T0.AddSeconds(8), "r1", "t1", "t1/2", "grep")
            .Workflow(T0.AddSeconds(9), AgentWorkflowEventKind.StepEnded, "run-1", record => record with { StepId = "read", Status = "completed", DurationMs = 3000 }, callId: "t1")
            .Workflow(T0.AddSeconds(10), AgentWorkflowEventKind.SessionStarted, "run-1", record => record with { StepId = "review", ChildSessionId = "child-1" }, callId: "t1")
            .Workflow(T0.AddSeconds(10.5), AgentWorkflowEventKind.SessionStarted, "run-1", record => record with { StepId = "review", ChildSessionId = "child-2" }, callId: "t1")
            .Workflow(T0.AddSeconds(15), AgentWorkflowEventKind.SessionEnded, "run-1", record => record with { ChildSessionId = "child-1", Status = "completed" }, callId: "t1")
            .Workflow(T0.AddSeconds(16), AgentWorkflowEventKind.SessionEnded, "run-1", record => record with { ChildSessionId = "child-2", Status = "failed", Message = "PRIVATE ERROR" }, callId: "t1")
            .Workflow(T0.AddSeconds(18), AgentWorkflowEventKind.StepEnded, "run-1", record => record with { StepId = "merge", Status = "failed", Message = "PRIVATE STEP ERROR" }, callId: "t1")
            .Workflow(T0.AddSeconds(20), AgentWorkflowEventKind.WaitStarted, "run-1", record => record with { Status = "question", StepId = "deploy", Message = "PRIVATE QUESTION" }, callId: "t1")
            .Workflow(T0.AddSeconds(50), AgentWorkflowEventKind.WaitEnded, "run-1", record => record with { Status = "question", StepId = "deploy" }, callId: "t1")
            .Workflow(T0.AddSeconds(51), AgentWorkflowEventKind.WaitStarted, "run-1", record => record with { Status = "sleep", WakeAt = T0.AddSeconds(55) }, callId: "t1")
            .ToolDone(T0.AddSeconds(52), "r1", "t1", execName)
            .Usage(T0.AddSeconds(53), "r1")
            .Idle(T0.AddSeconds(54), "r1")
            .Workflow(T0.AddSeconds(55), AgentWorkflowEventKind.WaitEnded, "run-1", record => record with { Status = "sleep" }, callId: "t1")
            .Workflow(T0.AddSeconds(90), AgentWorkflowEventKind.RunEnded, "run-1", record => record with { Status = "completed", DurationMs = 85_000, Calls = 7 }, callId: "t1");
        return b;
    }

    private static ScriptMeasures Script(FactBatch batch, string script, string origin = "you", string provider = "codex", string model = "gpt-6.1-sol")
        => batch.Scripts.Where(pair => pair.Key.Script == script && pair.Key.Origin == origin && pair.Key.Provider == provider && pair.Key.Model == model)
            .Aggregate(new ScriptMeasures(), static (sum, pair) => { sum.Add(pair.Value); return sum; });

    [TestMethod]
    public void ARunOfALibraryScript_IsCountedUnderItsName_WithWhatItDidAtItsEnd()
    {
        var result = SessionFactsReducerTests.CatchUp(ScriptJournal());
        var batch = result.Batch;

        Assert.AreEqual(0, result.Scan.UnknownLines);
        Assert.AreEqual(0, result.Scan.MalformedLines);
        var script = Script(batch, "review-pr");
        Assert.AreEqual(1, script.RunsStarted);
        Assert.AreEqual(1, script.RunsCompleted);
        Assert.AreEqual(1, script.DurationCount);
        Assert.AreEqual(85_000, script.DurationMs);
        Assert.AreEqual(30_000, script.WaitMs, "The question waited 30 seconds; a sleep is not a wait for the user.");
        Assert.AreEqual(7, script.Calls);
        Assert.AreEqual(2, script.Steps);
        Assert.AreEqual(1, script.StepsFailed);
        Assert.AreEqual(2, script.SessionsStarted);
        Assert.AreEqual(1, script.AsksFailed);
        Assert.AreEqual(1, script.Sleeps);
        Assert.AreEqual(0, Script(batch, ScriptLabels.Inline).RunsStarted, "The start moved to the library script it loaded.");

        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.ScriptCalled, "review-pr")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.ScriptStepFailed, "review-pr/merge")]);
        Assert.AreEqual(1, batch.Histograms[new HistogramKey(QuarterHour.Of(T0), HistogramMeasure.ScriptRunDurationMs, "review-pr", HistogramSteps.StepOf(85_000))]);
        Assert.AreEqual(1, batch.Histograms[new HistogramKey(QuarterHour.Of(T0), HistogramMeasure.ScriptRunSessions, "review-pr", HistogramSteps.StepOf(2))]);
        var longest = batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.LongestScriptRunMs, string.Empty)];
        Assert.AreEqual(85_000, longest.Value);
        Assert.AreEqual("run-1", longest.RunId);

        // The turn sent one script; the calls the script made are tool calls of the turn, and calls of scripts.
        var run = batch.Runs["r1"];
        Assert.AreEqual(1, run.ExecCalls);
        Assert.AreEqual(3, run.ToolCalls);
        Assert.AreEqual(ToolKind.Script, batch.Tools.Single(static pair => pair.Key.Tool == "ToolCall:exec").Key.Kind);
        Assert.AreEqual(2, batch.Tools.Values.Sum(static value => value.ScriptCalls));
        Assert.AreEqual(0, batch.Tools.Single(static pair => pair.Key.Tool == "ToolCall:exec").Value.ScriptCalls);

        var row = batch.ScriptRuns["run-1"];
        Assert.AreEqual("completed", row.Status);
        Assert.AreEqual("review-pr", row.Script);
        Assert.AreEqual("you", row.Origin);
        Assert.AreEqual(T0.AddSeconds(5), row.Start);
        Assert.AreEqual(T0.AddSeconds(90), row.End);
        Assert.AreEqual(85_000, row.DurationMs);
        Assert.AreEqual(7, row.Calls);
        Assert.AreEqual(2, row.Steps);
        Assert.AreEqual(1, row.StepsFailed);
        Assert.AreEqual(2, row.Sessions);
        Assert.AreEqual(30_000, row.WaitMs);
        Assert.AreEqual("gpt-6.1-sol", row.Model);

        // The records of a script are written by the host: the provider of the session stays its own.
        Assert.AreEqual("codex", result.Cursor.State.Provider);
        Assert.AreEqual(0, result.Cursor.State.OpenScripts.Count);
    }

    [TestMethod]
    public void NamesNeverContent_OnlyIdentifiersAreKept()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .Workflow(T0.AddSeconds(2), AgentWorkflowEventKind.RunStarted, "run-1", record => record with { Title = "Read C:\\Users\\someone\\secret.txt", RecordsScripts = true })
            .Workflow(T0.AddSeconds(3), AgentWorkflowEventKind.ScriptLoaded, "run-1", record => record with { ScriptName = "Not A Name" })
            .Workflow(T0.AddSeconds(4), AgentWorkflowEventKind.RunEnded, "run-1", record => record with { Status = "failed", Message = "TypeError: cannot read PRIVATE-VALUE", DurationMs = 10 })
            .Workflow(T0.AddSeconds(5), AgentWorkflowEventKind.RunStarted, "run-2", record => record with { RecordsScripts = true })
            .Workflow(T0.AddSeconds(6), AgentWorkflowEventKind.ScriptLoaded, "run-2", record => record with { ScriptName = "deploy" })
            .Workflow(T0.AddSeconds(7), AgentWorkflowEventKind.StepEnded, "run-2", record => record with { StepId = "a step with PRIVATE words", Status = "failed" })
            .Workflow(T0.AddSeconds(8), AgentWorkflowEventKind.RunEnded, "run-2", record => record with { Status = "failed", Message = "the PRIVATE thing broke", DurationMs = 10 });

        var result = SessionFactsReducerTests.CatchUp(b);
        var batch = result.Batch;
        var text = batch.ToCanonicalText() + batch.ToCanonicalRunsText();

        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.ScriptError, "TypeError")]);
        Assert.AreEqual(1, Script(batch, ScriptLabels.Inline, origin: "unknown", provider: "codex").RunsFailed, "A name that is not one of a library script names nothing.");
        Assert.AreEqual(1, Script(batch, "deploy", origin: "unknown", provider: "codex").StepsFailed, "The failed step counts, its id is not kept.");
        Assert.IsFalse(batch.Details.Keys.Any(static key => key.List == DetailList.ScriptStepFailed));
        Assert.IsFalse(text.Contains("PRIVATE", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("secret", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("Not A Name", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void ARunRecordedBeforeScriptsWere_IsUnknown_AndOneSinceIsInline()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .Workflow(T0.AddSeconds(2), AgentWorkflowEventKind.RunStarted, "run-old")
            .Workflow(T0.AddSeconds(3), AgentWorkflowEventKind.RunEnded, "run-old", record => record with { Status = "completed", DurationMs = 900, Calls = 2 })
            .Workflow(T0.AddSeconds(4), AgentWorkflowEventKind.RunStarted, "run-new", record => record with { RecordsScripts = true })
            .Workflow(T0.AddSeconds(5), AgentWorkflowEventKind.RunEnded, "run-new", record => record with { Status = "cancelled", DurationMs = 800 });

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(1, Script(batch, ScriptLabels.Unknown, origin: "unknown").RunsCompleted);
        Assert.AreEqual(1, Script(batch, ScriptLabels.Inline, origin: "unknown").RunsCancelled);
        Assert.AreEqual(ScriptLabels.Unknown, batch.ScriptRuns["run-old"].Script);
        Assert.AreEqual(ScriptLabels.Inline, batch.ScriptRuns["run-new"].Script);
    }

    [TestMethod]
    public void AnInterruptedRun_HasNoDuration_AndAResumeIsCountedAsOne()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .Workflow(T0.AddSeconds(2), AgentWorkflowEventKind.RunStarted, "run-1", record => record with { RecordsScripts = true })
            .Workflow(T0.AddSeconds(3), AgentWorkflowEventKind.WaitStarted, "run-1", record => record with { Status = "approval" })
            // CodeAlta ended; its next start writes the end, with no duration.
            .Workflow(T0.AddMinutes(40), AgentWorkflowEventKind.RunEnded, "run-1", record => record with { Status = "interrupted", Message = "CodeAlta ended while the script ran." })
            .Workflow(T0.AddMinutes(41), AgentWorkflowEventKind.RunStarted, "run-2", record => record with { RecordsScripts = true, ResumesRunId = "run-1" })
            .Workflow(T0.AddMinutes(42), AgentWorkflowEventKind.RunEnded, "run-2", record => record with { Status = "completed", DurationMs = 60_000 });

        var batch = SessionFactsReducerTests.Reduce(b);

        var interrupted = Script(batch, ScriptLabels.Inline, origin: "unknown");
        Assert.AreEqual(1, interrupted.RunsInterrupted);
        Assert.AreEqual(0, interrupted.DurationCount);
        Assert.AreEqual(0, interrupted.DurationMs);
        Assert.AreEqual(40L * 60 * 1000 - 3000, interrupted.WaitMs, "The wait for the approval ends with the run.");
        Assert.IsNull(batch.ScriptRuns["run-1"].DurationMs);
        Assert.AreEqual("interrupted", batch.ScriptRuns["run-1"].Status);
        Assert.IsFalse(batch.Details.Keys.Any(static key => key.List == DetailList.ScriptError), "An interruption is no error of the script.");
        var resumed = Script(batch, ScriptLabels.Inline, origin: "resume");
        Assert.AreEqual(1, resumed.RunsStarted);
        Assert.AreEqual(1, resumed.RunsCompleted);
        Assert.AreEqual("run-1", batch.ScriptRuns["run-2"].ResumesRunId);
    }

    [TestMethod]
    public void ExecThroughTheMcpServerOfCodeAlta_IsAScriptTool_AndAnotherServersIsNot()
    {
        Assert.AreEqual(ToolKind.Script, StatisticsToolBuckets.KindOf(AgentActivityKind.McpToolCall, "mcp__codealta__exec"));
        Assert.AreEqual(ToolKind.Script, StatisticsToolBuckets.KindOf(AgentActivityKind.McpToolCall, "mcp__codealta__wait"));
        Assert.AreEqual(ToolKind.Script, StatisticsToolBuckets.KindOf(AgentActivityKind.ToolCall, "wait"));
        Assert.AreEqual(ToolKind.Mcp, StatisticsToolBuckets.KindOf(AgentActivityKind.McpToolCall, "mcp__other__exec"));
        Assert.AreEqual(ToolKind.Alta, StatisticsToolBuckets.KindOf(AgentActivityKind.McpToolCall, "mcp__codealta__alta"));

        var batch = SessionFactsReducerTests.Reduce(ScriptJournal("mcp__codealta__exec"));
        Assert.AreEqual(1, batch.Runs["r1"].ExecCalls);
        Assert.AreEqual(1, Script(batch, "review-pr").RunsCompleted);
    }

    [TestMethod]
    public void ASessionAScriptStarted_KnowsTheRunOfItsScript()
    {
        var child = new JournalBuilder("55555555-5555-5555-5555-555555555555");
        child.Header(T0, parent: "11111111-1111-1111-1111-111111111111", createdByKind: "workflow", createdBySession: "11111111-1111-1111-1111-111111111111", correlation: "workflow:run-9/review");
        var agent = new JournalBuilder("66666666-6666-6666-6666-666666666666");
        agent.Header(T0, parent: "11111111-1111-1111-1111-111111111111", createdByKind: "agent", correlation: "workflow:run-9");

        Assert.AreEqual("run-9", SessionFactsReducerTests.Reduce(child).Session!.ScriptRunId);
        Assert.IsNull(SessionFactsReducerTests.Reduce(agent).Session!.ScriptRunId, "Only a session created by a script has the run of one.");
    }

    [TestMethod]
    public void AKindALaterVersionAdds_IsPassedOver_NotDamaged()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .Add($"{{\"$type\":\"workflow\",\"kind\":\"SomethingNew\",\"workflowRunId\":\"run-1\",{b.Envelope(T0.AddSeconds(1), null, "codealta")}");

        var result = SessionFactsReducerTests.CatchUp(b);

        Assert.AreEqual(0, result.Scan.UnknownLines);
        Assert.AreEqual(0, result.Scan.MalformedLines);
        Assert.AreEqual(0, result.Batch.Scripts.Count);
    }

    [TestMethod]
    public void TheRecordsOfAScriptOfTheSourceOfAFork_AreNotCountedInTheFork()
    {
        var source = ScriptJournal();
        var fork = source.ForkTo("77777777-7777-7777-7777-777777777777", source.Lines.Count, JournalBuilder.Time(30), JournalBuilder.Time(30));
        // A late end of a run the source started: it is the source's run.
        fork.Workflow(JournalBuilder.Time(31), AgentWorkflowEventKind.RunEnded, "run-1", record => record with { Status = "completed", DurationMs = 5 });

        var reducer = new SessionFactsReducer(fork.SessionId);
        using var scanner = new JournalScanner();
        using var stream = fork.ToStream();
        scanner.Scan(stream, 0, null, reducer, long.MaxValue, CancellationToken.None);
        var batch = reducer.TakeBatch();

        Assert.AreEqual(0, batch.Scripts.Count);
        Assert.AreEqual(0, batch.ScriptRuns.Count);
        Assert.AreEqual(1, reducer.Diagnostics.ScriptRecordsWithoutStart);
    }

    [TestMethod]
    public void ARecordOfTheSerializerOfTheApplication_IsReadFromItsStart()
    {
        var line = new JournalBuilder().Workflow(T0, AgentWorkflowEventKind.RunEnded, "run-1", record => record with { Status = "failed", Message = "Error: x", DurationMs = 3, Calls = 2 }).Lines[0];

        Assert.AreEqual(JournalRecordKind.Workflow, JournalPrefixClassifier.Classify(System.Text.Encoding.UTF8.GetBytes(line), out _));
        StringAssert.EndsWith(line, $"\"timestamp\":\"{T0:yyyy-MM-ddTHH:mm:ssK}\"}}".Replace("Z", "+00:00", StringComparison.Ordinal));
    }
}
