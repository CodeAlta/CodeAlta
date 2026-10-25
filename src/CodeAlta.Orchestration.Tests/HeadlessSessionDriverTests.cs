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
}
