using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The durable record of the runs of scripts, and the resumes and reruns planned over it.</summary>
[TestClass]
public sealed class CodeModeRunStoreTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task ARun_IsKeptAsItGoes_WithItsStepsAndTheCallsLeftInFlight()
    {
        using var folder = new TemporaryFolder();
        var journal = new List<AgentWorkflowEvent>();
        await using var runs = new CodeModeRunRegistry(null) { Journal = (_, record) => { lock (journal) journal.Add(record); return Task.CompletedTask; } };
        await runs.UseStoreAsync(new CodeModeRunStore(folder.Path));
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.RunEnded += id => ended.TrySetResult(id);

        var run = runs.TryStart("session-1", "Deploy", 1000, "call-1",
            new CodeModeRunStart("// @exec: {\"title\": \"Deploy\"}\nreturn 1;") { ArgumentsJson = """{"n":1}""", ResumesRunId = "run-old", IsIdempotent = static path => path == "tools.read_file" },
            (observer, _) =>
            {
                observer.StepStarted("build");
                observer.StepEnded(new CodeModeStepRecord("build", TimeSpan.Zero, TimeSpan.FromMilliseconds(5), true) { Key = "build#0", Index = 0, HasResult = true, ResultJson = """{"ok":true}""" });
                observer.CallStarted(new CodeModeCall(0, "tools.shell_command", """{"command":"make"}""", "build", null) { StepKey = "build#0" });
                observer.CallEnded(new CodeModeCallRecord(0, "tools.shell_command", "build", null, TimeSpan.Zero, TimeSpan.Zero, CodeModeCallStatus.Succeeded));
                observer.CallStarted(new CodeModeCall(1, "tools.shell_command", """{"command":"deploy"}""", "deploy", null) { StepKey = "deploy#0" });
                observer.CallStarted(new CodeModeCall(2, "tools.read_file", """{"path":"a"}""", "deploy", null) { StepKey = "deploy#0" });
                // The same index in a script workflow() started is another call.
                observer.CallStarted(new CodeModeCall(0, "tools.shell_command", """{"command":"nested"}""", null, null) { Scope = 1 });
                observer.CallEnded(new CodeModeCallRecord(0, "tools.shell_command", null, null, TimeSpan.Zero, TimeSpan.Zero, CodeModeCallStatus.Succeeded) { Scope = 1 });
                return Task.FromResult(new CodeModeRunResult(CodeModeRunStatus.Failed, TimeSpan.Zero));
            }, out var refusal);
        Assert.IsNotNull(run, refusal);
        Assert.AreEqual(run.Id, await ended.Task.WaitAsync(Hang));

        var kept = new CodeModeRunStore(folder.Path).TryLoad(run.Id);

        Assert.IsNotNull(kept);
        Assert.AreEqual("session-1", kept.SessionId);
        Assert.AreEqual("Deploy", kept.Title);
        Assert.AreEqual("call-1", kept.CallId);
        Assert.AreEqual("failed", kept.Status);
        Assert.IsNotNull(kept.EndedAt);
        Assert.AreEqual("""{"n":1}""", kept.ArgumentsJson);
        Assert.AreEqual("run-old", kept.ResumesRunId);
        Assert.AreEqual(CodeModeSource.BodyHashOf("return 1;"), kept.SourceHash);
        StringAssert.StartsWith(kept.Source, "// @exec:");
        var step = kept.Steps.Single();
        Assert.AreEqual(("build#0", true, """{"ok":true}"""), (step.Key, step.Replayable, step.ResultJson));
        var inFlight = kept.InFlightCalls.Single();
        Assert.AreEqual(("tools.shell_command", "deploy#0"), (inFlight.Path, inFlight.StepKey));
        StringAssert.Contains(inFlight.Arguments, "deploy");
        Assert.AreEqual("run-old", journal.Single(static record => record.Kind == AgentWorkflowEventKind.RunStarted).ResumesRunId);
        Assert.AreEqual("build#0", journal.Single(static record => record.Kind == AgentWorkflowEventKind.StepEnded).StepKey);
    }

    [TestMethod]
    public async Task AtStartup_ARunWithoutAnEnd_IsInterrupted_AndItsSessionIsTold()
    {
        using var folder = new TemporaryFolder();
        await using var before = new CodeModeRunRegistry(null);
        await before.UseStoreAsync(new CodeModeRunStore(folder.Path));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = before.TryStart("session-1", "Slow", 1000, "call-9", new CodeModeRunStart("return 1;"), async (observer, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
        }, out _)!;
        await started.Task.WaitAsync(Hang);
        await WaitForAsync(() => new CodeModeRunStore(folder.Path).TryLoad(run.Id) is not null);

        // CodeAlta starts again while the run had no end.
        var told = new List<AgentWorkflowEvent>();
        await using var after = new CodeModeRunRegistry(null) { Journal = (session, record) => { lock (told) told.Add(record); return Task.CompletedTask; } };
        await after.UseStoreAsync(new CodeModeRunStore(folder.Path)).WaitAsync(Hang);

        var kept = after.Store!.TryLoad(run.Id)!;
        Assert.AreEqual("interrupted", kept.Status);
        Assert.IsNotNull(kept.EndedAt);
        var record = told.Single();
        Assert.AreEqual((AgentWorkflowEventKind.RunEnded, "interrupted", run.Id, "call-9", "session-1"), (record.Kind, record.Status, record.WorkflowRunId, record.CallId, record.SessionId));
        Assert.ThrowsExactly<InvalidOperationException>(() => after.UseStoreAsync(new CodeModeRunStore(folder.Path)));
    }

    [TestMethod]
    public void Prune_KeepsTheMostRecentRunsThatEnded_WhileTheyAreRecent()
    {
        using var folder = new TemporaryFolder();
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        WriteRun(folder.Path, "run-1h", endedAt: now.AddHours(-1));
        WriteRun(folder.Path, "run-2h", endedAt: now.AddHours(-2));
        WriteRun(folder.Path, "run-3h", endedAt: now.AddHours(-3));
        WriteRun(folder.Path, "run-old", endedAt: now.AddDays(-20));
        WriteRun(folder.Path, "run-live", endedAt: null);
        var store = new CodeModeRunStore(folder.Path, maxEndedRuns: 2, maxAge: TimeSpan.FromDays(14), new FixedTime(now));

        Assert.AreEqual(2, store.Prune());

        CollectionAssert.AreEquivalent(new[] { "run-1h", "run-2h", "run-live" }, Directory.GetDirectories(folder.Path).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void Plan_RefusesARunItDoesNotHave_ARunOfAnotherSession_AndAChangedScript()
    {
        using var folder = new TemporaryFolder();
        WriteRun(folder.Path, "run-a", endedAt: DateTimeOffset.UtcNow, source: "// @exec: {\"title\": \"A\"}\nreturn await step(\"a\", () => 1);",
            steps: [Step(0, "a#0", "1")]);
        var store = new CodeModeRunStore(folder.Path);

        Assert.IsNull(CodeModeResume.TryPlan(null, "session-1", Resume("run-a"), Options("run-a"), out var noStore));
        StringAssert.Contains(noStore, "keeps no record");
        Assert.IsNull(CodeModeResume.TryPlan(store, "session-1", Resume("run-nope"), Options("run-nope"), out var unknown));
        StringAssert.Contains(unknown, "No run `run-nope` of this session");
        Assert.IsNull(CodeModeResume.TryPlan(store, "session-2", Resume("run-a"), Options("run-a"), out var other));
        StringAssert.Contains(other, "No run `run-a` of this session");
        var changed = Resume("run-a") + "\nreturn await step(\"a\", () => 2);";
        Assert.IsNull(CodeModeResume.TryPlan(store, "session-1", changed, Options("run-a"), out var refused));
        StringAssert.Contains(refused, "a changed script does not resume");
        Assert.IsNull(CodeModeResume.TryPlan(store, "session-1", Resume("run-a"), Options("run-a") with { From = "nope" }, out var noStep));
        StringAssert.Contains(noStep, "recorded no step `nope#0`. Its steps: a#0.");

        var kept = CodeModeResume.TryPlan(store, "session-1", Resume("run-a"), Options("run-a"), out _);
        var same = CodeModeResume.TryPlan(store, "session-1", Resume("run-a") + "\nreturn await step(\"a\", () => 1);", Options("run-a"), out _);
        var rerun = CodeModeResume.TryPlan(store, "session-1", changed, Options("run-a") with { Replay = "all" }, out _);

        StringAssert.StartsWith(kept?.Source, "// @exec: {\"title\": \"A\"}", "only the options line: the kept script runs");
        Assert.IsNotNull(same, "the same script resumes");
        Assert.AreEqual(changed, rerun?.Source, "a rerun over the recorded results runs the changed code");
        Assert.AreEqual(CodeModeReplayMode.All, rerun?.Replay.Mode);
    }

    [TestMethod]
    public void Plan_RefusesToRerunACallThatWasInFlight_UnlessTheOptionsSaySo()
    {
        using var folder = new TemporaryFolder();
        WriteRun(folder.Path, "run-a", endedAt: DateTimeOffset.UtcNow, source: "return 1;", steps: [Step(0, "build#0", "1")],
            calls:
            [
                """{"event":"started","call":"0:0","path":"tools.shell_command","stepKey":"build#0","args":"{\"command\":\"make\"}"}""",
                """{"event":"started","call":"0:1","path":"tools.shell_command","stepKey":"deploy#0","args":"{\"command\":\"deploy\"}"}""",
            ]);
        var store = new CodeModeRunStore(folder.Path);

        Assert.IsNull(CodeModeResume.TryPlan(store, "session-1", Resume("run-a"), Options("run-a"), out var refusal));
        StringAssert.Contains(refusal, "`tools.shell_command` in step `deploy` with {\"command\":\"deploy\"}");
        Assert.IsFalse(refusal!.Contains("make", StringComparison.Ordinal), "the call of a step that is replayed does not run again");
        StringAssert.Contains(refusal, "\"rerun_in_flight\": true");
        Assert.IsNull(CodeModeResume.TryPlan(store, "session-1", Resume("run-a"), Options("run-a") with { From = "build" }, out var fromBuild));
        StringAssert.Contains(fromBuild, "make", "a rerun from a step runs that step's call again");
        Assert.IsNotNull(CodeModeResume.TryPlan(store, "session-1", Resume("run-a"), Options("run-a") with { RerunInFlight = true }, out _));
    }

    [TestMethod]
    public void Prompt_CallsExecWithTheOptionsLineOnly_AndAsksBeforeRerunningACallInFlight()
    {
        var resume = CodeModeResume.Prompt("run-1a", null, all: false);
        var from = CodeModeResume.Prompt("run-1a", "merge#1", all: false);
        var all = CodeModeResume.Prompt("run-1a", null, all: true);

        StringAssert.Contains(resume, "// @exec: {\"resume\": \"run-1a\", \"yield_ms\": 600000}");
        StringAssert.Contains(resume, "ask the user");
        StringAssert.Contains(from, "\"from\": \"merge#1\"");
        StringAssert.Contains(from, "`merge` (2nd occurrence)");
        StringAssert.Contains(all, "\"replay\": \"all\"");
        Assert.ThrowsExactly<ArgumentException>(() => CodeModeResume.Prompt("../x", null, all: false));
    }

    private static string Resume(string runId) => $"// @exec: {{\"resume\": \"{runId}\"}}";

    private static CodeModeExecOptions Options(string runId) => new() { Resume = runId };

    private static string Step(int index, string key, string resultJson)
        => JsonSerializer.Serialize(new Dictionary<string, object> { ["index"] = index, ["key"] = key, ["succeeded"] = true, ["hasResult"] = true, ["result"] = resultJson },
            StoreTestJson.Default.DictionaryStringObject);

    // A run as a store of an earlier CodeAlta wrote it.
    private static void WriteRun(string root, string id, DateTimeOffset? endedAt, string source = "return 1;", string[]? steps = null, string[]? calls = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var info = new Dictionary<string, object>
        {
            ["id"] = id,
            ["sessionId"] = "session-1",
            ["sourceHash"] = CodeModeSource.BodyHashOf(source),
            ["startedAt"] = (endedAt ?? DateTimeOffset.UtcNow).AddMinutes(-1).ToString("O"),
            ["status"] = endedAt is null ? "running" : "completed",
        };
        if (endedAt is { } ended) info["endedAt"] = ended.ToString("O");
        File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(info, StoreTestJson.Default.DictionaryStringObject));
        File.WriteAllText(Path.Combine(folder, "source.js"), source);
        if (steps is not null) File.WriteAllLines(Path.Combine(folder, "steps.jsonl"), steps);
        if (calls is not null) File.WriteAllLines(Path.Combine(folder, "calls.jsonl"), calls);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Hang;
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the condition did not come true in time");
            await Task.Delay(20);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codemode-runs-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
internal sealed partial class StoreTestJson : System.Text.Json.Serialization.JsonSerializerContext;
