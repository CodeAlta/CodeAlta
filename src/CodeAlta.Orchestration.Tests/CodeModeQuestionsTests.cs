using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The questions of scripts (<c>ask()</c>), asked where the session asks its questions, on a host with a scripted provider.</summary>
[TestClass]
public sealed class CodeModeQuestionsTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    private static async Task<(HeadlessTestRoot Root, ScriptedTurnProvider Provider, CodeAltaHost Host, HeadlessSessionDriver Driver, HeadlessSession Session)> CreateAsync(
        bool enableOwnedAsks = true, AltaActorProvenance? createdBy = null)
    {
        var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, headlessInstructions: true, enableOwnedAsks: enableOwnedAsks);
        var driver = new HeadlessSessionDriver(host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model", CreatedBy = createdBy });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var session = opened.Session!;
        provider.Script(session.SessionId, ScriptedTurnProvider.Reply("Ready."));
        Assert.AreEqual(HeadlessRunStatus.Completed, (await driver.RunAsync(session, new HeadlessPrompt("start")).WaitAsync(Hang)).Status);
        return (root, provider, host, driver, session);
    }

    // A run of the session that waits until the test lets it end.
    private static CodeModeRunHandle StartRun(CodeAltaHost host, string sessionId, TaskCompletionSource release)
    {
        var run = host.CodeModeRuns.TryStart(sessionId, "Deploy", 1000, "call-5", async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero);
        }, out var refusal);
        Assert.IsNotNull(run, refusal);
        return run;
    }

    private static async Task<OwnedAskHead> HeadAsync(CodeAltaHost host, string sessionId, string question)
    {
        var deadline = DateTime.UtcNow + Hang;
        while (true)
        {
            if (host.Commands.Asks.List(sessionId).Head is { } head && head.Request.Questions[0].Question == question) return head;
            Assert.IsLessThan(deadline, DateTime.UtcNow, $"the question `{question}` was not asked");
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task AQuestion_WaitsWhereTheSessionAsks_OneAtATime_AndItsAnswerGoesToTheScript()
    {
        var (root, provider, host, driver, session) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var release = new TaskCompletionSource();
        var run = StartRun(host, session.SessionId, release);
        var requests = provider.RequestsOf(session.SessionId).Length;

        var first = host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "Deploy to production?") { Choices = ["yes", "no"], StepId = "deploy" }, CancellationToken.None);
        var second = host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "Which tag?") { Default = "latest" }, CancellationToken.None);
        var head = await HeadAsync(host, session.SessionId, "Deploy to production?");
        CollectionAssert.Contains(host.Commands.Asks.ListWaitingSessions().ToArray(), session.SessionId, "the session waits for the user");
        Assert.IsFalse(host.Commands.Asks.List(session.SessionId).HasMore, "the second question waits its turn");
        CollectionAssert.AreEqual(new[] { "yes", "no" }, head.Request.Questions[0].Choices.Select(static choice => choice.Title).ToArray());
        Assert.AreEqual(run.Id, head.Handle.RunId);
        Assert.IsTrue(head.Immediate, "the question of a script is shown at once, while the turn that waits for it runs");
        Assert.IsTrue(run.WaitingForUser, "exec does not count this time against its yield");
        var stopped = run.TimeNotWaitingForUser;
        await Task.Delay(100);
        Assert.AreEqual(stopped, run.TimeNotWaitingForUser);

        var disposition = await host.Commands.Asks.AnswerAsync(new OwnedAskAction(Guid.NewGuid(), head.Handle, [new AltaAskAnswer { QuestionIndex = 0, SelectedChoiceIndexes = [0] }]), CancellationToken.None);
        var answered = await first.WaitAsync(Hang);
        var next = await HeadAsync(host, session.SessionId, "Which tag?");
        await host.Commands.Asks.AnswerAsync(new OwnedAskAction(Guid.NewGuid(), next.Handle, [new AltaAskAnswer { QuestionIndex = 0, FreeformText = "" }]), CancellationToken.None);
        var defaulted = await second.WaitAsync(Hang);
        release.SetResult();
        await run.Completion.WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);

        Assert.AreEqual("admitted", disposition.Status);
        Assert.IsFalse(run.WaitingForUser);
        Assert.AreEqual((CodeModeQuestionOutcome.Answered, "yes"), (answered.Outcome, answered.Answer));
        Assert.AreEqual((CodeModeQuestionOutcome.Answered, "latest"), (defaulted.Outcome, defaulted.Answer), "an empty answer is the default");
        Assert.AreEqual(requests, provider.RequestsOf(session.SessionId).Length, "the answers went to the script, not to the session");
        var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId, CancellationToken.None);
        var history = await host.RuntimeService.TryReadStoredHistoryAsync(descriptor!, CancellationToken.None);
        var waits = history!.OfType<AgentWorkflowEvent>().Where(record => record.WorkflowRunId == run.Id
            && record.Kind is AgentWorkflowEventKind.WaitStarted or AgentWorkflowEventKind.WaitEnded).ToArray();
        CollectionAssert.AreEqual(new[] { AgentWorkflowEventKind.WaitStarted, AgentWorkflowEventKind.WaitEnded, AgentWorkflowEventKind.WaitStarted, AgentWorkflowEventKind.WaitEnded },
            waits.Select(static record => record.Kind).ToArray(), "one question at a time, each shown on the card");
        Assert.AreEqual(("question", "deploy", "Deploy to production?"), (waits[0].Status, waits[0].StepId, waits[0].Message));
    }

    [TestMethod]
    public async Task ADismissedQuestion_IsCancelled_AndAStoppedRunWithdrawsItsQuestion()
    {
        var (root, _, host, driver, session) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var release = new TaskCompletionSource();
        var run = StartRun(host, session.SessionId, release);

        var dismissed = host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "Go?"), CancellationToken.None);
        var head = await HeadAsync(host, session.SessionId, "Go?");
        await host.Commands.Asks.CancelAsync(new OwnedAskAction(Guid.NewGuid(), head.Handle, []), CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var withdrawn = host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "Still there?"), stop.Token);
        await HeadAsync(host, session.SessionId, "Still there?");
        await stop.CancelAsync();

        Assert.AreEqual(CodeModeQuestionOutcome.Cancelled, (await dismissed.WaitAsync(Hang)).Outcome);
        await Assert.ThrowsAsync<OperationCanceledException>(() => withdrawn.WaitAsync(Hang));
        Assert.IsNull(host.Commands.Asks.List(session.SessionId).Head, "the question of a stopped run leaves the panel");
        release.SetResult();
        await run.Completion.WaitAsync(Hang);
    }

    [TestMethod]
    public async Task NobodyIsAsked_InAnAutomation_NorInAHostThatAsksNothing()
    {
        foreach (var (asks, createdBy, expected) in new (bool, AltaActorProvenance?, string)[]
                 {
                     (true, new AltaActorProvenance { Kind = AltaActorProvenance.AutomationKind }, "An automation runs with nobody to answer"),
                     (false, null, "Nobody can be asked here"),
                 })
        {
            var (root, _, host, driver, session) = await CreateAsync(asks, createdBy);
            await using var scope = root;
            await using var closing = driver;
            var release = new TaskCompletionSource();
            var run = StartRun(host, session.SessionId, release);

            var answer = await host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "Go?"), CancellationToken.None).WaitAsync(Hang);

            Assert.AreEqual(CodeModeQuestionOutcome.Unavailable, answer.Outcome);
            StringAssert.StartsWith(answer.Message, expected);
            Assert.IsNull(host.Commands.Asks.List(session.SessionId).Head);
            release.SetResult();
            await run.Completion.WaitAsync(Hang);
        }
    }

    [TestMethod]
    public async Task TheApprovalOfABackgroundCall_TakesItsTurnAmongTheQuestionsOfItsRun()
    {
        var (root, _, host, driver, session) = await CreateAsync();
        await using var scope = root;
        await using var closing = driver;
        var release = new TaskCompletionSource();
        var run = StartRun(host, session.SessionId, release);

        Assert.AreSame(run, host.CodeModeRuns.FindByToolCall(session.SessionId, "call-5/3"));
        Assert.IsNull(host.CodeModeRuns.FindByToolCall(session.SessionId, "call-5"), "a call of the model is no call of a script");
        Assert.IsNull(host.CodeModeRuns.FindByToolCall("other", "call-5/3"));
        var question = host.CodeModeQuestions.AskAsync(new(session.SessionId, run.Id, "First?"), CancellationToken.None);
        var head = await HeadAsync(host, session.SessionId, "First?");
        var request = new AgentCommandPermissionRequest(new("scripted"), session.SessionId, DateTimeOffset.UtcNow, null, "call-5/3", null, "make", root.ProjectRoot, null, "fixture", null, null, null);
        var approval = host.RuntimeService.Permissions.WaitingForUser!(request, CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.IsFalse(approval.IsCompleted, "the approval waits for the question before it");
        await host.Commands.Asks.AnswerAsync(new OwnedAskAction(Guid.NewGuid(), head.Handle, [new AltaAskAnswer { QuestionIndex = 0, FreeformText = "ok" }]), CancellationToken.None);
        await question.WaitAsync(Hang);
        using (var turn = await approval.WaitAsync(Hang))
        {
            Assert.IsNotNull(turn);
        }

        release.SetResult();
        await run.Completion.WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId, CancellationToken.None);
        var history = await host.RuntimeService.TryReadStoredHistoryAsync(descriptor!, CancellationToken.None);
        Assert.AreEqual("approval", history!.OfType<AgentWorkflowEvent>().Last(static record => record.Kind == AgentWorkflowEventKind.WaitStarted).Status);
    }
}
