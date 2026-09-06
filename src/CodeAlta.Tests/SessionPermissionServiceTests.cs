using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

// Pure mailbox fixtures: no runtime/host/provider constructors or application/profile discovery.
[TestClass]
public sealed class SessionPermissionServiceTests
{
    [TestMethod]
    public async Task AutoApprove_AllowsOnceWithoutPendingRetention()
    {
        await using var service = new SessionPermissionService();
        var registration = await service.RegisterAsync("session", Request(), true, CancellationToken.None);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await registration.Completion).Kind);
        Assert.IsFalse(registration.IsPending);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
        Assert.IsFalse(await service.ResolveAsync(registration.Snapshot.Handle, AgentPermissionDecisionKind.Deny));
    }

    [TestMethod]
    public async Task PendingSnapshot_IsIndependentImmutableScalarData()
    {
        await using var service = new SessionPermissionService();
        var actions = new List<AgentCommandPreviewAction> { new(AgentCommandPreviewKind.Read, "original") };
        var amendments = new List<string> { "original" };
        var request = Request() with { Actions = actions, ProposedExecPolicyAmendment = amendments };
        var registration = await service.RegisterAsync("captured-session", request, false, CancellationToken.None);
        actions.Clear();
        amendments.Clear();
        request = request with { SessionId = "mutated", InteractionId = "mutated", Command = "mutated" };
        var snapshots = await service.ListAsync();
        var snapshot = snapshots.Single();
        Assert.AreEqual("captured-session", snapshot.Handle.SessionId);
        Assert.AreEqual("interaction", snapshot.Handle.InteractionId);
        Assert.AreEqual("run", snapshot.Handle.RunId);
        Assert.AreEqual("echo preview", snapshot.Command);
        Assert.IsFalse(snapshots is SessionPermissionSnapshot[]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<SessionPermissionSnapshot>)snapshots)[0] = snapshot with { Command = "changed" });
        Assert.IsTrue(registration.IsPending);
        Assert.IsFalse(registration.Completion.IsCompleted);
        await service.CancelAsync(snapshot.Handle);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registration.Completion).Kind);
        Assert.AreEqual(1, snapshots.Count, "An earlier snapshot is not a live mutable view.");
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task Resolve_RejectsWrongIdentityInvalidValueReplayAndReusedProviderId()
    {
        await using var service = new SessionPermissionService();
        var first = await service.RegisterAsync("session", Request(), false, CancellationToken.None);
        var handle = first.Snapshot.Handle;
        foreach (var wrong in new[]
        {
            handle with { SessionId = "other" }, handle with { RunId = "other" }, handle with { RunId = null },
            handle with { InteractionId = "other" }, handle with { AttemptId = Guid.NewGuid() },
        })
        {
            Assert.IsFalse(await service.ResolveAsync(wrong, AgentPermissionDecisionKind.AllowOnce));
            Assert.IsFalse(await service.CancelAsync(wrong));
            Assert.IsFalse(await service.IsPendingAsync(wrong));
        }

        Assert.IsFalse(await service.ResolveAsync(handle, (AgentPermissionDecisionKind)123));
        Assert.IsTrue(await service.IsPendingAsync(handle));
        Assert.IsTrue(await service.ResolveAsync(handle, AgentPermissionDecisionKind.Deny));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await first.Completion).Kind);
        Assert.IsFalse(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce));
        var second = await service.RegisterAsync("session", Request(), false, CancellationToken.None);
        Assert.AreNotEqual(handle.AttemptId, second.Snapshot.Handle.AttemptId);
        Assert.IsFalse(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce));
        Assert.IsTrue(second.IsPending);
        Assert.IsTrue(await service.ResolveAsync(second.Snapshot.Handle, AgentPermissionDecisionKind.AllowForSession));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowForSession, (await second.Completion).Kind);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationBeforeRegistration_NeverApproves(bool autoApprove)
    {
        await using var service = new SessionPermissionService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var registration = await service.RegisterAsync("session", Request(), autoApprove, cancellation.Token);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registration.Completion).Kind);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task CancellationDuringRegistration_RemovesPendingCompletion()
    {
        await using var service = new SessionPermissionService();
        using var cancellation = new CancellationTokenSource();
        var registering = service.RegisterAsync("session", Request(), false, cancellation.Token);
        cancellation.Cancel();
        var registration = await registering;
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registration.Completion).Kind);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task CancellationBeforeCleanup_IsExcludedFromReadsAndCannotApprove()
    {
        await using var service = new SessionPermissionService();
        using var cancellation = new CancellationTokenSource();
        var registration = await service.RegisterAsync("session", Request(), false, cancellation.Token);
        var listed = -1;
        var pending = true;
        var accepted = true;
        // Cancellation callbacks execute LIFO. This callback runs before WaitAsync's cancellation
        // callback, so the owner's asynchronous cleanup cannot have been scheduled yet.
        using var beforeCleanup = cancellation.Token.Register(() =>
        {
            listed = service.ListAsync().AsTask().GetAwaiter().GetResult().Count;
            pending = service.IsPendingAsync(registration.Snapshot.Handle).AsTask().GetAwaiter().GetResult();
            accepted = service.ResolveAsync(registration.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce)
                .AsTask().GetAwaiter().GetResult();
        });

        cancellation.Cancel();

        Assert.AreEqual(0, listed, "Canceled requests must be excluded even before their cleanup message.");
        Assert.IsFalse(pending);
        Assert.IsFalse(accepted);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registration.Completion).Kind);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task CallerCancellationAfterRegistration_RejectsLateAllow()
    {
        await using var service = new SessionPermissionService();
        using var cancellation = new CancellationTokenSource();
        var registration = await service.RegisterAsync("session", Request(), false, cancellation.Token);
        Assert.IsTrue(registration.IsPending);
        cancellation.Cancel();
        Assert.IsFalse(await service.ResolveAsync(registration.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registration.Completion).Kind);
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task CancellationAndResponseRace_CompletesExactlyOnceWithoutRetention()
    {
        await using var service = new SessionPermissionService();
        for (var i = 0; i < 40; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var registration = await service.RegisterAsync("session", Request(), false, cancellation.Token);
            var response = service.ResolveAsync(registration.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce).AsTask();
            cancellation.Cancel();
            var accepted = await response;
            var decision = await registration.Completion;
            Assert.AreEqual(accepted ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Cancel, decision.Kind);
            Assert.IsFalse(await service.CancelAsync(registration.Snapshot.Handle));
            Assert.AreEqual(decision, await registration.Completion);
            Assert.AreEqual(0, (await service.ListAsync()).Count);
        }
    }

    [TestMethod]
    public async Task ConcurrentSessionsAndRuns_HaveIndependentAttemptLifetimes()
    {
        await using var service = new SessionPermissionService();
        var registrations = await Task.WhenAll(
            service.RegisterAsync("one", Request(), false, CancellationToken.None),
            service.RegisterAsync("two", Request(), false, CancellationToken.None),
            service.RegisterAsync("one", Request() with { RunId = null }, false, CancellationToken.None));
        Assert.AreEqual(3, (await service.ListAsync()).Count);
        Assert.AreEqual(1, await service.CancelRunAsync("one", "run"));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await registrations[0].Completion).Kind);
        Assert.IsTrue(registrations[1].IsPending);
        Assert.IsTrue(registrations[2].IsPending);
        Assert.AreEqual(1, await service.CancelRunAsync("one", null));
        Assert.IsTrue(await service.ResolveAsync(registrations[1].Snapshot.Handle, AgentPermissionDecisionKind.Deny));
        await Task.WhenAll(registrations.Select(static registration => registration.Completion));
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    [TestMethod]
    public async Task Shutdown_CompletesPendingAndRejectsNewRequestsAndLateResponses()
    {
        var service = new SessionPermissionService();
        try
        {
            var pending = await service.RegisterAsync("session", Request(), false, CancellationToken.None);
            await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await pending.Completion).Kind);
            Assert.AreEqual(0, (await service.ListAsync()).Count);
            Assert.IsFalse(await service.ResolveAsync(pending.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce));
            var late = await service.RegisterAsync("session", Request(), true, CancellationToken.None);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await late.Completion).Kind);
        }
        finally { await service.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ValidationFailure_DoesNotCreatePendingState()
    {
        await using var service = new SessionPermissionService();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RegisterAsync(" ", Request(), false, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => service.RegisterAsync("session", null!, false, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RegisterAsync("session", Request() with { InteractionId = "" }, false, CancellationToken.None));
        Assert.AreEqual(0, (await service.ListAsync()).Count);
    }

    internal static AgentCommandPermissionRequest Request() => new(
        new ModelProviderId("test"), "session", DateTimeOffset.UnixEpoch, new AgentRunId("run"), "interaction",
        null, "echo preview", null, null, "test reason", null, null, null);
}
