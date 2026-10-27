using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

[TestClass]
public sealed class CodeModeReplayTests
{
    private static readonly CodeModeApi Api = new([new CodeModeFunction("tools.echo"), new CodeModeFunction("tools.slow")]);

    // Each step calls tools.echo with its name, so the calls say which steps ran.
    private const string ThreeSteps = """
        const a = await step("a", async () => (await tools.echo({ n: "a" })).n + "1");
        const b = await step("b", async () => (await tools.echo({ n: "b" })).n + "2");
        const c = await step("c", async () => (await tools.echo({ n: "c" })).n + "3");
        return [a, b, c];
        """;

    private static async Task<(CodeModeRunResult Result, DelegateHandler Handler)> RunAsync(string code, CodeModeRunOptions? options = null, ICodeModeRunObserver? observer = null)
    {
        var handler = new DelegateHandler().On("tools.echo", args => args).On("tools.slow", async (_, token) =>
        {
            await Task.Delay(200, token);
            return CodeModeCallResult.Text("slow");
        });
        var result = await new CodeModeHost(Api, new CodeModeScriptCache()).RunAsync(CodeModeSource.Prepare(code), handler, options, observer);
        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        return (result, handler);
    }

    private static IEnumerable<CodeModeReplayStep> Recorded(CodeModeRunResult result)
        => result.Steps.Where(static step => step.Key is not null).Select(static step => new CodeModeReplayStep(step.Index, step.Key!, step.Succeeded)
        {
            Id = step.StepId,
            ParentIndex = step.ParentIndex,
            HasResult = step.HasResult,
            ResultJson = step.ResultJson,
        });

    private static string[] Echoed(DelegateHandler handler)
        => [.. handler.Calls.Where(static call => call.Path == "tools.echo").Select(static call => JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("n").GetString()!)];

    private static string Output(CodeModeRunResult result) => string.Join("\n", result.Output.Select(static entry => entry.Text));

    [TestMethod]
    public async Task Steps_RecordTheirKeyAndTheirResult()
    {
        var (result, _) = await RunAsync("""
            await step("a", async () => ({ n: 1 }));
            await step("a", async () => undefined);
            await step("b", async () => () => 1);
            try { await step("c", async () => { throw new Error("no"); }); } catch { }
            await step("big", async () => "x".repeat(300 * 1024));
            """);

        var steps = result.Steps.ToDictionary(static step => step.Key!);
        CollectionAssert.AreEquivalent(new[] { "a#0", "a#1", "b#0", "c#0", "big#0" }, steps.Keys.ToArray());
        Assert.IsTrue(steps["a#0"].HasResult);
        Assert.AreEqual("""{"n":1}""", steps["a#0"].ResultJson);
        Assert.IsTrue(steps["a#1"].HasResult, "undefined is a result");
        Assert.IsNull(steps["a#1"].ResultJson);
        Assert.IsFalse(steps["b#0"].HasResult, "a function is not JSON");
        Assert.IsFalse(steps["c#0"].Succeeded);
        Assert.IsFalse(steps["c#0"].HasResult);
        Assert.IsFalse(steps["big#0"].HasResult, "a value over 256 KB is not recorded");
        Assert.IsNull(steps["big#0"].ResultJson);
    }

