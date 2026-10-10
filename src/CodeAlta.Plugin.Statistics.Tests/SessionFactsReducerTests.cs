using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class SessionFactsReducerTests
{
    private static readonly DateTimeOffset T0 = JournalBuilder.Time(0);

    internal static CatchUpResult CatchUp(JournalBuilder builder, JournalCursor? cursor = null, long maxBytes = long.MaxValue)
    {
        using var catchUp = new SessionCatchUp();
        using var stream = builder.ToStream();
        return catchUp.CatchUp(builder.SessionId, stream, cursor, maxBytes, CancellationToken.None);
    }

    internal static FactBatch Reduce(JournalBuilder builder) => CatchUp(builder).Batch;

    private static long Sum<TKey>(Dictionary<TKey, ActivityMeasures> dictionary, Func<ActivityMeasures, long> selector, Func<TKey, bool>? filter = null)
        where TKey : notnull
        => dictionary.Where(pair => filter?.Invoke(pair.Key) ?? true).Sum(pair => selector(pair.Value));

    private static long Sum<TKey>(Dictionary<TKey, UsageMeasures> dictionary, Func<UsageMeasures, long> selector, Func<TKey, bool>? filter = null)
        where TKey : notnull
        => dictionary.Where(pair => filter?.Invoke(pair.Key) ?? true).Sum(pair => selector(pair.Value));

    private static long Sum<TKey>(Dictionary<TKey, ToolMeasures> dictionary, Func<ToolMeasures, long> selector, Func<TKey, bool>? filter = null)
        where TKey : notnull
        => dictionary.Where(pair => filter?.Invoke(pair.Key) ?? true).Sum(pair => selector(pair.Value));

    private static long Sum<TKey>(Dictionary<TKey, ContentMeasures> dictionary, Func<ContentMeasures, long> selector, Func<TKey, bool>? filter = null)
        where TKey : notnull
        => dictionary.Where(pair => filter?.Invoke(pair.Key) ?? true).Sum(pair => selector(pair.Value));

    // ----- runs and active time -----

    [TestMethod]
    public void Run_GoesFromItsFirstRecordToItsIdle()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .ModelChanged(T0.AddSeconds(2), "r1", "codex", "gpt-6.1-sol", "High")
            .User(T0.AddSeconds(3), "r1")
            .Usage(T0.AddSeconds(30), "r1")
            .Idle(T0.AddSeconds(62), "r1");

        var batch = Reduce(b);

        var key = new ActivityKey(QuarterHour.Of(T0), "codex", "gpt-6.1-sol", "high");
        var activity = batch.Activity[key];
        Assert.AreEqual(1, activity.RunsStarted);
        Assert.AreEqual(1, activity.RunsCompleted);
        Assert.AreEqual(60_000, activity.ActiveMs);
        var run = batch.Runs["r1"];
        Assert.AreEqual(RunOutcome.Completed, run.Outcome);
        Assert.AreEqual(T0.AddSeconds(2), run.Start);
        Assert.AreEqual(T0.AddSeconds(62), run.End);
        Assert.AreEqual(TimeSpan.FromSeconds(60), run.Duration);
        Assert.AreEqual(PromptSender.You, run.Sender);
        Assert.AreEqual(PromptKind.NewTurn, run.PromptKind);
        Assert.AreEqual(1, run.Requests);
        var step = HistogramSteps.StepOf(60_000);
        Assert.AreEqual(1, batch.Histograms[new HistogramKey(QuarterHour.Of(T0), HistogramMeasure.RunDurationMs, string.Empty, step)]);
        Assert.AreEqual(60_000, batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.LongestRunMs, string.Empty)].Value);
        Assert.AreEqual("r1", batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.LongestRunMs, string.Empty)].RunId);
    }

    [TestMethod]
    public void Run_ActiveTimeIsCutAtTheLimitsOfQuarterHours()
    {
        var start = new DateTimeOffset(2026, 10, 9, 10, 14, 30, TimeSpan.Zero);
        var b = new JournalBuilder();
        b.ModelChanged(start, "r1", "codex", "m", "Low")
            .Usage(start.AddSeconds(20), "r1", model: "m")
            .Usage(start.AddSeconds(100), "r1", model: "m")
            .Idle(start.AddSeconds(150), "r1");

        var batch = Reduce(b);

        var first = new ActivityKey(QuarterHour.Of(start), "codex", "m", "low");
        var second = new ActivityKey(QuarterHour.Of(start.AddMinutes(1)), "codex", "m", "low");
        Assert.AreNotEqual(first.Quarter, second.Quarter);
        Assert.AreEqual(30_000, batch.Activity[first].ActiveMs);
        Assert.AreEqual(120_000, batch.Activity[second].ActiveMs);
        Assert.AreEqual(1, batch.Activity[first].RunsStarted, "a run is counted in the quarter it starts");
        Assert.AreEqual(0, batch.Activity[second].RunsStarted);
        Assert.AreEqual(1, batch.Activity[second].RunsCompleted, "its outcome in the quarter it ends");
        Assert.AreEqual(150_000, Sum(batch.Activity, static m => m.ActiveMs));
    }

    [TestMethod]
    public void Run_CrossingMidnightGivesEachDayItsPart()
    {
        var start = new DateTimeOffset(2026, 10, 9, 23, 58, 0, TimeSpan.Zero);
        var b = new JournalBuilder();
        b.ModelChanged(start, "r1", "codex", "m", "Low").Idle(start.AddMinutes(4), "r1");

        var batch = Reduce(b);

        var before = batch.Activity.Where(static pair => pair.Key.Quarter.Start.UtcDateTime.Date == new DateTime(2026, 10, 9)).Sum(static pair => pair.Value.ActiveMs);
        var after = batch.Activity.Where(static pair => pair.Key.Quarter.Start.UtcDateTime.Date == new DateTime(2026, 10, 10)).Sum(static pair => pair.Value.ActiveMs);
        Assert.AreEqual(120_000, before);
        Assert.AreEqual(120_000, after);
        Assert.AreEqual(new DateTime(2026, 10, 9), batch.Activity.Single(static pair => pair.Value.RunsStarted == 1).Key.Quarter.Start.UtcDateTime.Date);
        Assert.AreEqual(new DateTime(2026, 10, 10), batch.Activity.Single(static pair => pair.Value.RunsCompleted == 1).Key.Quarter.Start.UtcDateTime.Date);
    }

    [TestMethod]
    public void Run_WithoutAnEndIsInterruptedWhenTheNextOneStarts()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .User(T0.AddSeconds(1), "r1")
            .Usage(T0.AddSeconds(40), "r1", model: "m")
            .ModelChanged(T0.AddMinutes(5), "r2", "codex", "m", "Low")
            .User(T0.AddMinutes(5).AddSeconds(1), "r2")
            .Idle(T0.AddMinutes(5).AddSeconds(30), "r2");

        var batch = Reduce(b);

        Assert.AreEqual(RunOutcome.Interrupted, batch.Runs["r1"].Outcome);
        Assert.AreEqual(T0.AddSeconds(40), batch.Runs["r1"].End);
        Assert.AreEqual(1, Sum(batch.Activity, static m => m.RunsInterrupted));
        Assert.AreEqual(1, Sum(batch.Activity, static m => m.RunsCompleted));
        Assert.AreEqual(2, Sum(batch.Activity, static m => m.RunsStarted));
        Assert.AreEqual(40_000 + 30_000, Sum(batch.Activity, static m => m.ActiveMs));
    }

    [TestMethod]
    public void Run_StillGoingStaysOpen_AndIsInterruptedOnlyWhenTheCallerSaysSo()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1").Usage(T0.AddSeconds(40), "r1", model: "m");
        using var stream = b.ToStream();
        var reducer = new SessionFactsReducer(b.SessionId);
        using var scanner = new JournalScanner();
        scanner.Scan(stream, 0, null, reducer, long.MaxValue, CancellationToken.None);

        var running = reducer.TakeBatch();
        Assert.AreEqual(RunOutcome.Running, running.Runs["r1"].Outcome);
        Assert.AreEqual(0, Sum(running.Activity, static m => m.RunsInterrupted));
        Assert.AreEqual(40_000, Sum(running.Activity, static m => m.ActiveMs));

        reducer.InterruptOpenRuns();
        var closed = reducer.TakeBatch();
        Assert.AreEqual(RunOutcome.Interrupted, closed.Runs["r1"].Outcome);
        Assert.AreEqual(1, Sum(closed.Activity, static m => m.RunsInterrupted));
        Assert.AreEqual(0, reducer.State.OpenRuns.Count);
    }

    [TestMethod]
    public void Run_EndingWithAnErrorFailsAndCountsTheError()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1").Error(T0.AddSeconds(20), "r1").Idle(T0.AddSeconds(25), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(RunOutcome.Failed, batch.Runs["r1"].Outcome);
        Assert.AreEqual(1, Sum(batch.Activity, static m => m.RunsFailed));
        Assert.AreEqual(0, Sum(batch.Activity, static m => m.RunsCompleted), "a record after the end of the run does not end it again");
        Assert.AreEqual(1, Sum(batch.Activity, static m => m.Errors));
        Assert.AreEqual(20_000, Sum(batch.Activity, static m => m.ActiveMs));
    }

    [TestMethod]
    public void Run_UsesTheOrderOfTheFile_NotTheOrderOfTime()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0.AddSeconds(5), "r1", "codex", "m", "Low")
            .Usage(T0.AddSeconds(30), "r1", model: "m")
            .Usage(T0.AddSeconds(20), "r1", model: "m")
            .Usage(T0.AddSeconds(4), "r1", model: "m")
            .Idle(T0.AddSeconds(40), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(T0.AddSeconds(5), batch.Runs["r1"].Start);
        Assert.AreEqual(35_000, Sum(batch.Activity, static m => m.ActiveMs));
        Assert.AreEqual(3, Sum(batch.Usage, static m => m.Requests));
    }

    [TestMethod]
    public void Run_RecordsOfARunThatEndedDoNotOpenItAgain()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").Idle(T0.AddSeconds(10), "r1").Usage(T0.AddSeconds(11), "r1", model: "m").DiffUpdated(T0.AddSeconds(12), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(1, Sum(batch.Activity, static m => m.RunsStarted));
        Assert.AreEqual(10_000, Sum(batch.Activity, static m => m.ActiveMs));
    }

    // ----- models, providers -----

    [TestMethod]
    public void Model_SwitchGivesEachModelItsPartOfTheTimeAndTheRequests()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex_cli", "gpt-a", "High")
            .User(T0.AddSeconds(1), "r1")
            .Usage(T0.AddSeconds(10), "r1", model: "gpt-a", provider: "codex_cli")
            .ModelChanged(T0.AddSeconds(30), "r1", "claude-code", "opus", "Medium")
            .Usage(T0.AddSeconds(50), "r1", model: "claude-opus-5-5", provider: "claude-code")
            .Idle(T0.AddSeconds(60), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(30_000, batch.Activity[new ActivityKey(QuarterHour.Of(T0), "codex", "gpt-a", "high")].ActiveMs, "codex_cli is folded into codex");
        Assert.AreEqual(20_000, batch.Activity[new ActivityKey(QuarterHour.Of(T0), "claude-code", "opus", "medium")].ActiveMs, "the alias holds the time until the first request names the model");
        Assert.AreEqual(10_000, batch.Activity[new ActivityKey(QuarterHour.Of(T0), "claude-code", "claude-opus-5-5", "medium")].ActiveMs);
        Assert.IsTrue(batch.Usage.ContainsKey(new UsageKey(QuarterHour.Of(T0), "codex", "gpt-a", "high", "default", UsagePurpose.Turn)));
        Assert.IsTrue(batch.Usage.ContainsKey(new UsageKey(QuarterHour.Of(T0), "claude-code", "claude-opus-5-5", "medium", "default", UsagePurpose.Turn)), "the model the provider says it used, not the alias");
    }

    [TestMethod]
    public void Model_AliasLearnedFromARequestIsUsedWhenTheModelIsChosenAgain()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "claude-code");
        b.ModelChanged(T0, "r1", "claude-code", "opus", "High")
            .Usage(T0.AddSeconds(5), "r1", model: "claude-opus-5-5", provider: "claude-code")
            .Idle(T0.AddSeconds(10), "r1")
            .ModelChanged(T0.AddMinutes(1), "r2", "claude-code", "opus", "High")
            .Idle(T0.AddMinutes(1).AddSeconds(10), "r2");

        var batch = Reduce(b);

        Assert.IsTrue(batch.Activity.Keys.All(static key => key.Model is "opus" or "claude-opus-5-5"));
        Assert.AreEqual(1, batch.Activity.Where(static pair => pair.Key.Model == "opus").Sum(static pair => pair.Value.RunsStarted), "only the first run of the session started under the alias");
        Assert.AreEqual(1, batch.Activity.Where(static pair => pair.Key.Model == "claude-opus-5-5").Sum(static pair => pair.Value.RunsStarted));
    }

    [TestMethod]
    public void Model_ComesFromTheStateUntilTheFirstChange()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "copilot_cli");
        b.State(T0, model: "mai-code-1-flash-picker", effort: "High").Usage(T0.AddSeconds(5), "r1", model: "mai-code-1-flash-picker", input: 10, output: 5);

        var batch = Reduce(b);

        Assert.IsTrue(batch.Usage.ContainsKey(new UsageKey(QuarterHour.Of(T0), "copilot", "mai-code-1-flash-picker", "high", "default", UsagePurpose.Turn)));
    }

    // ----- requests -----

    [TestMethod]
    public void Usage_CountsOnlyUsageUpdated_AndSplitsTheInputWithTheOneRuleOfTheApplication()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "gpt-6.1-sol", "High")
            .Usage(T0.AddSeconds(1), "r1", input: 1000, output: 150, cached: 400, reasoning: 50, window: 20000, limit: 100000)
            .Usage(T0.AddSeconds(2), "r1", input: 2000, output: 10, cached: 0, reasoning: 0, window: 50000, limit: 100000)
            .Usage(T0.AddSeconds(3), "r1", kind: "CompactionCompleted", input: 9000, output: 9000)
            .Idle(T0.AddSeconds(4), "r1", input: 9999, output: 9999);

        var batch = Reduce(b);

        var usage = batch.Usage.Single().Value;
        Assert.AreEqual(2, usage.Requests);
        Assert.AreEqual(3000, usage.InputTokens);
        Assert.AreEqual(2600, usage.FreshInputTokens);
        Assert.AreEqual(400, usage.CacheReadTokens);
        Assert.AreEqual(0, usage.CacheWriteTokens);
        Assert.AreEqual(160, usage.OutputTokens);
        Assert.AreEqual(50, usage.ReasoningTokens);
        Assert.AreEqual(2, usage.ContextSamples);
        Assert.AreEqual(70000, usage.ContextTokensSum);
        Assert.AreEqual(200000, usage.ContextLimitSum);
        Assert.AreEqual(200_000 + 500_000, usage.ContextFillPpmSum);
        Assert.AreEqual(500_000, usage.ContextFillPpmMax);
        Assert.AreEqual(2, batch.Runs["r1"].Requests);
        Assert.AreEqual(3000, batch.Runs["r1"].InputTokens);
        Assert.AreEqual(500_000, batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.HighestContextFillPpm, "gpt-6.1-sol")].Value);
        Assert.AreEqual(2000, batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.LargestRequestInputTokens, "gpt-6.1-sol")].Value);
    }

    [TestMethod]
    public void Usage_OfClaudeCodeKeepsTheCacheWriteAndTheCostOfTheTurnOnce()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "claude-code");
        b.ModelChanged(T0, "r1", "claude-code", "opus", "High")
            .Usage(T0.AddSeconds(1), "r1", model: "claude-opus-5-5", input: 2, output: 669, cacheRead: 14932, cacheWrite: 24624, cached: 14932)
            .Usage(T0.AddSeconds(2), "r1", model: "claude-opus-5-5", input: 3, output: 995, cacheRead: 245627, cacheWrite: 1656, cached: 245627, cost: 6.5957132, duration: 806888.0)
            .Usage(T0.AddSeconds(3), "r1", model: "claude-opus-5-5", input: 3, output: 995, cacheRead: 245627, cacheWrite: 1656, cached: 245627, cost: 6.5957132, duration: 806888.0)
            .Idle(T0.AddSeconds(4), "r1");

        var batch = Reduce(b);

        var usage = batch.Usage.Single().Value;
        Assert.AreEqual(3, usage.Requests);
        Assert.AreEqual(2 + 14932 + 24624 + 3 + 245627 + 1656 + 3 + 245627 + 1656, usage.InputTokens);
        Assert.AreEqual(2 + 3 + 3, usage.FreshInputTokens);
        Assert.AreEqual(24624 + 1656 + 1656, usage.CacheWriteTokens);
        Assert.AreEqual(806888, usage.ProviderDurationMs, "the duration of the turn is counted once, like its cost");
        var cost = batch.Cost.Single();
        Assert.AreEqual(CostUnits.Usd, cost.Key.Unit);
        Assert.AreEqual(6.5957132, cost.Value.Total, 1e-9);
        Assert.AreEqual(1, cost.Value.Records);
        Assert.AreEqual(6.5957132, batch.Runs["r1"].CostUsd, 1e-9);
        Assert.AreEqual(0, batch.Runs["r1"].CostCredits);
    }

    [TestMethod]
    public void Usage_OfCopilotCostsInCreditsPerRequest_AndNeverAddsThemToDollars()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "copilot");
        b.ModelChanged(T0, "r1", "copilot", "gpt-x", "Low")
            .Usage(T0.AddSeconds(1), "r1", model: "gpt-x", input: 100, output: 10, cost: 2.5, costUnit: "AI credits")
            .Usage(T0.AddSeconds(2), "r1", model: "gpt-x", input: 100, output: 10, cost: 2.5, costUnit: "AI credits", duration: 50)
            .Usage(T0.AddSeconds(3), "r1", model: "gpt-x", input: 100, output: 10, cost: 1.0)
            .Idle(T0.AddSeconds(4), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(5.0, batch.Cost.Single(static pair => pair.Key.Unit == CostUnits.Credits).Value.Total, 1e-9);
        Assert.AreEqual(1.0, batch.Cost.Single(static pair => pair.Key.Unit == CostUnits.Usd).Value.Total, 1e-9);
        Assert.AreEqual(5.0, batch.Runs["r1"].CostCredits, 1e-9);
        Assert.AreEqual(1.0, batch.Runs["r1"].CostUsd, 1e-9);
    }

    [TestMethod]
    public void Usage_OfAPlainApiProvider_HasNoCacheAndNoCost()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "mistral");
        b.ModelChanged(T0, "r1", "mistral", "mistral-large", "Low").Usage(T0.AddSeconds(1), "r1", model: "mistral-large", input: 500, output: 40).Idle(T0.AddSeconds(2), "r1");

        var batch = Reduce(b);

        var usage = batch.Usage.Single().Value;
        Assert.AreEqual(500, usage.InputTokens);
        Assert.AreEqual(500, usage.FreshInputTokens);
        Assert.AreEqual(0, usage.CacheReadTokens);
        Assert.AreEqual(0, usage.ReasoningTokens);
        Assert.AreEqual(0, batch.Cost.Count);
    }

    [TestMethod]
    public void Usage_OfACompactionRequestIsKeptApart()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .Usage(T0.AddSeconds(1), "r1", model: "m", input: 100, output: 10)
            .Usage(T0.AddSeconds(2), "r1", model: "m", input: 5000, output: 300, initiator: "compaction")
            .Compaction(T0.AddSeconds(3), "r1", "threshold", 200000, 20000)
            .Idle(T0.AddSeconds(4), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(1, batch.Usage.Single(static pair => pair.Key.Purpose == UsagePurpose.Turn).Value.Requests);
        Assert.AreEqual(5000, batch.Usage.Single(static pair => pair.Key.Purpose == UsagePurpose.Compaction).Value.InputTokens);
        Assert.AreEqual(1, batch.Runs["r1"].Requests, "a request of a compaction is not a request of the turn");
        Assert.AreEqual(1, Sum(batch.Activity, static m => m.Compactions));
        Assert.AreEqual(200000, Sum(batch.Activity, static m => m.CompactionTokensBefore));
        Assert.AreEqual(20000, Sum(batch.Activity, static m => m.CompactionTokensAfter));
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.CompactionTrigger, "threshold")]);
        Assert.AreEqual(1, batch.Runs["r1"].Compactions);
    }

    [TestMethod]
    public void Usage_HistogramsPutRequestsInFixedSteps()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .Usage(T0.AddSeconds(1), "r1", model: "m", input: 1000, output: 10)
            .Usage(T0.AddSeconds(2), "r1", model: "m", input: 1001, output: 10)
            .Usage(T0.AddSeconds(3), "r1", model: "m", input: 100000, output: 10);

        var batch = Reduce(b);

        var input = batch.Histograms.Where(static pair => pair.Key.Measure == HistogramMeasure.RequestInputTokens).ToDictionary(static pair => pair.Key.Step, static pair => pair.Value);
        Assert.AreEqual(2, input[HistogramSteps.StepOf(1000)]);
        Assert.AreEqual(1, input[HistogramSteps.StepOf(100000)]);
        Assert.AreEqual(3, batch.Histograms.Single(static pair => pair.Key.Measure == HistogramMeasure.RequestOutputTokens).Value);
    }

    // ----- tools -----

    [TestMethod]
    public void Tool_IsCountedWhenItStartsAndMeasuredWhenItEnds()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .Requested(T0.AddSeconds(1), "r1", "t1", "read_file")
            .ToolStarted(T0.AddSeconds(1), "r1", "t1", "read_file", "{\"path\":\"a.cs\"}")
            .ToolOutput(T0.AddSeconds(3), "r1", "t1", new string('o', 4000))
            .ToolDone(T0.AddSeconds(3.5), "r1", "t1", "read_file", argsJson: "{\"path\":\"a.cs\"}", readFiles: ["C:\\code\\a.cs", "C:\\code\\b.cs"])
            .ToolStarted(T0.AddSeconds(4), "r1", "t2", "apply_patch", "{\"input\":\"x\"}", modifiedFiles: ["C:\\code\\a.cs", "readme.md"])
            .ToolDone(T0.AddSeconds(6), "r1", "t2", "apply_patch", argsJson: "{\"input\":\"x\"}", modifiedFiles: ["C:\\code\\a.cs", "readme.md"], diff: "--- a\n+++ b\n@@\n-x\n+y\n+z")
            .ToolStarted(T0.AddSeconds(7), "r1", "t3", "read_file", "{\"path\":\"missing\"}")
            .ToolDone(T0.AddSeconds(7.1), "r1", "t3", "read_file", phase: "Failed", argsJson: "{\"path\":\"missing\"}")
            .Idle(T0.AddSeconds(10), "r1");

        var batch = Reduce(b);

        var read = batch.Tools.Single(static pair => pair.Key.Tool == "ToolCall:read_file");
        Assert.AreEqual(ToolKind.Files, read.Key.Kind);
        Assert.AreEqual("codex", read.Key.Provider);
        Assert.AreEqual(2, read.Value.Calls);
        Assert.AreEqual(1, read.Value.Failures);
        Assert.AreEqual(2, read.Value.DurationCount);
        Assert.AreEqual(2500 + 100, read.Value.DurationMsTotal);
        Assert.AreEqual(2500, read.Value.DurationMsMax);
        Assert.AreEqual(2, read.Value.FilesRead);
        Assert.AreEqual("{\"path\":\"a.cs\"}".Length + "{\"path\":\"missing\"}".Length, read.Value.BytesIn);
        Assert.IsTrue(read.Value.BytesOut > 0);

        var patch = batch.Tools.Single(static pair => pair.Key.Tool == "ToolCall:apply_patch").Value;
        Assert.AreEqual(1, patch.Calls);
        Assert.AreEqual(2, patch.FilesChanged);
        Assert.AreEqual(2, patch.LinesAdded);
        Assert.AreEqual(1, patch.LinesRemoved);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.ChangedFileExtension, "cs")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.ChangedFileExtension, "md")]);

        Assert.AreEqual(3, batch.Runs["r1"].ToolCalls);
        Assert.AreEqual(1, batch.Runs["r1"].ToolFailures);
        Assert.AreEqual(2500, batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.LongestToolMs, "ToolCall:read_file")].Value);
        Assert.AreEqual(3, batch.Extremes[new ExtremeKey(QuarterHour.Of(T0), ExtremeMeasure.MostToolCallsInRun, string.Empty)].Value);
    }

    [TestMethod]
    public void Tool_BucketsAndKindsFollowTheRulesOfTheTimelineCards()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .ToolStarted(T0.AddSeconds(1), "r1", "a", "shell_command", "{\"command\":\"git status\"}")
            .ToolStarted(T0.AddSeconds(2), "r1", "b", "PowerShell", "{\"command\":\"dotnet build\"}")
            .ToolStarted(T0.AddSeconds(3), "r1", "c", "alta", "{\"args\":[\"session\",\"create\",\"--project\",\"x\"]}")
            .ToolStarted(T0.AddSeconds(4), "r1", "d", "mcp__codealta_dev__take_snapshot", "{}")
            .ToolStarted(T0.AddSeconds(5), "r1", "e", "mcp__codealta__alta", "{\"args\":[\"task\",\"list\"]}")
            .ToolStarted(T0.AddSeconds(6), "r1", "f", "grep", "{\"pattern\":\"x\"}")
            .ToolStarted(T0.AddSeconds(7), "r1", "g", "webget", "{\"url\":\"u\"}")
            .ToolStarted(T0.AddSeconds(8), "r1", "h", "codealta_skills_activate", "{\"skillName\":\"ilspy-decompile\"}")
            .ToolStarted(T0.AddSeconds(9), "r1", "i", "inspect_two", "{}")
            .Idle(T0.AddSeconds(10), "r1");

        var batch = Reduce(b);

        ToolKind KindOf(string tool) => batch.Tools.Single(pair => pair.Key.Tool == tool).Key.Kind;
        Assert.AreEqual(ToolKind.Shell, KindOf("shell"));
        Assert.AreEqual(2, batch.Tools.Single(static pair => pair.Key.Tool == "shell").Value.Calls);
        Assert.AreEqual(ToolKind.Alta, KindOf("ToolCall:alta"));
        Assert.AreEqual(ToolKind.Mcp, KindOf("ToolCall:mcp__codealta_dev__take_snapshot"));
        Assert.AreEqual(ToolKind.Alta, KindOf("ToolCall:mcp__codealta__alta"));
        Assert.AreEqual(ToolKind.Search, KindOf("ToolCall:grep"));
        Assert.AreEqual(ToolKind.Web, KindOf("ToolCall:webget"));
        Assert.AreEqual(ToolKind.Skill, KindOf("ToolCall:codealta_skills_activate"));
        Assert.AreEqual(ToolKind.Other, KindOf("ToolCall:inspect_two"));

        var q = QuarterHour.Of(T0);
        Assert.AreEqual(1, batch.Details[new DetailKey(q, DetailList.ShellProgram, "git")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(q, DetailList.ShellProgram, "dotnet")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(q, DetailList.AltaCommand, "session create")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(q, DetailList.AltaCommand, "task list")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(q, DetailList.Skill, "ilspy-decompile")]);
    }

    [TestMethod]
    public void Tool_EndWithoutAStartStillCountsTheCall()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").ToolDone(T0.AddSeconds(1), "r1", "x", "read_file", readFiles: ["a.cs"]);

        var batch = Reduce(b);

        var measures = batch.Tools.Single().Value;
        Assert.AreEqual(1, measures.Calls);
        Assert.AreEqual(0, measures.DurationCount);
        Assert.AreEqual(1, measures.FilesRead);
    }

    [TestMethod]
    public void Tool_ACallOfAnOlderKindIsBucketedLikeTheCards()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .Add($"{{\"$type\":\"activity\",\"kind\":\"CommandExecution\",\"phase\":\"Started\",\"activityId\":\"c1\",\"name\":\"ls\",{b.Envelope(T0.AddSeconds(1), "r1")}")
            .Add($"{{\"$type\":\"activity\",\"kind\":\"CommandExecution\",\"phase\":\"Completed\",\"activityId\":\"c1\",\"name\":\"ls\",{b.Envelope(T0.AddSeconds(2), "r1")}")
            .Add($"{{\"$type\":\"activity\",\"kind\":\"McpToolCall\",\"phase\":\"Started\",\"activityId\":\"m1\",\"name\":\"search\",{b.Envelope(T0.AddSeconds(3), "r1")}")
            .Add($"{{\"$type\":\"activity\",\"kind\":\"WebSearch\",\"phase\":\"Started\",\"activityId\":\"w1\",\"name\":\"query\",{b.Envelope(T0.AddSeconds(4), "r1")}");

        var batch = Reduce(b);

        Assert.AreEqual(ToolKind.Shell, batch.Tools.Single(static pair => pair.Key.Tool == "shell").Key.Kind);
        Assert.AreEqual(ToolKind.Mcp, batch.Tools.Single(static pair => pair.Key.Tool == "McpToolCall:search").Key.Kind);
        Assert.AreEqual(ToolKind.Web, batch.Tools.Single(static pair => pair.Key.Tool == "WebSearch:query").Key.Kind);
        Assert.AreEqual(1, batch.Tools.Single(static pair => pair.Key.Tool == "shell").Value.DurationCount);
    }

    // ----- prompts -----

    [TestMethod]
    public void Prompt_OfYouStartsARun_AndItsSizeIsCounted()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1", "fix the failing test please", ["file", "localImage"]).Idle(T0.AddSeconds(5), "r1");

        var batch = Reduce(b);

        var content = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Prompt);
        Assert.AreEqual(PromptSender.You, content.Key.Sender);
        Assert.AreEqual(PromptKind.NewTurn, content.Key.PromptKind);
        Assert.AreEqual(1, content.Value.Count);
        Assert.AreEqual(27, content.Value.Chars);
        Assert.AreEqual(5, content.Value.Words);
        Assert.AreEqual(1, content.Value.Files);
        Assert.AreEqual(1, content.Value.Images);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.RunOrigin, "you")]);
        Assert.AreEqual(1, batch.Histograms.Single(static pair => pair.Key.Measure == HistogramMeasure.PromptChars).Value);
        Assert.AreEqual(27, batch.Runs["r1"].PromptChars);
    }

    [TestMethod]
    public void Prompt_SentIntoARunThatWasGoingIsASteer()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1", "first").User(T0.AddSeconds(20), "r1", "wait, one more thing").Idle(T0.AddSeconds(30), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(1, batch.Content.Single(static pair => pair.Key.PromptKind == PromptKind.NewTurn).Value.Count);
        Assert.AreEqual(1, batch.Content.Single(static pair => pair.Key.PromptKind == PromptKind.Steer).Value.Count);
        Assert.AreEqual(1, batch.Details.Single(static pair => pair.Key.List == DetailList.RunOrigin).Value, "only the first prompt gives the run its origin");
    }

    [TestMethod]
    public void Prompt_OfAnotherAgent_IsAKnownByItsSourceSession()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1", "from a parent", sourceSession: "55555555-5555-5555-5555-555555555555").Idle(T0.AddSeconds(5), "r1");

        var batch = Reduce(b);

        var content = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Prompt);
        Assert.AreEqual(PromptSender.Agent, content.Key.Sender);
        Assert.AreEqual(0, batch.Histograms.Count(static pair => pair.Key.Measure == HistogramMeasure.PromptChars), "the size of the prompts of other senders is not a size of yours");
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.RunOrigin, "agent")]);
    }

    [TestMethod]
    public void Prompt_AnsweringAQuestionIsAnAnswer()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1", "yes", answer: true).Idle(T0.AddSeconds(5), "r1");

        var batch = Reduce(b);

        Assert.AreEqual(PromptKind.Answer, batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Prompt).Key.PromptKind);
    }

    [TestMethod]
    public void Prompt_ProvenanceWrittenAfterThePromptCorrectsWhatWasCounted()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .User(T0.AddSeconds(1), "r1", "reminder text here")
            .State(T0.AddSeconds(1.1), provenance: [JournalBuilder.Provenance("p1", "send", T0.AddSeconds(1.1), queued: false, submittedByKind: "reminder")])
            .Idle(T0.AddSeconds(5), "r1");

        var batch = Reduce(b);

        var prompts = batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt && !pair.Value.IsZero).ToArray();
        Assert.AreEqual(1, prompts.Length);
        Assert.AreEqual(PromptSender.Reminder, prompts[0].Key.Sender);
        Assert.AreEqual(1, prompts[0].Value.Count);
        Assert.AreEqual(0, batch.Histograms.Where(static pair => pair.Key.Measure == HistogramMeasure.PromptChars).Sum(static pair => pair.Value), "the prompt of a reminder is not one of yours");
        Assert.AreEqual(1, batch.Details.Single(static pair => pair.Key.List == DetailList.RunOrigin && pair.Value != 0).Value);
        Assert.AreEqual("reminder", batch.Details.Single(static pair => pair.Key.List == DetailList.RunOrigin && pair.Value != 0).Key.Name);
        Assert.AreEqual(PromptSender.Reminder, batch.Runs["r1"].Sender);
    }

    [TestMethod]
    public void Prompt_ProvenanceWrittenBeforeThePromptIsQueuedWork_OfAnAgent()
    {
        var b = new JournalBuilder();
        b.State(T0, provenance: [JournalBuilder.Provenance("p1", "parent-notify", T0.AddSeconds(5), queued: true, submittedByKind: "agent", sourceSession: "55555555-5555-5555-5555-555555555555")], queued: 1)
            .ModelChanged(T0.AddSeconds(5.01), "r1", "codex", "m", "Low")
            .User(T0.AddSeconds(5.03), "r1", "child report")
            .Idle(T0.AddSeconds(8), "r1");

        var batch = Reduce(b);

        var content = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Prompt);
        Assert.AreEqual(PromptSender.Agent, content.Key.Sender);
        Assert.AreEqual(PromptKind.Queued, content.Key.PromptKind);
    }

    [TestMethod]
    public void Prompt_ProvenanceEntriesAreTakenOnlyOnce_WhenEveryStateRepeatsThem()
    {
        var b = new JournalBuilder();
        var entry = JournalBuilder.Provenance("p1", "send", T0.AddSeconds(1.1), submittedByKind: "automation");
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .User(T0.AddSeconds(1), "r1", "automated prompt")
            .State(T0.AddSeconds(1.1), provenance: [entry])
            .State(T0.AddSeconds(2), provenance: [entry])
            .State(T0.AddSeconds(3), provenance: [entry])
            .Idle(T0.AddSeconds(5), "r1");

        var result = CatchUp(b);

        var prompts = result.Batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt && !pair.Value.IsZero).ToArray();
        Assert.AreEqual(1, prompts.Length);
        Assert.AreEqual(PromptSender.Automation, prompts[0].Key.Sender);
        Assert.AreEqual(1, result.Cursor.State.SeenPrompts.Count);
    }

    // ----- contents, instructions -----

    [TestMethod]
    public void Content_OfAnswersAndReasoningIsCounted()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .Reasoning(T0.AddSeconds(1), "r1", "hmm let me think")
            .Reasoning(T0.AddSeconds(2), "r1", "ok")
            .Assistant(T0.AddSeconds(3), "r1", "here is the answer to your question")
            .SystemPrompt(T0.AddSeconds(4), "r1", 100, 400)
            .Idle(T0.AddSeconds(5), "r1");

        var batch = Reduce(b);

        var reasoning = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Reasoning).Value;
        Assert.AreEqual(2, reasoning.Count);
        Assert.AreEqual(18, reasoning.Chars);
        Assert.AreEqual(0, reasoning.Words, "reasoning is counted in characters");
        var answer = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Answer).Value;
        Assert.AreEqual(1, answer.Count);
        Assert.AreEqual(35, answer.Chars);
        Assert.AreEqual(7, answer.Words);
        var instructions = batch.Content.Single(static pair => pair.Key.Kind == ContentKind.Instructions).Value;
        Assert.AreEqual(1, instructions.Count);
        Assert.AreEqual(500, instructions.Chars);
        Assert.AreEqual(125, instructions.ApproxTokens);
        Assert.AreEqual(35, batch.Runs["r1"].AnswerChars);
    }

    // ----- identity -----

    [TestMethod]
    public void Session_IdentityComesFromTheHeaderAndTheState()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "codex");
        b.Header(T0, parent: "44444444-4444-4444-4444-444444444444", createdByKind: "agent", createdBySession: "44444444-4444-4444-4444-444444444444", title: "Child task")
            .State(T0.AddSeconds(1), permission: "ask")
            .ModelChanged(T0.AddSeconds(2), "r1", "codex", "m", "Low")
            .Idle(T0.AddSeconds(3), "r1");

        var batch = Reduce(b);

        var session = batch.Session!;
        Assert.AreEqual("33333333-3333-3333-3333-333333333333", session.SessionId);
        Assert.AreEqual("22222222-2222-2222-2222-222222222222", session.ProjectRef);
        Assert.AreEqual("ProjectSession", session.SessionKind);
        Assert.AreEqual("44444444-4444-4444-4444-444444444444", session.ParentSessionId);
        Assert.AreEqual("agent", session.CreatedByKind);
        Assert.AreEqual("44444444-4444-4444-4444-444444444444", session.CreatedBySessionId);
        Assert.AreEqual("Child task", session.Title);
        Assert.AreEqual("codex", session.Provider);
        Assert.AreEqual("ask", session.PermissionMode);
        Assert.AreEqual(T0, session.FirstRecord);
        Assert.AreEqual(T0.AddSeconds(3), session.LastRecord);
        Assert.IsTrue(session.HasHeader);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.SessionOrigin, "agent")]);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.PermissionMode, "ask")]);
        Assert.AreEqual("ask", batch.Runs["r1"].PermissionMode);
    }

    [TestMethod]
    public void Session_OfTheLegacyHeaderAndWithoutACreator_IsARootSession()
    {
        var b = new JournalBuilder();
        b.Header(T0, legacy: true, kind: "ProjectThread").State(T0.AddSeconds(1), legacy: true);

        var batch = Reduce(b);

        Assert.AreEqual("ProjectThread", batch.Session!.SessionKind);
        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.SessionOrigin, "root")]);
    }

    [TestMethod]
    public void Session_WithAParentButNoCreator_IsAChild()
    {
        var b = new JournalBuilder();
        b.Header(T0, parent: "44444444-4444-4444-4444-444444444444");

        var batch = Reduce(b);

        Assert.AreEqual(1, batch.Details[new DetailKey(QuarterHour.Of(T0), DetailList.SessionOrigin, "child")]);
    }

    // ----- state, restart -----

    [TestMethod]
    public void State_SerializesToJsonAndResumes()
    {
        var b = new JournalBuilder();
        b.Header(T0).ModelChanged(T0.AddSeconds(1), "r1", "codex", "m", "Low").User(T0.AddSeconds(2), "r1").ToolStarted(T0.AddSeconds(3), "r1", "t1", "read_file");

        var result = CatchUp(b);
        var json = result.Cursor.State.ToJson();
        var restored = SessionFactsState.FromJson(json);

        Assert.AreEqual(json, restored.ToJson());
        Assert.AreEqual(1, restored.OpenRuns.Count);
        Assert.AreEqual(1, restored.OpenTools.Count);
        Assert.AreEqual("m", restored.Model);
        Assert.IsTrue(json.Length < 2000, "the state stays small");
    }

    [TestMethod]
    public void State_OfAnotherVersionIsNotUsed()
    {
        var b = new JournalBuilder();
        b.Header(T0).ModelChanged(T0.AddSeconds(1), "r1", "codex", "m", "Low").User(T0.AddSeconds(2), "r1").Idle(T0.AddSeconds(3), "r1");
        var first = CatchUp(b);
        var old = new JournalCursor(first.Cursor.Offset, first.Cursor.FirstLine, new SessionFactsState { Version = 0 });

        var again = CatchUp(b, old);

        Assert.IsTrue(again.Restarted);
        Assert.AreEqual(first.Batch.ToCanonicalText(), again.Batch.ToCanonicalText());
    }

    [TestMethod]
    public void CatchUp_OfARewrittenFileRestartsFromTheStart()
    {
        var b = new JournalBuilder();
        b.Header(T0).ModelChanged(T0.AddSeconds(1), "r1", "codex", "m", "Low").User(T0.AddSeconds(2), "r1").Idle(T0.AddSeconds(3), "r1");
        var first = CatchUp(b);

        var rewritten = new JournalBuilder();
        rewritten.Header(T0.AddSeconds(-5), title: "Prepended").Add(b.Lines[0]).Add(b.Lines[1]).Add(b.Lines[2]).Add(b.Lines[3]);
        var again = CatchUp(rewritten, first.Cursor);

        Assert.IsTrue(again.Restarted);
        Assert.AreEqual(rewritten.ToBytes().Length, again.Cursor.Offset);
        Assert.AreEqual(1, again.Batch.Runs.Count);
        Assert.AreEqual(0, again.Scan.StartOffset);
    }

    [TestMethod]
    public void CatchUp_LeavesTheSavedStateAsItWas()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low").User(T0.AddSeconds(1), "r1");
        var first = CatchUp(b);
        var saved = first.Cursor.State.ToJson();

        b.Idle(T0.AddSeconds(5), "r1");
        var second = CatchUp(b, first.Cursor);

        Assert.AreEqual(saved, first.Cursor.State.ToJson(), "a catch-up that is not saved must not change the saved state");
        Assert.AreEqual(0, second.Cursor.State.OpenRuns.Count);
    }

    // ----- histograms -----

    [TestMethod]
    public void HistogramSteps_AreFixedAndAboutNineteenPercentWider()
    {
        Assert.IsTrue(HistogramSteps.Count is >= 105 and <= 115, $"steps: {HistogramSteps.Count}");
        Assert.AreEqual(1, HistogramSteps.LowerBound(0));
        Assert.AreEqual(0, HistogramSteps.StepOf(0));
        Assert.AreEqual(0, HistogramSteps.StepOf(-5));
        Assert.AreEqual(0, HistogramSteps.StepOf(1));
        Assert.IsTrue(HistogramSteps.LowerBound(HistogramSteps.Count - 1) >= 86_400_000);
        Assert.AreEqual(HistogramSteps.Count - 1, HistogramSteps.StepOf(long.MaxValue));
        for (var step = 0; step < HistogramSteps.Count; step++)
        {
            Assert.AreEqual(step, HistogramSteps.StepOf(HistogramSteps.LowerBound(step)));
            if (step > 0)
            {
                Assert.AreEqual(step - 1, HistogramSteps.StepOf(HistogramSteps.LowerBound(step) - 1));
            }
        }

        // From the 30th step on (a value of about a hundred) each step is about 19% wider than the one before.
        for (var step = 30; step < HistogramSteps.Count - 1; step++)
        {
            var ratio = (double)HistogramSteps.LowerBound(step + 1) / HistogramSteps.LowerBound(step);
            Assert.IsTrue(ratio is > 1.185 and < 1.205, $"step {step}: {ratio}");
        }
    }
}
