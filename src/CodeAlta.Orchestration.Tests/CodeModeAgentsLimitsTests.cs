using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The sessions scripts start, at their limits: what they inherit, how many, how deep and how many at once, what a stop of
/// the tree does to them, three at once, and a resume over a step that started one.
/// </summary>
[TestClass]
public sealed class CodeModeAgentsLimitsTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    private static async Task<(HeadlessTestRoot Root, ScriptedTurnProvider Provider, CodeAltaHost Host, HeadlessSessionDriver Driver, HeadlessSession Parent)> CreateAsync(
        bool permissionModes = false)
    {
        var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, headlessInstructions: true,
            sessionPermissionModes: permissionModes, inheritPermissionMode: permissionModes);
        var driver = new HeadlessSessionDriver(host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var parent = opened.Session!;
        provider.Script(parent.SessionId, ScriptedTurnProvider.Reply("Parent ready."));
        Assert.AreEqual(HeadlessRunStatus.Completed, (await driver.RunAsync(parent, new HeadlessPrompt("start")).WaitAsync(Hang)).Status);
        return (root, provider, host, driver, parent);
    }

    // A turn of a child that tells it started, then waits for the test (or the end of its run).
    private static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> Held(Task release, Action started, string answer = "done")
        => async (_, token) =>
        {
            started();
            await release.WaitAsync(token).ConfigureAwait(false);
            return ScriptedTurnProvider.Answer(answer);
        };

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Hang;
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, what);
            await Task.Delay(25);
        }
    }

    [TestMethod]
    public async Task TheSessionsAScriptStarts_HaveAtMostTheToolsOfTheirCreator_AndAtLeastItsRestrictions()
    {
        var (root, provider, host, driver, parent) = await CreateAsync(permissionModes: true);
        await using var scope = root;
        await using var closing = driver;
        Assert.AreEqual("ok", (await host.Commands.SetPermissionModeAsync(parent.SessionId, SessionPermissionModes.Ask)).Status);
        host.RuntimeService.SetDisabledToolPatterns(parent.SessionId, ["shell_command"]);
        Assert.AreEqual("ok", (await host.Commands.SetSandboxAsync(parent.SessionId, "strict")).Status);
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Child answer."));

        var agent = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-narrow" }, CancellationToken.None).WaitAsync(Hang);
        var kept = await host.CodeModeAgents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-narrow" }, CancellationToken.None).WaitAsync(Hang);
        var unsandboxed = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-narrow", Sandbox = "off" }, CancellationToken.None).WaitAsync(Hang);
        var another = await host.CodeModeAgents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-narrow", Sandbox = "workspace" }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsTrue(agent.Succeeded, agent.Failure);
        Assert.AreEqual("opened", kept.Status, kept.Failure);
        foreach (var child in new[] { agent.SessionId!, kept.SessionId! })
        {
            CollectionAssert.Contains(host.RuntimeService.GetDisabledToolPatterns(child).ToArray(), "shell_command", "a child does without what its creator does without");
            Assert.AreEqual(SessionPermissionPolicy.Review, host.RuntimeService.GetPermissionPolicy(child), "a child asks as its creator asks");
            var sandbox = await host.RuntimeService.GetSessionSandboxAsync(child);
            Assert.IsTrue(sandbox.IsSandboxed, "a child of a sandboxed session is sandboxed");
            Assert.AreEqual("strict", sandbox.Choice.Profile);
        }

        Assert.AreEqual("refused", unsandboxed.Status, "a script does not take a session out of its sandbox");
        StringAssert.Contains(unsandboxed.Failure, "sandbox");
        Assert.AreEqual("refused", another.Status, "nor gives it another sandbox");
        await ((CodeModeAgents)host.CodeModeAgents).ForgetRunAsync("run-narrow");
    }

    [TestMethod]
    public async Task ARun_StartsAtMost50Sessions_3LevelsDeep_8AtWorkAtOnce()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var agents = (CodeModeAgents)host.CodeModeAgents;

        // 50 sessions, then the 51st is refused with its reason.
        for (var index = 0; index < CodeModeAgents.MaxPerRun; index++)
        {
            var opened = await agents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-many" }, CancellationToken.None).WaitAsync(Hang);
            Assert.AreEqual("opened", opened.Status, $"session {index + 1}: {opened.Failure}");
        }

        var fiftyFirst = await agents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-many" }, CancellationToken.None).WaitAsync(Hang);
        Assert.AreEqual("refused", fiftyFirst.Status);
        StringAssert.Contains(fiftyFirst.Failure, "already started 50 sessions");
        await agents.ForgetRunAsync("run-many");

        // A script of a session three levels below the first is refused a fourth level.
        var level = parent.SessionId;
        for (var depth = 1; depth <= CodeModeAgents.MaxDepth; depth++)
        {
            var opened = await agents.OpenAsync(level, new CodeModeAgentRequest(string.Empty) { RunId = $"run-depth-{depth}" }, CancellationToken.None).WaitAsync(Hang);
            Assert.AreEqual("opened", opened.Status, $"level {depth}: {opened.Failure}");
            level = opened.SessionId!;
        }

        var fourth = await agents.OpenAsync(level, new CodeModeAgentRequest(string.Empty) { RunId = "run-depth-4" }, CancellationToken.None).WaitAsync(Hang);
        Assert.AreEqual("refused", fourth.Status);
        StringAssert.Contains(fourth.Failure, "3 levels deep");
        for (var depth = 1; depth <= CodeModeAgents.MaxDepth; depth++) await agents.ForgetRunAsync($"run-depth-{depth}");

        // Nine sessions asked at once: eight work, the ninth waits for one of them.
        var release = new TaskCompletionSource();
        var started = 0;
        for (var index = 0; index < 9; index++) provider.Turns.Enqueue(Held(release.Task, () => Interlocked.Increment(ref started)));
        var nine = Enumerable.Range(0, 9).Select(index => agents.RunAsync(parent.SessionId, new CodeModeAgentRequest($"Part {index}.") { RunId = "run-wide" }, CancellationToken.None)).ToArray();
        await WaitForAsync(() => Volatile.Read(ref started) == CodeModeAgents.MaxAtWork, "eight sessions did not start");
        await Task.Delay(1500);
        Assert.AreEqual(CodeModeAgents.MaxAtWork, Volatile.Read(ref started), "the ninth waits while eight work");
        release.SetResult();
        var results = await Task.WhenAll(nine).WaitAsync(Hang);

        Assert.IsTrue(results.All(static result => result.Succeeded), string.Join("; ", results.Select(static result => result.Failure)));
        Assert.AreEqual(9, started);
    }

    [TestMethod]
    public async Task AStopOfTheTree_EndsTheRunAndItsChildCancelled_AndTellsTheParentNothing()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var parentRequests = provider.RequestsOf(parent.SessionId).Length;

        async Task<(CodeModeRunResult Result, CodeModeAgentResult? Child, CodeModeRunHandle Run)> RunHeldAsync(Func<CodeModeRunHandle, Task> stop)
        {
            var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Turns.Enqueue(Held(new TaskCompletionSource().Task, () => childStarted.TrySetResult()));
            CodeModeAgentResult? child = null;
            var run = host.CodeModeRuns.TryStart(parent.SessionId, "Held", 1000, "call-held", async (observer, token) =>
            {
                child = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Wait.") { RunId = ((CodeModeRunHandle)observer).Id }, token);
                token.ThrowIfCancellationRequested();
                return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
            }, out var refusal)!;
            Assert.IsNotNull(run, refusal);
            // It went on in the background: an end that is not stopped would come back to the parent as a prompt.
            run.Background();
            await childStarted.Task.WaitAsync(Hang);
            await stop(run);
            var result = await run.Completion.WaitAsync(Hang);
            return (result, child, run);
        }

        var aborted = await RunHeldAsync(_ => host.RuntimeService.AbortTreeAsync(parent.SessionId, "test"));
        var terminated = await RunHeldAsync(async run =>
        {
            // `wait` with `terminate: true`: the wait takes the result.
            run.Stop();
            var (ended, result) = await host.CodeModeRuns.WaitAsync(run, Hang, CancellationToken.None);
            Assert.IsTrue(ended);
            Assert.AreEqual(CodeModeRunStatus.Cancelled, result?.Status);
        });
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);
        await Task.Delay(1000);

        Assert.IsTrue(aborted.Run.TryCollect(), "no prompt took the result of the aborted run: it was never delivered");
        Assert.IsFalse(terminated.Run.TryCollect(), "the wait took the result of the terminated run");
        foreach (var (result, child, _) in new[] { aborted, terminated })
        {
            Assert.AreEqual(CodeModeRunStatus.Cancelled, result.Status);
            Assert.IsNotNull(child, "the child's run ended");
            Assert.AreEqual("aborted", child.Status, "the child's turn was stopped with the run");
        }

        Assert.AreEqual(parentRequests, provider.RequestsOf(parent.SessionId).Length, "the parent is told nothing");
    }

    [TestMethod]
    public async Task ThreeChildrenAtOnce_AllAnswerTheRun_AndTheirParentGetsNoRequest()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var parentRequests = provider.RequestsOf(parent.SessionId).Length;
        var release = new TaskCompletionSource();
        var started = 0;
        for (var index = 0; index < 3; index++) provider.Turns.Enqueue(Held(release.Task, () => Interlocked.Increment(ref started), $"answer"));

        var run = host.CodeModeRuns.TryStart(parent.SessionId, "Three", 1000, "call-three", async (observer, token) =>
        {
            var runId = ((CodeModeRunHandle)observer).Id;
            var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(index =>
                host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest($"Part {index}.") { RunId = runId, StepId = "fan" }, token)));
            return new CodeModeRunResult(answers.All(static answer => answer.Succeeded) ? CodeModeRunStatus.Completed : CodeModeRunStatus.Failed, TimeSpan.Zero)
            {
                ValueJson = JsonSerializer.Serialize(answers.Select(static answer => answer.Answer).ToArray(), LimitsTestJson.Default.StringArray),
            };
        }, out var refusal)!;
        Assert.IsNotNull(run, refusal);
        await WaitForAsync(() => Volatile.Read(ref started) == 3, "the three children did not work at once");
        release.SetResult();
        var result = await run.Completion.WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status);
        Assert.AreEqual("""["answer","answer","answer"]""", result.ValueJson);
        Assert.AreEqual(parentRequests, provider.RequestsOf(parent.SessionId).Length, "the answers went to the run, not to the parent");
        var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(parent.SessionId, CancellationToken.None);
        var records = (await host.RuntimeService.TryReadStoredHistoryAsync(descriptor!, CancellationToken.None))!
            .OfType<AgentWorkflowEvent>().Where(record => record.WorkflowRunId == run.Id).ToArray();
        var kinds = records.Select(static record => record.Kind).ToList();
        Assert.AreEqual(3, kinds.Count(static kind => kind == AgentWorkflowEventKind.SessionStarted));
        Assert.IsLessThan(kinds.IndexOf(AgentWorkflowEventKind.SessionEnded), kinds.LastIndexOf(AgentWorkflowEventKind.SessionStarted),
            "the three sessions started before any ended: they overlapped");
    }

    [TestMethod]
    public async Task AResumeOverAStepThatStartedASession_ReusesItsAnswer_AndStartsNoSession()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Looks good."));
        const string Code = """
            const review = await step("review", () => agent("Review the change."));
            return "review: " + review;
            """;
        var api = new CodeModeApi([new CodeModeFunction("codemode.agent")]);

        var first = await new CodeModeHost(api, new CodeModeScriptCache()).RunAsync(CodeModeSource.Prepare(Code), new Agents(host.CodeModeAgents, parent.SessionId, "run-first"));
        var requests = provider.Requests.Count;
        var recorded = first.Steps.Select(static step => new CodeModeReplayStep(step.Index, step.Key!, step.Succeeded) { HasResult = step.HasResult, ResultJson = step.ResultJson });
        var resumed = await new CodeModeHost(api, new CodeModeScriptCache()).RunAsync(CodeModeSource.Prepare(Code), new Agents(host.CodeModeAgents, parent.SessionId, "run-resumed"),
            new CodeModeRunOptions { Replay = new CodeModeReplay(recorded, CodeModeReplayMode.Resume) });

        Assert.AreEqual(CodeModeRunStatus.Completed, first.Status, first.Error?.Message);
        Assert.AreEqual("\"review: Looks good.\"", first.ValueJson);
        Assert.AreEqual(first.ValueJson, resumed.ValueJson, "the resume gives the recorded answer");
        Assert.AreEqual(requests, provider.Requests.Count, "no session was started, nor asked anything");
        Assert.IsTrue(resumed.Steps.Single().Replayed);
    }

    // codemode.agent of the tests: the sessions scripts start, as the binding of the tools gives them.
    private sealed class Agents(ICodeModeAgents agents, string parentSessionId, string runId) : ICodeModeCallHandler
    {
        public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            var result = await agents.RunAsync(parentSessionId, new CodeModeAgentRequest(arguments.RootElement.GetProperty("prompt").GetString()!) { RunId = runId, StepId = call.StepId },
                cancellationToken);
            if (!result.Succeeded) return CodeModeCallResult.Failure(new CodeModeCallError("AgentError", result.Failure ?? result.Status));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("answer", result.Answer);
                writer.WriteEndObject();
            }

            return CodeModeCallResult.Success(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class LimitsTestJson : System.Text.Json.Serialization.JsonSerializerContext;