    [TestMethod]
    public async Task Resume_ReplaysTheStepsThatCompleted_AndRunsTheRestLive()
    {
        var (first, _) = await RunAsync(ThreeSteps);
        // The run stopped during c: c never ended.
        var recorded = Recorded(first).Where(static step => step.Key != "c#0");
        var observer = new ReplayObserver();

        var (resumed, handler) = await RunAsync(ThreeSteps, new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) }, observer);

        CollectionAssert.AreEqual(new[] { "c" }, Echoed(handler), "the steps that completed do not run their function");
        Assert.AreEqual(first.ValueJson, resumed.ValueJson, "a replayed step resolves with its recorded value");
        StringAssert.Contains(Output(resumed), "Resume: step `c` has no recorded result; it and what follows run live.");
        CollectionAssert.AreEqual(new[] { "a#0", "b#0" }, observer.Replayed.ToArray());
        Assert.IsTrue(resumed.Steps.Single(static step => step.Key == "a#0").Replayed);
        Assert.AreEqual("\"a1\"", resumed.Steps.Single(static step => step.Key == "a#0").ResultJson, "a replayed step is recorded again, for a later resume");
    }

    [TestMethod]
    public async Task Resume_StopsAtTheFirstStepThatDiverges_AndSaysWhichOccurrence()
    {
        var recorded = new[]
        {
            new CodeModeReplayStep(0, "a#0", true) { HasResult = true, ResultJson = "1" },
            new CodeModeReplayStep(1, "merge#0", true) { HasResult = true, ResultJson = "2" },
            new CodeModeReplayStep(2, "x#0", true) { HasResult = true, ResultJson = "3" },
            new CodeModeReplayStep(3, "merge#1", true) { HasResult = true, ResultJson = "4" },
        };

        var (result, handler) = await RunAsync("""
            const values = [];
            for (const id of ["a", "merge", "merge", "x"]) values.push(await step(id, async () => (await tools.echo({ n: id })).n));
            return values;
            """, new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) });

        Assert.AreEqual("""[1,2,"merge","x"]""", result.ValueJson);
        CollectionAssert.AreEqual(new[] { "merge", "x" }, Echoed(handler), "once diverged, every step runs, also one the record has");
        Assert.AreEqual("Resume: diverged at step `merge` (2nd occurrence); it and what follows run live.", result.Output.Single().Text);
    }

    [TestMethod]
    public async Task All_ReplaysEveryRecordedStep_WhereverItIs()
    {
        var (first, _) = await RunAsync(ThreeSteps);

        var (rerun, handler) = await RunAsync("""
            const c = await step("c", async () => (await tools.echo({ n: "c" })).n);
            const added = await step("added", async () => (await tools.echo({ n: "added" })).n);
            const a = await step("a", async () => (await tools.echo({ n: "a" })).n);
            return [a, added, c].join(" ");
            """, new CodeModeRunOptions { Replay = new CodeModeReplay(Recorded(first), CodeModeReplayMode.All) });

        Assert.AreEqual("\"a1 added c3\"", rerun.ValueJson, "the changed code runs over the recorded results");
        CollectionAssert.AreEqual(new[] { "added" }, Echoed(handler));
        Assert.AreEqual("Rerun: step `added` has no recorded result; it runs live.", rerun.Output.Single().Text);
    }

    [TestMethod]
    public async Task UntilStep_ReplaysTheStepsBefore_AndRunsThatStepAndWhatFollows()
    {
        var (first, _) = await RunAsync(ThreeSteps);

        var (rerun, handler) = await RunAsync(ThreeSteps, new CodeModeRunOptions { Replay = new CodeModeReplay(Recorded(first), CodeModeReplayMode.UntilStep, "b") });

        CollectionAssert.AreEqual(new[] { "b", "c" }, Echoed(handler));
        Assert.AreEqual("Rerun from step `b`: it and what follows run live.", rerun.Output.Single().Text);
    }

    [TestMethod]
    public async Task AReplayedStep_ReplaysTheStepsItStarted_AndKeepsTheOccurrencesThatFollow()
    {
        const string code = """
            const outer = await step("outer", async () => {
              const one = await step("read", async () => (await tools.echo({ n: "read-1" })).n);
              const two = await step("read", async () => (await tools.echo({ n: "read-2" })).n);
              return one + "+" + two;
            });
            const last = await step("read", async () => (await tools.echo({ n: "read-3" })).n);
            return [outer, last];
            """;
        var (first, _) = await RunAsync(code);
        var recorded = Recorded(first).Where(static step => step.Key != "read#2").ToArray();
        Assert.AreEqual(first.Steps.Single(static step => step.Key == "outer#0").Index, recorded.Single(static step => step.Key == "read#0").ParentIndex);

        var (resumed, handler) = await RunAsync(code, new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) });

        CollectionAssert.AreEqual(new[] { "read-3" }, Echoed(handler), "the steps outer started are not started again, and read#2 is the third read");
        Assert.AreEqual(first.ValueJson, resumed.ValueJson);
        CollectionAssert.AreEqual(new[] { "outer#0", "read#0", "read#1" }, resumed.Steps.Where(static step => step.Replayed).Select(static step => step.Key).ToArray());
        Assert.AreEqual("read#2", resumed.Steps.Single(static step => !step.Replayed).Key);
    }

    // Library scripts by name: count-steps reads twice in `read`, then counts; outer starts count-steps in a step.
    private static readonly FrozenDictionary<string, string> Library = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["count-steps"] = """
            const read = await step("read", async () => [(await tools.echo({ n: "a.txt" })).n, (await tools.echo({ n: "b.txt" })).n]);
            return await step("count", async () => (await tools.echo({ n: "count" })).n + ":" + read.length);
            """,
        ["first"] = """return [await step("read", async () => (await tools.echo({ n: "first-1" })).n), await step("read", async () => (await tools.echo({ n: "first-2" })).n)];""",
        ["second"] = """return [await step("read", async () => (await tools.echo({ n: "second" })).n), await workflow("inner")];""",
        ["inner"] = """return await step("read", async () => (await tools.echo({ n: "inner" })).n);""",
        // The first call waits before its step: the second call reaches its step first.
        ["same"] = """if (args.wait) await tools.slow({}); return await step("read", async () => (await tools.echo({ n: args.n })).n);""",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static CodeModeRunOptions WithLibrary(CodeModeReplay? replay = null) => new()
    {
        Replay = replay,
        ResolveScript = (reference, _) => ValueTask.FromResult(CodeModeSource.Prepare(Library[reference.Name ?? Path.GetFileNameWithoutExtension(reference.ScriptPath!)], "lib:" + reference.Name)),
    };

    [TestMethod]
    public async Task TheStepsOfAWorkflowScript_HaveItsNameInTheirKey_AndAreReplayed()
    {
        const string code = """return await workflow("count-steps", {});""";
        var (first, firstHandler) = await RunAsync(code, WithLibrary());
        CollectionAssert.AreEqual(new[] { "count-steps#0/read#0", "count-steps#0/count#0" }, first.Steps.Select(static step => step.Key).ToArray());
        Assert.AreEqual("read", first.Steps[0].StepId, "the card shows the id the script gave");
        CollectionAssert.AreEqual(new[] { "count-steps#0/read#0", "count-steps#0/read#0", "count-steps#0/count#0" }, firstHandler.Calls.Select(static call => call.StepKey).ToArray(),
            "the calls of a nested step carry its key");

        var (rerun, handler) = await RunAsync(code, WithLibrary(new CodeModeReplay(Recorded(first), CodeModeReplayMode.All)));

        Assert.AreEqual(first.ValueJson, rerun.ValueJson);
        Assert.IsEmpty(handler.Calls, "every step of the library script gives its recorded result");
        Assert.IsTrue(rerun.Steps.All(static step => step.Replayed));
    }

    [TestMethod]
    public async Task AResume_ReplaysTheNestedStepsBeforeTheOneThatStopped_AndARerunFromAStepTakesItsBareId()
    {
        const string code = """return await workflow("count-steps", {});""";
        var (first, _) = await RunAsync(code, WithLibrary());
        // CodeAlta ended during count: it never ended.
        var interrupted = Recorded(first).Where(static step => step.Key != "count-steps#0/count#0");

        var (resumed, handler) = await RunAsync(code, WithLibrary(new CodeModeReplay(interrupted, CodeModeReplayMode.Resume)));
        var (fromCount, fromHandler) = await RunAsync(code, WithLibrary(new CodeModeReplay(Recorded(first), CodeModeReplayMode.UntilStep, "count")));

        Assert.AreEqual(first.ValueJson, resumed.ValueJson);
        CollectionAssert.AreEqual(new[] { "count" }, Echoed(handler));
        StringAssert.Contains(Output(resumed), "Resume: step `count-steps#0/count` has no recorded result");
        CollectionAssert.AreEqual(new[] { "count" }, Echoed(fromHandler), "`count` names count-steps#0/count, the only step of that id");
        StringAssert.Contains(Output(fromCount), "Rerun from step `count-steps#0/count`");
    }

    [TestMethod]
    public async Task TwoWorkflowScriptsInParallel_KeepTheirOwnKeys_AndAreReplayed()
    {
        const string code = """return await Promise.all([workflow("first", {}), workflow("second", {})]);""";
        var (first, _) = await RunAsync(code, WithLibrary());
        CollectionAssert.AreEquivalent(new[] { "first#0/read#0", "first#0/read#1", "second#0/read#0", "second#0/inner#0/read#0" }, first.Steps.Select(static step => step.Key).ToArray());
        Assert.AreEqual(first.Steps.Count, first.Steps.Select(static step => step.Index).Distinct().Count(), "one counter of indexes for the run and its scripts");

        var (rerun, handler) = await RunAsync(code, WithLibrary(new CodeModeReplay(Recorded(first), CodeModeReplayMode.All)));

        Assert.AreEqual("""[["first-1","first-2"],["second","inner"]]""", rerun.ValueJson);
        Assert.IsEmpty(handler.Calls);
    }

    [TestMethod]
    public async Task TwoParallelCallsOfOneScript_AreNumberedInCallOrder_AndKeepTheirOwnResults()
    {
        const string code = """return await Promise.all([workflow("same", { n: "x", wait: true }), workflow("same", { n: "y" })]);""";
        var (first, _) = await RunAsync(code, WithLibrary());
        var keys = first.Steps.OrderBy(static step => step.Index).Select(static step => step.Key).ToArray();
        CollectionAssert.AreEqual(new[] { "same#1/read#0", "same#0/read#0" }, keys, "the second call reached its step first, and keeps the number of its call");

        var (rerun, handler) = await RunAsync(code, WithLibrary(new CodeModeReplay(Recorded(first), CodeModeReplayMode.All)));

        Assert.AreEqual("""["x","y"]""", rerun.ValueJson, "each call gives its own recorded result");
        Assert.IsEmpty(Echoed(handler));
    }

    [TestMethod]
    public void Keys_ReadAndDescribeTheirOccurrence()
    {
        Assert.AreEqual("merge#0", CodeModeReplay.NormalizeKey("merge"));
        Assert.AreEqual(("a#b", 2), CodeModeReplay.ParseKey("a#b#2"));
        Assert.AreEqual("`merge`", CodeModeReplay.Describe("merge#0"));
        Assert.AreEqual("`merge` (2nd occurrence)", CodeModeReplay.Describe("merge#1"));
        Assert.AreEqual("`merge` (12th occurrence)", CodeModeReplay.Describe("merge#11"));
        Assert.AreEqual("`merge` (23rd occurrence)", CodeModeReplay.Describe("merge#22"));
        Assert.AreEqual("`lib#1/merge`", CodeModeReplay.Describe("lib#1/merge#0"));
    }

    [TestMethod]
    public void ATypedKey_IsTheFullKey_ThePathWithoutCallNumbers_OrABareId_WhenItNamesOneStep()
    {
        string[] keys = ["a#0", "lib#0/count#0", "lib#0/read#0", "lib#0/read#1", "other#0/read#0", "same#0/read#0", "same#1/read#0", "a#0/b#0/write#0"];

        Assert.AreEqual("lib#0/count#0", CodeModeReplay.ResolveKey("count", keys, out _));
        Assert.AreEqual("lib#0/read#1", CodeModeReplay.ResolveKey("lib/read#1", keys, out _));
        Assert.AreEqual("lib#0/read#1", CodeModeReplay.ResolveKey("lib#0/read#1", keys, out _));
        Assert.AreEqual("a#0/b#0/write#0", CodeModeReplay.ResolveKey("a/b/write", keys, out _));
        Assert.AreEqual("same#1/read#0", CodeModeReplay.ResolveKey("same#1/read", keys, out _));
        Assert.AreEqual("a#0", CodeModeReplay.ResolveKey("a", keys, out _));
        Assert.IsNull(CodeModeReplay.ResolveKey("same/read", keys, out var calls));
        CollectionAssert.AreEqual(new[] { "same#0/read", "same#1/read" }, calls.ToArray());
        Assert.IsNull(CodeModeReplay.ResolveKey("read", keys, out var ids));
        CollectionAssert.AreEqual(new[] { "lib#0/read", "other#0/read", "same#0/read", "same#1/read" }, ids.ToArray());
        Assert.IsNull(CodeModeReplay.ResolveKey("nope", keys, out _));
    }

    private sealed class ReplayObserver : ICodeModeRunObserver
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Replayed { get; } = new();

        public void StepEnded(CodeModeStepRecord record)
        {
            if (record.Replayed)
            {
                Replayed.Enqueue(record.Key!);
            }
        }
    }
}
