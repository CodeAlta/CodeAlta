using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The sessions scripts start, and scripts as background work of their session, on a host with a scripted provider.</summary>
[TestClass]
public sealed class CodeModeAgentsTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    private static async Task<(HeadlessTestRoot Root, ScriptedTurnProvider Provider, CodeAltaHost Host, HeadlessSessionDriver Driver, HeadlessSession Parent)> CreateAsync()
    {
        var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, fullTrust: true, headlessInstructions: true);
        var driver = new HeadlessSessionDriver(host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var parent = opened.Session!;
        provider.Script(parent.SessionId, ScriptedTurnProvider.Reply("Parent ready."));
        Assert.AreEqual(HeadlessRunStatus.Completed, (await driver.RunAsync(parent, new HeadlessPrompt("start")).WaitAsync(Hang)).Status);
        return (root, provider, host, driver, parent);
    }

    [TestMethod]
    public async Task AChildAnswersTheScript_NeverItsParent_AndIsRecordedAsCreatedByAWorkflow()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Child answer."));
        var parentRequests = provider.RequestsOf(parent.SessionId).Length;

        var result = await host.CodeModeAgents.RunAsync(parent.SessionId,
            new CodeModeAgentRequest("Review the parser.") { RunId = "run-1", StepId = "review", Title = "Review" }, CancellationToken.None).WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);

        Assert.IsTrue(result.Succeeded, result.Failure);
        Assert.AreEqual("Child answer.", result.Answer);
        var child = await host.RuntimeService.TryGetResumableSessionAsync(result.SessionId!, CancellationToken.None);
        Assert.AreEqual(parent.SessionId, child!.ParentSessionId);
        Assert.AreEqual(AltaActorProvenance.WorkflowKind, child.CreatedBy?.Kind);
        Assert.AreEqual("workflow:run-1/review", child.CreatedBy?.CorrelationId);
        Assert.AreEqual(parentRequests, provider.RequestsOf(parent.SessionId).Length, "the answer of the child is not a prompt of its parent");
    }

    [TestMethod]
    public async Task ARun_WritesItsRecordsInTheJournalOfItsSession_InOrder_BeforeItsResult()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Child answer."));

        var run = host.CodeModeRuns.TryStart(parent.SessionId, "Fan out", 1000, "call-9", async (observer, token) =>
        {
            var runId = ((CodeModeRunHandle)observer).Id;
            await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Look.") { RunId = runId, StepId = "look" }, token);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.FromMilliseconds(5));
        }, out var refusal);
        Assert.IsNotNull(run, refusal);
        await run!.Completion.WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);

        var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(parent.SessionId, CancellationToken.None);
        var history = await host.RuntimeService.TryReadStoredHistoryAsync(descriptor!, CancellationToken.None);
        var records = history!.OfType<AgentWorkflowEvent>().Where(record => record.WorkflowRunId == run.Id).ToArray();
        CollectionAssert.AreEqual(new[] { AgentWorkflowEventKind.RunStarted, AgentWorkflowEventKind.SessionStarted, AgentWorkflowEventKind.SessionEnded, AgentWorkflowEventKind.RunEnded },
            records.Select(static record => record.Kind).ToArray());
        Assert.IsTrue(records.All(static record => record.RunId is null && record.CallId == "call-9"), "a run's records belong to no turn, and name the call that runs it");
        Assert.AreEqual("Fan out", records[0].Title);
        Assert.AreEqual("look", records[1].StepId);
        Assert.AreEqual("completed", records[2].Status);
        Assert.AreEqual("completed", records[3].Status);
    }

    [TestMethod]
    public async Task AFailingChild_IsAFailedResult_AndItsParentIsStillNotTold()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Fail(AgentFailureKind.Other, "the child broke"));
        var parentRequests = provider.RequestsOf(parent.SessionId).Length;

        var result = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Do it.") { RunId = "run-2" }, CancellationToken.None).WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(parent.SessionId).WaitAsync(Hang);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure, "the child broke");
        Assert.AreEqual(parentRequests, provider.RequestsOf(parent.SessionId).Length, "an error of the child is not sent to its parent either");
    }

    [TestMethod]
    public async Task ARequestOutsideTheLimits_IsRefusedWithTheReason()
    {
        var (root, _, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;

        var tier = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("x") { RunId = "run-3", Tier = "huge" }, CancellationToken.None);
        var unknown = await host.CodeModeAgents.RunAsync(Guid.NewGuid().ToString(), new CodeModeAgentRequest("x") { RunId = "run-3" }, CancellationToken.None);

        Assert.AreEqual("refused", tier.Status);
        StringAssert.Contains(tier.Failure, "is not a tier");
        Assert.AreEqual("refused", unknown.Status);
    }

    [TestMethod]
    public async Task AScriptHoldsItsSession_UntilItsResultIsGiven_AndAnAbortOfTheTreeStopsIt()
    {
        var (root, _, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var release = new TaskCompletionSource();

        var run = host.CodeModeRuns.TryStart(parent.SessionId, "slow", 1000, async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
        }, out var refusal);
        Assert.IsNotNull(run, refusal);

        var settled = host.RuntimeService.WhenSettledAsync(parent.SessionId);
        await Task.Delay(200);
        Assert.IsFalse(settled.IsCompleted, "a session whose script runs is not settled");
        CollectionAssert.Contains(host.CodeModeRuns.ListSessionsWithRunningScripts().ToArray(), parent.SessionId);
        Assert.AreEqual(1, host.RuntimeService.CountSessionsAtWork(), "a script is work CodeAlta asks about before exiting");

        await host.RuntimeService.AbortTreeAsync(parent.SessionId, "test").WaitAsync(Hang);
        var result = await run!.Completion.WaitAsync(Hang);
        await settled.WaitAsync(Hang);

        Assert.AreEqual(CodeModeRunStatus.Cancelled, result.Status, "the abort of its tree stops the script");
    }
}
