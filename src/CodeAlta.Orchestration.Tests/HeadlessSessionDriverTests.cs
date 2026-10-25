using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The headless driver end to end, on a host without a user and a scripted provider.</summary>
[TestClass]
public sealed class HeadlessSessionDriverTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    private static async Task<(HeadlessTestRoot Root, ScriptedTurnProvider Provider, CodeAltaHost Host)> CreateAsync(
        ModelProviderAvailability availability = ModelProviderAvailability.Ready)
    {
        var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider(availability: availability);
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, fullTrust: true, headlessInstructions: true);
        return (root, provider, host);
    }

    [TestMethod]
    public async Task Run_Completes_ReportsTheAnswerUsageAndCost_AndTakesSeveralPrompts()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var events = new ConcurrentQueue<HeadlessSessionEvent>();
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            Observer = (item, _) => { events.Enqueue(item); return ValueTask.CompletedTask; },
        });

        var opened = await driver.OpenAsync(new HeadlessSessionRequest
        {
            ProjectFolder = root.ProjectRoot,
            ModelRef = "scripted:scripted-model@high",
            DisabledToolPatterns = [AgentToolPatterns.WebAlias],
        });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var session = opened.Session!;
        Assert.AreEqual("scripted:scripted-model@high", session.ModelRef);
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("First answer."));

        var first = await driver.RunAsync(session, new HeadlessPrompt("hello")).WaitAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Completed, first.Status, first.FailureMessage);
        Assert.AreEqual("First answer.", first.FinalMessage);
        Assert.AreEqual(100, first.Usage.InputTokens);
        Assert.AreEqual(20, first.Usage.OutputTokens);
        Assert.AreEqual(120, first.Usage.TotalTokens);
        Assert.AreEqual(0.25, first.Usage.Cost);
        Assert.AreEqual("USD", first.Usage.CostUnit);
        Assert.IsTrue(first.Duration > TimeSpan.Zero);
        Assert.IsNotNull(first.RunId);
        var request = provider.RequestsOf(session.SessionId).Single();
        Assert.AreEqual(AgentReasoningEffort.High, request.ReasoningEffort);
        CollectionAssert.DoesNotContain(request.Tools.Select(static tool => tool.Spec.Name).ToArray(), "webget");
        StringAssert.EndsWith(request.SystemMessage, SessionRuntimeService.HeadlessGuidance);
        Assert.AreEqual(SessionPermissionPolicy.Approve, host.RuntimeService.GetPermissionPolicy(session.SessionId));

        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Second answer."));
        var second = await driver.RunAsync(session, new HeadlessPrompt("again")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.Completed, second.Status);
        Assert.AreEqual("Second answer.", second.FinalMessage);
        Assert.AreEqual(100, second.Usage.InputTokens, "The usage of a run is its own, not the session's.");
        Assert.IsTrue(events.Any(item => item.RootSessionId == session.SessionId
            && item.Event is SessionAgentEvent { Event: AgentContentCompletedEvent { Content: "Second answer." } }), "The observer sees the events of the session.");
    }

    [TestMethod]
    public async Task Run_WaitsForTheChildItStarted_AndObservesItsEventsUnderTheRoot()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var events = new ConcurrentQueue<HeadlessSessionEvent>();
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            Observer = (item, _) => { events.Enqueue(item); return ValueTask.CompletedTask; },
        });
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        var childId = Guid.CreateVersion7().ToString();
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        provider.Script(session.SessionId,
            ScriptedTurnProvider.DoThenReply(async () =>
            {
                var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child", session.SessionId, null, null, null, childId);
                _ = host.RuntimeService.SendAsync(child, options, new AgentSendOptions { Input = AgentInput.Text("child work") });
            }, "Started."),
            ScriptedTurnProvider.Reply("The child said it."));
        provider.Script(childId, ScriptedTurnProvider.Reply("Child result."));

        var result = await driver.RunAsync(session, new HeadlessPrompt("delegate")).WaitAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Completed, result.Status, result.FailureMessage);
        Assert.AreEqual("The child said it.", result.FinalMessage, "The run ends after the parent read its child.");
        Assert.AreEqual(300, result.Usage.InputTokens, "Two runs of the root and one of the child.");
        Assert.IsTrue(events.Any(item => item.RootSessionId == session.SessionId && item.Event.SessionId == childId));
    }

    [TestMethod]
    public async Task FlushEvents_WaitsUntilTheObserverWasGivenEveryEventPublishedBefore()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var events = new ConcurrentQueue<HeadlessSessionEvent>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            // A slow observer: it takes nothing until the gate opens.
            Observer = async (item, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                events.Enqueue(item);
            },
        });
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("The answer."));
        var result = await driver.RunAsync(session, new HeadlessPrompt("hello")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.Completed, result.Status, result.FailureMessage);

        var flush = driver.FlushEventsAsync();
        Assert.IsFalse(flush.IsCompleted, "The observer has not taken the events of the run yet.");
        gate.SetResult();
        await flush.WaitAsync(Hang);

        Assert.IsTrue(events.Any(static item => item.Event is SessionAgentEvent { Event: AgentContentCompletedEvent { Kind: AgentContentKind.Assistant, Content: "The answer." } }),
            "Every event published before the flush reached the observer.");
        Assert.IsTrue(events.All(static item => !string.IsNullOrEmpty(item.Event.SessionId)), "The marker of the flush is not given to the observer.");
        await using var unobserved = new HeadlessSessionDriver(host);
        Assert.IsTrue(unobserved.FlushEventsAsync().IsCompleted, "A driver without an observer has nothing to flush.");
    }

    [TestMethod]
    public async Task Run_ThatFails_IsFailed_AndASignInFailureIsProviderNotReady()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        await using var driver = new HeadlessSessionDriver(host);
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;

        provider.Turns.Enqueue(ScriptedTurnProvider.Fail(AgentFailureKind.Other, "The tool broke."));
        var failed = await driver.RunAsync(session, new HeadlessPrompt("work")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.Failed, failed.Status);
        StringAssert.Contains(failed.FailureMessage, "The tool broke.");
        Assert.AreEqual(AgentFailureKind.Other, failed.FailureKind);

        provider.Turns.Enqueue(ScriptedTurnProvider.Fail(AgentFailureKind.Auth, "Sign in again."));
        var signIn = await driver.RunAsync(session, new HeadlessPrompt("work again")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.ProviderNotReady, signIn.Status);
    }

    [TestMethod]
    public async Task Open_WithAProviderThatIsNotReady_OrNotConfigured_IsProviderNotReady()
    {
        var (root, _, host) = await CreateAsync(ModelProviderAvailability.Failed);
        await using var scope = root;
        await using var driver = new HeadlessSessionDriver(host);

        var failed = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" });
        var missing = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "missing:model" });
        var tier = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, Tier = CodeAlta.Catalog.ModelTiers.ModelTier.Complex });

        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, failed.Status);
        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, missing.Status);
        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, tier.Status, "A tier without a model has none ready.");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.GlobalRoot, "sessions")), "No session is created for a provider that cannot run it.");
    }

    [TestMethod]
    public async Task Run_ThatRunsOutOfTime_IsAbortedWithTheReasonTimeout()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        await using var driver = new HeadlessSessionDriver(host);
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        provider.Turns.Enqueue(ScriptedTurnProvider.WaitThenReply(new TaskCompletionSource().Task, "never"));

        var result = await driver.RunAsync(session, new HeadlessPrompt("work"), TimeSpan.FromMilliseconds(300)).WaitAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Timeout, result.Status);
        Assert.AreEqual("timeout", result.AbortReason);
        var outcome = await host.RuntimeService.ReadOutcomeAsync(session.Descriptor, result.RunId);
        Assert.AreEqual(SessionOutcomeStatus.Cancelled, outcome.Status);
        Assert.AreEqual("timeout", outcome.AbortReason, "The journal records the abort and its reason.");
    }

    [TestMethod]
    public async Task Run_ThatRunsOutOfTimeBeforeItsPromptStarts_StartsNoRun_AndIsAbortedWithTheReasonTimeout()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            // A run left going after the abort would make the driver wait this long.
            AbortGrace = TimeSpan.FromSeconds(5),
            Observer = (item, _) => { if (IsAbortRecord(item.Event)) aborted.TrySetResult(); return ValueTask.CompletedTask; },
        });
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        provider.Turns.Enqueue(ScriptedTurnProvider.WaitThenReply(new TaskCompletionSource().Task, "never"));
        // The prompt is admitted, then held before its run starts until the timeout has aborted it.
        host.Commands.SendStarting = (_, token) => HoldUntilAbortedAsync(aborted.Task, token);

        var result = await driver.RunAsync(session, new HeadlessPrompt("work"), TimeSpan.FromMilliseconds(100)).WaitAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Timeout, result.Status, result.FailureMessage);
        Assert.AreEqual("timeout", result.AbortReason);
        Assert.AreEqual(0, provider.RequestsOf(session.SessionId).Length, "A prompt admitted before the abort does not start after it.");
        await host.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        var outcome = await host.RuntimeService.ReadOutcomeAsync(session.Descriptor, result.RunId);
        Assert.AreEqual(SessionOutcomeStatus.Cancelled, outcome.Status);
        Assert.AreEqual("timeout", outcome.AbortReason, "The journal records the abort and its reason.");
    }

    [TestMethod]
    public async Task Cancel_BeforeThePromptStarts_StartsNoRun_AndALaterPromptRuns()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            AbortGrace = TimeSpan.FromSeconds(5),
            Observer = (item, _) => { if (IsAbortRecord(item.Event)) aborted.TrySetResult(); return ValueTask.CompletedTask; },
        });
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Commands.SendStarting = (_, token) =>
        {
            held.TrySetResult();
            return HoldUntilAbortedAsync(aborted.Task, token);
        };
        using var signalled = new CancellationTokenSource();

        // As altacli does on a signal: the tree is aborted, then the token stops the wait of the run.
        var run = driver.RunAsync(session, new HeadlessPrompt("work"), null, signalled.Token);
        await held.Task.WaitAsync(Hang);
        await driver.CancelAsync(session, "signal").WaitAsync(Hang);
        await signalled.CancelAsync();
        var result = await run.WaitAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Aborted, result.Status, result.FailureMessage);
        Assert.IsNotNull(result.AbortReason);
        Assert.AreEqual(0, provider.RequestsOf(session.SessionId).Length, "A prompt admitted before the abort does not start after it.");
        var outcome = await host.RuntimeService.ReadOutcomeAsync(session.Descriptor, result.RunId);
        Assert.AreEqual(SessionOutcomeStatus.Cancelled, outcome.Status);
        Assert.IsNotNull(outcome.AbortReason, "The journal records the abort and its reason.");

        host.Commands.SendStarting = null;
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Later answer."));
        var later = await driver.RunAsync(session, new HeadlessPrompt("again")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.Completed, later.Status, later.FailureMessage);
        Assert.AreEqual("Later answer.", later.FinalMessage, "The abort does not hold a prompt sent after it.");
        Assert.IsNull(host.RuntimeService.GetAbortReason(session.SessionId));
    }

    [TestMethod]
    public async Task Cancel_BeforeTheDetachedPromptOfAChildStarts_StartsNoRunOfTheChild_AndALaterPromptToItRuns()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grace = TimeSpan.FromSeconds(10);
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            // A run of the child left going after the abort would make the driver wait this long.
            AbortGrace = grace,
            Observer = (item, _) => { if (IsAbortRecord(item.Event)) aborted.TrySetResult(); return ValueTask.CompletedTask; },
        });
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        var childId = Guid.CreateVersion7().ToString();
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        SessionViewDescriptor? child = null;
        provider.Script(session.SessionId, async (request, cancellationToken) =>
        {
            child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child", session.SessionId, null, null, null, childId);
            // As `alta session send` does for an agent: the send to the child is detached from the command that made it.
            _ = host.RuntimeService.SendAsync(child, options, new AgentSendOptions { Input = AgentInput.Text("child work") }, CancellationToken.None);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ScriptedTurnProvider.Answer("never");
        });
        provider.Script(childId, ScriptedTurnProvider.WaitThenReply(new TaskCompletionSource().Task, "never"));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The send to the child is admitted, then held before it does anything until the abort was recorded.
        host.RuntimeService.SendStarting = (sessionId, token) =>
        {
            if (!string.Equals(sessionId, childId, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
            held.TrySetResult();
            return HoldUntilAbortedAsync(aborted.Task, token);
        };
        using var signalled = new CancellationTokenSource();

        var run = driver.RunAsync(session, new HeadlessPrompt("delegate"), null, signalled.Token);
        await held.Task.WaitAsync(Hang);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await signalled.CancelAsync();
        var result = await run.WaitAsync(Hang);
        stopwatch.Stop();

        Assert.AreEqual(HeadlessRunStatus.Aborted, result.Status, result.FailureMessage);
        Assert.AreEqual("cancelled", result.AbortReason);
        Assert.AreEqual(0, provider.RequestsOf(childId).Length, "A prompt sent to a child before the abort of its tree does not start after it.");
        Assert.IsTrue(stopwatch.Elapsed < grace, $"The driver does not wait the grace for a child that starts nothing ({stopwatch.Elapsed}).");
        await host.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);

        host.RuntimeService.SendStarting = null;
        provider.SessionTurns.TryRemove(childId, out _);
        provider.Script(childId, ScriptedTurnProvider.Reply("Later answer."));
        await host.RuntimeService.SendAsync(child!, options, new AgentSendOptions { Input = AgentInput.Text("again") }).WaitAsync(Hang);
        await host.RuntimeService.WhenSettledAsync(childId).WaitAsync(Hang);
        Assert.AreEqual(1, provider.RequestsOf(childId).Length, "The abort does not hold a prompt sent to the child after it.");
    }

    private static bool IsAbortRecord(SessionRuntimeEvent runtimeEvent)
        => runtimeEvent is SessionAgentEvent { Event: AgentSessionUpdateEvent update } && SessionOutcome.AbortReasonOf(update) is not null;

    // Holds a send before it reaches the runtime until it is cancelled, or until the abort was recorded when nothing cancels it.
    private static async Task HoldUntilAbortedAsync(Task aborted, CancellationToken cancellationToken)
        => await Task.WhenAny(aborted, Task.Delay(Timeout.Infinite, cancellationToken));

    [TestMethod]
    public async Task Open_ASessionIdOpensOrCreates_AResumeOfAnUnknownSessionIsNotFound_AndALeaseHeldElsewhereRefuses()
    {
        var root = new HeadlessTestRoot();
        await using var scope = root;
        var host = await root.CreateHostAsync(new ScriptedTurnProvider(), readOnlyGlobalState: true, fullTrust: true, headlessInstructions: true,
            sessionLeaseOwner: SessionLeaseOwner.CliKind);
        await using var driver = new HeadlessSessionDriver(host);
        var id = Guid.NewGuid().ToString();
        var leases = new SessionLeaseStore(host.CatalogOptions);

        var created = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, SessionId = id, ModelRef = "scripted:scripted-model" });
        Assert.AreEqual(HeadlessOpenStatus.Created, created.Status, created.Message);
        Assert.AreEqual(id, created.Session!.SessionId);
        Assert.AreEqual(SessionLeaseOwner.CliKind, leases.TryReadHolder(id)?.OwnerKind, "An open session holds its lease.");
        Assert.AreEqual(HeadlessOpenStatus.SessionInUse, (await driver.OpenAsync(new HeadlessSessionRequest { SessionId = id })).Status, "A session is open once.");
        await driver.CloseAsync(created.Session);

        var reopened = await driver.OpenAsync(new HeadlessSessionRequest { SessionId = id });
        Assert.AreEqual(HeadlessOpenStatus.Opened, reopened.Status, reopened.Message);
        StringAssert.StartsWith(reopened.Session!.ModelRef, "scripted:scripted-model");
        await driver.CloseAsync(reopened.Session);
        // The attachment the opening made still holds the lease: it goes with the host.
        await host.RuntimeService.DetachRuntimeSessionAsync(id);
        Assert.IsNull(leases.TryReadHolder(id), "A closed session without an attachment releases its lease.");

        Assert.AreEqual(HeadlessOpenStatus.NotFound, (await driver.OpenAsync(new HeadlessSessionRequest { ResumeSessionId = Guid.NewGuid().ToString() })).Status);
        // Another process holds it (a second lease of this process stands for one).
        using (var elsewhere = leases.TryAcquire(id, new SessionLeaseOwner(SessionLeaseOwner.DesktopKind, "other")))
        {
            Assert.IsNotNull(elsewhere);
            var refused = await driver.OpenAsync(new HeadlessSessionRequest { ResumeSessionId = id });
            Assert.AreEqual(HeadlessOpenStatus.SessionInUse, refused.Status);
            StringAssert.Contains(refused.Message, "CodeAlta Desktop");
        }

        Assert.AreEqual(HeadlessOpenStatus.Opened, (await driver.OpenAsync(new HeadlessSessionRequest { ResumeSessionId = id })).Status, "Once released, the session is continued.");
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest, (await driver.OpenAsync(new HeadlessSessionRequest { SessionId = "nope" })).Status);
    }

    [TestMethod]
    public async Task ChangeSelection_ChangesWhatTheNextPromptsRunWith_AndRefusesWhatTheSessionCannotRun()
    {
        var (root, provider, host) = await CreateAsync();
        await using var scope = root;
        await using var driver = new HeadlessSessionDriver(host);
        var session = (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" })).Session!;
        var prompts = (await host.Commands.GetSelectionChoicesAsync(session.SessionId))!.Prompts.Select(static prompt => prompt.Id).ToArray();
        CollectionAssert.Contains(prompts, "plan");

        var changed = await driver.ChangeSelectionAsync(session, new HeadlessSelectionChange { AgentPromptId = "plan", Reasoning = AgentReasoningEffort.Low });

        Assert.AreEqual(HeadlessOpenStatus.Opened, changed.Status, changed.Message);
        Assert.AreEqual("plan", changed.Session!.AgentPromptId);
        Assert.AreEqual(AgentReasoningEffort.Low, changed.Session.ReasoningEffort);
        Assert.AreEqual("scripted-model", changed.Session.ModelId, "What the change does not name stays.");
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Planned."));
        var result = await driver.RunAsync(changed.Session, new HeadlessPrompt("plan it")).WaitAsync(Hang);
        Assert.AreEqual(HeadlessRunStatus.Completed, result.Status, result.FailureMessage);
        Assert.AreEqual(AgentReasoningEffort.Low, provider.RequestsOf(session.SessionId).Last().ReasoningEffort);
        var stored = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId);
        Assert.AreEqual("plan", stored!.AgentPromptId, "The prompt ran with the agent prompt chosen.");

        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, (await driver.ChangeSelectionAsync(changed.Session, new() { ModelRef = "scripted:missing-model" })).Status);
        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, (await driver.ChangeSelectionAsync(changed.Session, new() { ModelRef = "missing:model" })).Status);
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest, (await driver.ChangeSelectionAsync(changed.Session, new() { AgentPromptId = "no-such-prompt" })).Status);
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest, (await driver.ChangeSelectionAsync(changed.Session, new() { ModelRef = "not a model" })).Status);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => driver.ChangeSelectionAsync(changed.Session with { SessionId = Guid.NewGuid().ToString() }, new()));
    }
}
