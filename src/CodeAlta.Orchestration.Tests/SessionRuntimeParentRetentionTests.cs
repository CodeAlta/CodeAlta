using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Actors;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

public sealed partial class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    public Task ParentRetention_PluginObservationKeepsDistinctLineageAfterCanceledWait() => Fixture.Run(async f =>
    {
        var parent = f.NewSession();
        var source = Guid.NewGuid().ToString("D");
        await f.Persist(parent);
        f.Session.ParentSessionId = parent.SessionId;
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var plugin = new ParentRetentionObserverPlugin();
        await using var manager = new PluginRuntimeManager();
        var started = await f.Wait(manager.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = f.GlobalRoot,
            IsHeadless = true,
            BuiltIns = [new BuiltInPluginDefinition
            {
                Id = "retention-observer", DisplayName = "Synthetic retention observer",
                PluginType = typeof(ParentRetentionObserverPlugin), Factory = () => plugin,
            }],
        }).AsTask());
        Assert.AreEqual(1, started.ActivePlugins.Count, string.Join("; ", started.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var observerProperty = typeof(SessionRuntimeService).GetProperty("PluginEventObserver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var prior = observerProperty.GetValue(f.Runtime);
        observerProperty.SetValue(f.Runtime, new RuntimePluginAgentEventObserver(manager, static (_, failure) => ValueTask.FromException(failure)));
        var post = ParentEventPoster(f, _ => AssertParentRetirementBlocked(f.Runtime, source));
        var original = f.Track(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
            f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "plugin-held", null,
            "<notify-parent>held by synthetic observation</notify-parent>")));
        try
        {
            await f.Ready(plugin.Entered.Task);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await f.ExpectCancellation(original.WaitAsync(canceled.Token));
            Assert.IsFalse(original.IsCompleted);
            AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
            AssertParentRetirementBlocked(f.Runtime, parent.SessionId);
            AssertParentRetirementBlocked(f.Runtime, source);
            Assert.AreEqual(0, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
        }
        finally
        {
            plugin.Release.TrySetResult();
            await f.Wait(original);
            observerProperty.SetValue(f.Runtime, prior);
        }
        Assert.AreEqual(1, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
        using var released = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
    });

    private sealed class ParentRetentionObserverPlugin : PluginBase
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
        {
            if (context.Event is not AgentContentCompletedEvent) return;
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    [TestMethod]
    [DataRow("progress")]
    [DataRow("handoff")]
    [DataRow("answer")]
    [DataRow("idle-first")]
    [DataRow("error")]
    public Task ParentRetention_ClaimsBeforePublicationThroughDurableQueue(string route) => Fixture.Run(async f =>
    {
        var parent = f.NewSession();
        await f.Persist(parent);
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var publications = 0;
        var post = ParentEventPoster(f, _ =>
        {
            AssertParentRetirementBlocked(f.Runtime, parent.SessionId);
            AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
            publications++;
        });
        var run = new AgentRunId("parent-retention");
        var completed = new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId, f.Session.SessionId,
            DateTimeOffset.UtcNow, run, AgentContentKind.Assistant, "answer", null,
            route is "progress" or "handoff" ? $"<notify-parent kind=\"{route}\">inert update</notify-parent>" : "inert answer");
        var idle = new AgentSessionUpdateEvent(f.Provider.Descriptor.ProviderId, f.Session.SessionId,
            DateTimeOffset.UtcNow, run, AgentSessionUpdateKind.Idle, "idle");
        if (route == "idle-first") await f.Wait(post(idle));
        if (route == "error")
            await f.Wait(post(new AgentErrorEvent(f.Provider.Descriptor.ProviderId, f.Session.SessionId,
                DateTimeOffset.UtcNow, "inert failure", exception: null, runId: run)));
        else
            await f.Wait(post(completed));
        if (route == "answer") await f.Wait(post(idle));
        Assert.IsTrue(publications > 0);
        var queued = (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts;
        Assert.AreEqual(1, queued.Count);
        Assert.AreEqual("parent-notify", queued[0].Kind);
        Assert.AreEqual(f.Session.SessionId, queued[0].SubmittedBy?.SourceSessionId);
        // The original capture is finished; the durable queue/lineage now owns protection.
        using var released = TryClaimParentRetirement(f.Runtime, parent.SessionId);
        Assert.IsNotNull(released);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ParentRetention_CanceledWaitKeepsOriginalSteerClaim(bool shutdown) => Fixture.Run(async f =>
    {
        var parent = f.NewSession();
        await f.Persist(parent);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(parent, f.Options));
        var parentProvider = f.Provider.Latest;
        parentProvider.EmitState(AgentSessionUpdateKind.Warning, new AgentRunId("active-parent"), "active");
        // A mailbox query joins the earlier forwarded event before the child is attached.
        Assert.AreEqual("active-parent", (await f.Wait(f.Runtime.GetCurrentStateAsync(parent.SessionId))).Entry!.ActiveRunId);
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var post = ParentEventPoster(f, _ => AssertParentRetirementBlocked(f.Runtime, parent.SessionId));
        var original = f.Track(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
            f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "held", null,
            "<notify-parent>inert held steer</notify-parent>")));
        await f.Ready(f.Provider.SteerStarted.Task);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await f.ExpectCancellation(original.WaitAsync(canceled.Token));
        Assert.IsFalse(original.IsCompleted);
        AssertParentRetirementBlocked(f.Runtime, parent.SessionId);
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        Task? close = null;
        if (shutdown)
        {
            close = f.Track(f.Runtime.DisposeAsync().AsTask());
            await f.Ready(f.Provider.SteerCancelled.Task);
            Assert.IsFalse(close.IsCompleted);
            AssertParentRetirementBlocked(f.Runtime, parent.SessionId);
        }
        f.Provider.ReleaseSteer.TrySetResult();
        await f.Wait(original);
        if (close is not null) await f.Wait(close);
        else Assert.AreEqual(0, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
        using var released = TryClaimParentRetirement(f.Runtime, parent.SessionId);
        Assert.IsNotNull(released);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ParentRetention_RetirementWinsBeforeCaptureWithoutPublication(bool tombstone) => Fixture.Run(async f =>
    {
        // This case requires retirement to win before any notification capture. The normal fixture
        // emits Idle during subscription; its independent tail can still own a valid lineage claim
        // after EnsureCoordinatorSessionAsync returns. Do not race that unrelated bootstrap work.
        f.Provider.EmitIdleOnSubscribe = false;
        var parent = f.NewSession();
        await f.Persist(parent);
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        using var retired = TryClaimParentRetirement(f.Runtime, parent.SessionId);
        Assert.IsNotNull(retired);
        if (tombstone)
        {
            var service = typeof(SessionRuntimeService);
            var gate = service.GetField("_identityGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Runtime)!;
            lock (gate)
                ((HashSet<string>)service.GetField("_retiredSessionIds", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(f.Runtime)!).Add(parent.SessionId);
            retired.Dispose();
        }
        var publications = 0;
        var post = ParentEventPoster(f, _ => publications++);
        await f.Wait(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
            f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "refused", null,
            "<notify-parent>must not deliver</notify-parent>")));
        Assert.AreEqual(0, publications);
        Assert.AreEqual(0, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
    });

    [TestMethod]
    public Task ParentRetention_CaptureGapAndCanceledWaitRemainProtected() => Fixture.Run(async f =>
    {
        var parent = f.NewSession();
        var source = Guid.NewGuid().ToString("D");
        await f.Persist(parent);
        f.Session.ParentSessionId = parent.SessionId;
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actor = (SessionActor)typeof(SessionRuntimeService).GetMethod("GetActorForWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Runtime, [f.Session.SessionId])!;
        var actorHold = f.Track(actor.QueryAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }).AsTask());
        var publications = 0;
        var post = ParentEventPoster(f, _ => publications++);
        Task original = Task.CompletedTask;
        try
        {
            await f.Ready(entered.Task);
            original = f.Track(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
                f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "captured", null,
                "<notify-parent>not yet delivered</notify-parent>")));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await f.ExpectCancellation(original.WaitAsync(canceled.Token));
            Assert.IsFalse(original.IsCompleted);
            AssertParentRetirementBlocked(f.Runtime, parent.SessionId);
            AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
            AssertParentRetirementBlocked(f.Runtime, source);
            Assert.AreEqual(0, publications);
            Assert.AreEqual(0, f.Provider.Steers);
            Assert.AreEqual(0, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
        }
        finally { release.TrySetResult(); }
        await f.Wait(actorHold);
        await f.Wait(original);
        Assert.AreEqual(1, (await f.Wait(f.Journal.ReadLatestStateAsync(parent.SessionId, parent.CreatedAt)))!.QueuedPrompts.Count);
        using var released = TryClaimParentRetirement(f.Runtime, parent.SessionId);
        using var releasedSource = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
        Assert.IsNotNull(releasedSource);
    });

    [TestMethod]
    [DataRow("parent", false)]
    [DataRow("parent", true)]
    [DataRow("host", false)]
    [DataRow("host", true)]
    [DataRow("running-host", false)]
    [DataRow("running-host", true)]
    public Task ParentAndHostRetention_OnlyConclusiveSteerFailureAllowsQueueFallback(string route, bool orphan) => Fixture.Run(async f =>
    {
        var target = f.NewSession();
        await f.Persist(target);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(target, f.Options));
        f.Provider.Latest.EmitState(AgentSessionUpdateKind.Warning, new AgentRunId("failing-steer"), "active");
        Assert.AreEqual("failing-steer", (await f.Wait(f.Runtime.GetCurrentStateAsync(target.SessionId))).Entry!.ActiveRunId);
        f.Provider.SteerFailure = orphan
            ? new AgentDependencyRetentionException("fixture steer", "unconfirmed release", [new IOException("inert prerequisite failure")], f.Provider)
            : new IOException("inert terminal steer failure");
        Task original;
        if (route == "parent")
        {
            f.Session.ParentSessionId = target.SessionId;
            await f.Persist(f.Session);
            await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
            var post = ParentEventPoster(f, _ => AssertParentRetirementBlocked(f.Runtime, target.SessionId));
            original = f.Track(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
                f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "failure", null,
                "<notify-parent>inert failure path</notify-parent>")));
        }
        else
            original = f.Track(route == "host"
                ? f.Runtime.DeliverHostPromptAsync(target.SessionId, "inert failure path", "fixture")
                : f.Runtime.DeliverHostPromptToRunningTurnAsync(target.SessionId, "inert failure path", "fixture"));
        await f.Ready(f.Provider.SteerStarted.Task);
        AssertParentRetirementBlocked(f.Runtime, target.SessionId);
        f.Provider.ReleaseSteer.TrySetResult();
        if (orphan)
        {
            var failure = await Assert.ThrowsAsync<Exception>(() => f.Wait(original));
            f.Expected(failure);
            Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
            AssertParentRetirementBlocked(f.Runtime, target.SessionId);
            Assert.AreEqual(0, (await f.Wait(f.Journal.ReadLatestStateAsync(target.SessionId, target.CreatedAt)))!.QueuedPrompts.Count);
            var closeFailure = await Assert.ThrowsAsync<Exception>(() => f.Wait(f.Close()));
            f.Expected(closeFailure);
            Assert.IsTrue(AgentDependencyRetentionException.Contains(closeFailure));
            AssertParentRetirementBlocked(f.Runtime, target.SessionId);
        }
        else
        {
            await f.Wait(original);
            Assert.AreEqual(1, (await f.Wait(f.Journal.ReadLatestStateAsync(target.SessionId, target.CreatedAt)))!.QueuedPrompts.Count);
            using var released = TryClaimParentRetirement(f.Runtime, target.SessionId);
            Assert.IsNotNull(released);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ParentAndHostRetention_QueuedWorkerOutlivesCanceledWaitAndRowRemoval(bool host) => Fixture.Run(async f =>
    {
        var target = f.NewSession();
        await f.Persist(target);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(target, f.Options));
        Task original;
        if (host)
            original = f.Track(f.Runtime.DeliverHostPromptAsync(target.SessionId, "inert queue worker", "fixture"));
        else
        {
            f.Session.ParentSessionId = target.SessionId;
            await f.Persist(f.Session);
            await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
            var post = ParentEventPoster(f, _ => AssertParentRetirementBlocked(f.Runtime, target.SessionId));
            original = f.Track(post(new AgentContentCompletedEvent(f.Provider.Descriptor.ProviderId,
                f.Session.SessionId, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, "queued", null,
                "<notify-parent>inert queue worker</notify-parent>")));
        }
        await f.Ready(f.Provider.SendStarted.Task);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await f.ExpectCancellation(original.WaitAsync(cancellation.Token));
        Assert.IsFalse(original.IsCompleted);
        AssertParentRetirementBlocked(f.Runtime, target.SessionId);
        var state = (await f.Wait(f.Journal.ReadLatestStateAsync(target.SessionId, target.CreatedAt)))!;
        Assert.AreEqual("submitting", state.QueuedPrompts.Single().State);
        // Synthetic public-history eviction is not authority to release the captured worker.
        state.QueuedPrompts.Clear();
        await f.Wait(f.Journal.AppendStateAsync(target, state));
        AssertParentRetirementBlocked(f.Runtime, target.SessionId);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Wait(original);
        using var released = TryClaimParentRetirement(f.Runtime, target.SessionId);
        Assert.IsNotNull(released);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    // Exercise the real forwarding, actor, projector and delivery path with a deterministic publication
    // probe. Reflection avoids adding production test hooks or opening a native/profile-backed host.
    private static Func<AgentEvent, Task> ParentEventPoster(Fixture f, Action<SessionRuntimeEvent> publish)
    {
        var service = typeof(SessionRuntimeService);
        var entries = service.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Runtime)!;
        var entry = entries.GetType().GetProperty("Item")!.GetValue(entries, [f.Session.SessionId])!;
        var projectorType = service.GetNestedType("EventProjector", BindingFlags.NonPublic)!;
        var observe = entry.GetType().GetMethod("ObserveEvent")!.CreateDelegate<Action<AgentEvent>>(entry);
        var projector = Activator.CreateInstance(projectorType, [f.Session.SessionId, publish, observe])!;
        projectorType.GetProperty("Entry")!.SetValue(projector, entry);
        var actor = service.GetMethod("GetActorForWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Runtime, [f.Session.SessionId]);
        var post = service.GetMethod("PostAgentEventToActorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return value => (Task)post.Invoke(f.Runtime, [actor, f.Session.SessionId, projector, value, true])!;
    }

    private static IDisposable? TryClaimParentRetirement(SessionRuntimeService runtime, string sessionId)
        => (IDisposable?)typeof(SessionRuntimeService).GetMethod("TryClaimRuntimeRetirement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [sessionId]);

    private static void AssertParentRetirementBlocked(SessionRuntimeService runtime, string sessionId)
    {
        using var claim = TryClaimParentRetirement(runtime, sessionId);
        Assert.IsNull(claim, "Captured parent work must exclude retirement before publication and until its original ends.");
    }
}
