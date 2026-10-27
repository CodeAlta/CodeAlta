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
        Assert.IsFalse(string.IsNullOrEmpty(records[1].ProviderKey), "the start of a session names its provider");
        Assert.IsFalse(string.IsNullOrEmpty(records[1].ModelId), "and its model");
        Assert.AreEqual("completed", records[2].Status);
        Assert.AreEqual("completed", records[3].Status);
    }

    [TestMethod]
    public async Task AChildAskedForAWorktree_WorksInANewWorktreeOfTheProject_OnABranchOfItsOwn()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        if (!Git(root.ProjectRoot, "init", "-q", "-b", "main")) Assert.Inconclusive("git is not installed.");
        foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("commit.gpgsign", "false") }) Git(root.ProjectRoot, "config", name, value);
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "one\n");
        Git(root.ProjectRoot, "add", "-A");
        Assert.IsTrue(Git(root.ProjectRoot, "commit", "-q", "-m", "one"));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("First."));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Second."));

        // Two children that change files at the same time, each in a worktree of its own.
        var results = await Task.WhenAll(
            host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Try one way.") { RunId = "run-w", Worktree = true }, CancellationToken.None),
            host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Try another.") { RunId = "run-w", Worktree = true }, CancellationToken.None)).WaitAsync(Hang);

        foreach (var result in results)
        {
            Assert.IsTrue(result.Succeeded, result.Failure);
            Assert.IsTrue(File.Exists(Path.Combine(result.WorktreeFolder!, "a.txt")), "the worktree is a checkout of the project");
            StringAssert.StartsWith(result.WorktreeBranch, "alta/");
            var child = await host.RuntimeService.TryGetResumableSessionAsync(result.SessionId!, CancellationToken.None);
            Assert.AreEqual(Path.GetFullPath(result.WorktreeFolder!), Path.GetFullPath(child!.WorktreeDirectory!), true, "the child works in its worktree");
            Assert.AreEqual(parent.Descriptor.ProjectRef, child.ProjectRef, "of the project of its parent");
        }

        Assert.AreNotEqual(results[0].WorktreeBranch, results[1].WorktreeBranch);
        Assert.IsTrue(Directory.Exists(results[0].WorktreeFolder), "the worktree stays when its session closes, with the work to merge");
    }

    [TestMethod]
    public async Task AChild_DoesWithoutTheToolsOfItsParent_AndOneThatWouldHaveAToolItsParentDoesWithoutIsRefused()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var prompts = Directory.CreateDirectory(Path.Combine(root.GlobalRoot, "prompts", "agents")).FullName;
        File.WriteAllText(Path.Combine(prompts, "reader.prompt.md"), "---\nname: Reader\ndisabled_tools: [shell_command]\n---\nRead only.\n");

        // A session of `altacli run --disable-tools web`: its children do without the web tools too.
        var narrow = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model", DisabledToolPatterns = ["web"] })).Session!;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Child answer."));
        var child = await host.CodeModeAgents.RunAsync(narrow.SessionId, new CodeModeAgentRequest("Look.") { RunId = "run-t" }, CancellationToken.None).WaitAsync(Hang);
        Assert.IsTrue(child.Succeeded, child.Failure);
        var disabled = await host.RuntimeService.GetDisabledToolsAsync(child.SessionId!);
        foreach (var tool in new[] { "webget", "web_search", "web_fetch" }) Assert.IsTrue(disabled.Contains(tool), tool);

        // A session whose agent prompt does without the shell gives it to no child under another prompt.
        var reader = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model", AgentPromptId = "reader" })).Session!;
        provider.Script(reader.SessionId, ScriptedTurnProvider.Reply("Reader ready."));
        Assert.AreEqual(HeadlessRunStatus.Completed, (await driver.RunAsync(reader, new HeadlessPrompt("start")).WaitAsync(Hang)).Status);
        Assert.IsTrue((await host.RuntimeService.GetDisabledToolsAsync(reader.SessionId)).Contains("shell_command"), "the reader does without the shell");
        var wider = await host.CodeModeAgents.RunAsync(reader.SessionId, new CodeModeAgentRequest("Run it.") { RunId = "run-t" }, CancellationToken.None).WaitAsync(Hang);
        Assert.AreEqual("refused", wider.Status);
        Assert.IsNull(wider.SessionId, "no session is created");
        StringAssert.Contains(wider.Failure, "`shell_command`");
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Read."));
        var same = await host.CodeModeAgents.RunAsync(reader.SessionId, new CodeModeAgentRequest("Read it.") { RunId = "run-t", AgentPromptId = "reader" }, CancellationToken.None).WaitAsync(Hang);
        Assert.IsTrue(same.Succeeded, same.Failure);
    }

    [TestMethod]
    public async Task AWorktree_IsRefusedToTheScriptOfASessionWithoutAGitProject()
    {
        var (root, _, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;

        var result = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Change it.") { RunId = "run-nw", Worktree = true }, CancellationToken.None).WaitAsync(Hang);
        var badBase = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("Change it.") { RunId = "run-nw", Worktree = true, WorktreeBase = "--force" }, CancellationToken.None).WaitAsync(Hang);

        Assert.AreEqual("refused", result.Status);
        StringAssert.Contains(result.Failure, "worktree");
        Assert.IsNull(result.SessionId, "no session is created without its worktree");
        Assert.AreEqual("refused", badBase.Status);
        StringAssert.Contains(badBase.Failure, "`base`");
    }

    private static bool Git(string folder, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(folder, "..", "no-global-config");
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
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
    public async Task AnAnswerWithASchema_IsTheCheckedValue_AfterOneCorrection_AndASecondMissFails()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        using var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"]}""");
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("There are three."));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("""{"count": 3}"""));

        var corrected = await host.CodeModeAgents.RunAsync(parent.SessionId,
            new CodeModeAgentRequest("Count them.") { RunId = "run-s", Schema = schema.RootElement.Clone() }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsTrue(corrected.Succeeded, corrected.Failure);
        Assert.AreEqual("""{"count": 3}""", corrected.Value);
        var requests = provider.RequestsOf(corrected.SessionId!);
        Assert.AreEqual(2, requests.Length, "one corrective prompt");
        StringAssert.Contains(System.Text.Json.JsonSerializer.Serialize(requests[0].Conversation), "only one JSON value");

        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Three."));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Still three."));
        var missed = await host.CodeModeAgents.RunAsync(parent.SessionId,
            new CodeModeAgentRequest("Count them.") { RunId = "run-s", Schema = schema.RootElement.Clone() }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsFalse(missed.Succeeded);
        StringAssert.Contains(missed.Failure, "does not match the schema");
    }

    [TestMethod]
    public async Task AKeptSession_TakesSeveralPrompts_AndNoneAfterItIsClosed()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("First."));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Second."));
        var agents = (CodeModeAgents)host.CodeModeAgents;

        var opened = await agents.OpenAsync(parent.SessionId, new CodeModeAgentRequest(string.Empty) { RunId = "run-k", Title = "Kept" }, CancellationToken.None).WaitAsync(Hang);
        var first = await agents.AskAsync("run-k", opened.SessionId!, "one", null, null, CancellationToken.None).WaitAsync(Hang);
        var second = await agents.AskAsync("run-k", opened.SessionId!, "two", null, null, CancellationToken.None).WaitAsync(Hang);
        await agents.CloseAsync("run-k", opened.SessionId!);
        var after = await agents.AskAsync("run-k", opened.SessionId!, "three", null, null, CancellationToken.None);

        Assert.AreEqual("opened", opened.Status, opened.Failure);
        Assert.AreEqual(("First.", "Second."), (first.Answer, second.Answer));
        Assert.AreEqual(2, provider.RequestsOf(opened.SessionId!).Length, "the same session took both prompts");
        Assert.AreEqual("refused", after.Status);
    }

    [TestMethod]
    public async Task ASpentBudget_StartsNoMoreSessions()
    {
        var (root, provider, host, driver, parent) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Done once."));

        // A scripted turn reports 100 input and 20 output tokens.
        var first = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("one") { RunId = "run-b", BudgetTokens = 100 }, CancellationToken.None).WaitAsync(Hang);
        var second = await host.CodeModeAgents.RunAsync(parent.SessionId, new CodeModeAgentRequest("two") { RunId = "run-b", BudgetTokens = 100 }, CancellationToken.None).WaitAsync(Hang);

        Assert.IsTrue(first.Succeeded, first.Failure);
        Assert.AreEqual("refused", second.Status);
        StringAssert.Contains(second.Failure, "budget of this script is spent");
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
