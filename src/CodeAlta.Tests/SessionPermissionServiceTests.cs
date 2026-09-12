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

    [TestMethod]
    public async Task OwnedPermission_ValidatesCompletePayloadAndRestrictsTrustedResolution()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var request = Request() with { RunId = null, WorkingDirectory = "Q:\\fixture" };
        var deliveries = new List<Task<AgentPermissionDecision>>();
        try
        {
            var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
            Assert.IsNotNull(execution);
            Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
            foreach (var invalid in new AgentPermissionRequest[]
            {
                new AgentGenericPermissionRequest(new ModelProviderId("test"), "session", DateTimeOffset.UnixEpoch, null, "interaction", "commandExecution", default),
                new AgentFileChangePermissionRequest(new ModelProviderId("test"), "session", DateTimeOffset.UnixEpoch, null, "interaction", "Q:\\fixture", null),
                request with { SessionId = "other" }, request with { ProviderId = new ModelProviderId("other") },
                request with { SessionId = "SESSION" },
                request with { Kind = "other" },
                request with { InteractionId = " " }, request with { RunId = new AgentRunId("bad\ud800") },
                request with { RunId = new AgentRunId(new string('x', SessionPermissionService.OwnedIdentityLimit + 1)) },
                request with { Command = " " }, request with { WorkingDirectory = null },
                request with { Command = new string('x', SessionPermissionService.OwnedCommandLimit + 1) },
                request with { WorkingDirectory = new string('x', SessionPermissionService.OwnedDirectoryLimit + 1) },
                request with { Reason = new string('x', SessionPermissionService.OwnedReasonLimit + 1) },
                request with { InteractionId = new string('x', SessionPermissionService.OwnedIdentityLimit + 1) },
                request with { InteractionId = " padded" }, request with { InteractionId = "bad\0id" },
                request with { WorkingDirectory = "bad\udc00" }, request with { Command = "bad\0command" },
                request with { Reason = "bad\ud800" }, request with { ApprovalId = "approval" },
                request with { Actions = [] }, request with { Network = new("host", "https") },
                request with { ProposedExecPolicyAmendment = [] }, request with { ProposedNetworkPolicyAmendments = [] },
            })
            {
                var denied = service.HandleOwnedCommandAsync(execution, invalid, CancellationToken.None);
                deliveries.Add(denied);
                Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await denied.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            }
            request = request with
            {
                Command = new string('x', SessionPermissionService.OwnedCommandLimit - 2) + "\U0001f600",
                WorkingDirectory = new string('x', SessionPermissionService.OwnedDirectoryLimit),
                Reason = new string('x', SessionPermissionService.OwnedReasonLimit),
                InteractionId = new string('x', SessionPermissionService.OwnedIdentityLimit),
            };
            var pending = service.HandleOwnedCommandAsync(execution, request, CancellationToken.None);
            deliveries.Add(pending);
            var snapshot = (await service.ListAsync()).Single();
            Assert.AreEqual(request.Command, snapshot.Command);
            Assert.AreEqual(request.WorkingDirectory, snapshot.WorkingDirectory);
            Assert.AreEqual(request.Reason, snapshot.Reason);
            var handle = snapshot.Handle;
            Assert.IsFalse(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowForSession));
            Assert.IsTrue(await service.IsPendingAsync(handle));
            Assert.IsTrue(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce));
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            await service.CloseOwnedExecutionAsync(execution);
            Assert.IsFalse(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
        }
        finally
        {
            await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, []);
        }
    }

    [TestMethod]
    public async Task OwnedPermission_GlobalCapacityReclaimsCompletedAttemptsAndExecutions()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        try
        {
            var request = Request() with { WorkingDirectory = "Q:\\fixture" };
            SessionPermissionService.OwnedPermissionExecution? first = null;
            for (var i = 0; i < SessionPermissionService.OwnedPendingLimit / SessionPermissionService.OwnedPendingPerExecutionLimit; i++)
            {
                var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
                Assert.IsNotNull(execution);
                first ??= execution;
                Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
                for (var j = 0; j < SessionPermissionService.OwnedPendingPerExecutionLimit; j++)
                    deliveries.Add(service.HandleOwnedCommandAsync(execution, request, CancellationToken.None));
            }
            Assert.AreEqual(SessionPermissionService.OwnedPendingLimit, (await service.ListAsync()).Count);
            var extra = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
            Assert.IsNotNull(extra);
            Assert.IsTrue(await service.BindOwnedExecutionAsync(extra, Guid.NewGuid(), attachment, new ModelProviderId("test")));
            var denied = service.HandleOwnedCommandAsync(extra, request, CancellationToken.None);
            deliveries.Add(denied);
            Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await denied.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            await service.CloseOwnedExecutionAsync(first!);
            var reclaimed = service.HandleOwnedCommandAsync(extra, request, CancellationToken.None);
            deliveries.Add(reclaimed);
            Assert.AreEqual(SessionPermissionService.OwnedPendingLimit - SessionPermissionService.OwnedPendingPerExecutionLimit + 1,
                (await service.ListAsync()).Count);
            await service.CloseOwnedExecutionAsync(extra);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await reclaimed.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            // Individually closed records do not exhaust the live execution cap or need tombstones.
            for (var i = 0; i < SessionPermissionService.OwnedExecutionLimit * 2; i++)
            {
                var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
                Assert.IsNotNull(execution);
                await service.CloseOwnedExecutionAsync(execution);
            }
        }
        finally
        {
            await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, []);
        }
    }

    [TestMethod]
    public async Task OwnedPermission_AssociationAndCancellationResolutionOrderingAreAuthoritative()
    {
        var service = new SessionPermissionService();
        await using var other = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        var cancellations = new List<CancellationTokenSource>();
        try
        {
            Assert.IsNull(await service.CreateOwnedExecutionAsync(Guid.Empty, "session", CancellationToken.None));
            Assert.IsNull(await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "bad\ud800", CancellationToken.None));
            for (var i = 0; i < 12; i++)
            {
                var cancellation = new CancellationTokenSource();
                cancellations.Add(cancellation);
                var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", cancellation.Token);
                Assert.IsNotNull(execution);
                Assert.IsNull(await service.CreateOwnedExecutionAsync(execution.OperationId, "session", cancellation.Token));
                Assert.IsFalse(await other.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
                Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
                Assert.IsFalse(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
                var request = Request() with { RunId = null, WorkingDirectory = "Q:\\fixture", Command = "inert \U0001f600" };
                var wrongOwner = other.HandleOwnedCommandAsync(execution, request, CancellationToken.None);
                deliveries.Add(wrongOwner);
                Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await wrongOwner.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
                var pending = service.HandleOwnedCommandAsync(execution, request, CancellationToken.None);
                deliveries.Add(pending);
                var handle = (await service.ListAsync()).Single().Handle;
                Assert.IsFalse(await service.ResolveAsync(handle with { AttemptId = Guid.NewGuid() }, AgentPermissionDecisionKind.AllowOnce));
                if (i % 3 == 0) cancellation.Cancel();
                var response = service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce).AsTask();
                if (i % 3 == 1) Assert.IsTrue(await response); // Accepted before invalidation remains accepted.
                cancellation.Cancel(); // Other iterations race cancellation against the queued response.
                var accepted = await response;
                if (i % 3 == 0) Assert.IsFalse(accepted);
                await service.CloseOwnedExecutionAsync(execution);
                Assert.AreEqual(accepted ? AgentPermissionDecisionKind.AllowOnce : AgentPermissionDecisionKind.Cancel,
                    (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
                Assert.IsFalse(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce));
                var late = service.HandleOwnedCommandAsync(execution, request, CancellationToken.None);
                deliveries.Add(late);
                Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await late.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            }
        }
        finally
        {
            await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, cancellations);
        }
    }

    [TestMethod]
    public async Task OwnedPermission_CapacityAndCancellationReleaseBookkeeping()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var cancellation = new CancellationTokenSource();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        try
        {
            var executions = new List<SessionPermissionService.OwnedPermissionExecution>();
            for (var i = 0; i < SessionPermissionService.OwnedExecutionLimit; i++)
            {
                var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", cancellation.Token);
                Assert.IsNotNull(execution);
                executions.Add(execution);
            }
            Assert.IsNull(await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", cancellation.Token));
            var first = executions[0];
            Assert.IsTrue(await service.BindOwnedExecutionAsync(first, Guid.NewGuid(), attachment, new ModelProviderId("test")));
            for (var i = 0; i < SessionPermissionService.OwnedPendingPerExecutionLimit; i++)
                deliveries.Add(service.HandleOwnedCommandAsync(first, Request() with { WorkingDirectory = "Q:\\fixture" }, CancellationToken.None));
            var overflow = service.HandleOwnedCommandAsync(first, Request() with { WorkingDirectory = "Q:\\fixture" }, CancellationToken.None);
            deliveries.Add(overflow);
            Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await overflow.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            var handles = (await service.ListAsync()).Select(snapshot => snapshot.Handle).ToArray();
            cancellation.Cancel();
            foreach (var handle in handles)
                Assert.IsFalse(await service.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce));
            await service.CloseOwnedAdmissionAsync();
            await Task.WhenAll(deliveries).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, (await service.ListAsync()).Count);
            Assert.IsNull(await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None));
            // Closing owned admission must not dispose or change trusted TUI registration.
            var tui = await service.RegisterAsync("session", Request(), true, CancellationToken.None);
            Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await tui.Completion).Kind);
        }
        finally
        {
            await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, [cancellation]);
        }
    }

    [TestMethod]
    public async Task OwnedReview_WindowIsBoundedSelectedAndExcludesTrustedRegistrations()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        try
        {
            var trusted = await service.RegisterAsync("session", Request(), false, CancellationToken.None);
            deliveries.Add(trusted.Completion);
            for (var i = 0; i < 2; i++)
            {
                var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
                Assert.IsNotNull(execution);
                Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
                for (var j = 0; j < 4; j++)
                    deliveries.Add(service.HandleOwnedCommandAsync(execution, Request() with { WorkingDirectory = "Q:\\fixture" }, CancellationToken.None));
            }
            Assert.AreEqual(0, (await service.ListOwnedCommandsAsync("other", default)).Entries.Count);
            var page = await service.ListOwnedCommandsAsync("session", default);
            Assert.AreEqual(4, page.Entries.Count);
            Assert.IsTrue(page.HasMore);
            Assert.IsFalse(page.Entries.Any(entry => entry.Handle.Attempt == trusted.Snapshot.Handle));
            foreach (var entry in page.Entries)
            {
                Assert.AreEqual(entry.Request.Handle, entry.Handle.Attempt);
                Assert.IsTrue(await service.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.Deny, default));
            }
            var next = await service.ListOwnedCommandsAsync("session", default);
            Assert.AreEqual(4, next.Entries.Count);
            Assert.IsFalse(next.HasMore);
            Assert.IsFalse(next.Entries.Any(entry => page.Entries.Contains(entry)));
            Assert.IsTrue(await service.IsPendingAsync(trusted.Snapshot.Handle));
            Assert.IsFalse(await service.ResolveOwnedCommandAsync(next.Entries[0].Handle with { Attempt = trusted.Snapshot.Handle }, AgentPermissionDecisionKind.AllowOnce, default));
            // The existing trusted TUI policy remains independent of the renderer route.
            Assert.IsTrue(await service.ResolveAsync(trusted.Snapshot.Handle, AgentPermissionDecisionKind.AllowForSession));
        }
        finally { await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, []); }
    }

    [TestMethod]
    public async Task OwnedReview_ExactBindingDecisionAndReplayAreMailboxAuthoritative()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        try
        {
            var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", CancellationToken.None);
            Assert.IsNotNull(execution);
            Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
            foreach (var runId in new AgentRunId?[] { null, new("run") })
            foreach (var decision in new[] { AgentPermissionDecisionKind.AllowOnce, AgentPermissionDecisionKind.Deny, AgentPermissionDecisionKind.Cancel })
            {
                var delivery = service.HandleOwnedCommandAsync(execution, Request() with { RunId = runId, WorkingDirectory = "Q:\\fixture" }, default);
                deliveries.Add(delivery);
                var handle = (await service.ListOwnedCommandsAsync("session", default)).Entries.Single().Handle;
                foreach (var wrong in new[] { handle with { OperationId = Guid.NewGuid() }, handle with { RuntimeInstanceId = Guid.NewGuid() },
                    handle with { AttachmentGeneration = handle.AttachmentGeneration + 1 }, handle with { Attempt = handle.Attempt with { SessionId = "other" } },
                    handle with { Attempt = handle.Attempt with { RunId = "wrong-run" } }, handle with { Attempt = handle.Attempt with { InteractionId = "other" } },
                    handle with { Attempt = handle.Attempt with { AttemptId = Guid.NewGuid() } } })
                    Assert.IsFalse(await service.ResolveOwnedCommandAsync(wrong, AgentPermissionDecisionKind.AllowOnce, default));
                Assert.IsFalse(await service.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowForSession, default));
                Assert.IsFalse(await service.ResolveOwnedCommandAsync(handle, (AgentPermissionDecisionKind)999, default));
                Assert.IsTrue(await service.ResolveOwnedCommandAsync(handle, decision, default));
                Assert.IsFalse(await service.ResolveOwnedCommandAsync(handle, decision, default));
                Assert.AreEqual(decision, (await delivery.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            }
            var closing = service.HandleOwnedCommandAsync(execution, Request() with { WorkingDirectory = "Q:\\fixture" }, default);
            deliveries.Add(closing);
            var stale = (await service.ListOwnedCommandsAsync("session", default)).Entries.Single().Handle;
            await service.InvalidateOwnedOperationAsync(execution.OperationId);
            Assert.IsFalse(await service.ResolveOwnedCommandAsync(stale, AgentPermissionDecisionKind.AllowOnce, default));
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await closing.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            Assert.AreEqual(0, (await service.ListOwnedCommandsAsync("session", default)).Entries.Count);
        }
        finally { await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, []); }
    }

    [TestMethod]
    public async Task OwnedReview_CancellationAndExecutionClosureInvalidateReturnedWindows()
    {
        var service = new SessionPermissionService();
        var forwarding = new OwnedProviderEventForwarding();
        var attachment = forwarding.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var deliveries = new List<Task<AgentPermissionDecision>>();
        var cancellation = new CancellationTokenSource();
        try
        {
            foreach (var identity in new[] { "", " padded ", "bad\ud800", "bad\n", new string('x', 129) })
                Assert.Throws<ArgumentException>(() => service.ListOwnedCommandsAsync(identity, default));
            var execution = await service.CreateOwnedExecutionAsync(Guid.NewGuid(), "session", cancellation.Token);
            Assert.IsNotNull(execution);
            Assert.IsTrue(await service.BindOwnedExecutionAsync(execution, Guid.NewGuid(), attachment, new ModelProviderId("test")));
            var delivery = service.HandleOwnedCommandAsync(execution, Request() with { WorkingDirectory = "Q:\\fixture" }, default);
            deliveries.Add(delivery);
            var handle = (await service.ListOwnedCommandsAsync("session", default)).Entries.Single().Handle;
            Assert.Throws<OperationCanceledException>(() => service.ListOwnedCommandsAsync("session", new CancellationToken(true)));
            Assert.Throws<OperationCanceledException>(() => service.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowOnce, new CancellationToken(true)));
            Assert.IsTrue(await service.IsPendingAsync(handle.Attempt)); // Canceling an RPC is not a permission Cancel.
            cancellation.Cancel();
            Assert.IsFalse(await service.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.AllowOnce, default));
            Assert.AreEqual(0, (await service.ListOwnedCommandsAsync("session", default)).Entries.Count);
            await service.CloseOwnedExecutionAsync(execution);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await delivery.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            Assert.IsFalse(await service.ResolveOwnedCommandAsync(handle, AgentPermissionDecisionKind.Cancel, default));
        }
        finally { await CloseOwnedFixtureAsync(service, forwarding, attachment, deliveries, [cancellation]); }
        Assert.AreEqual(0, (await service.ListOwnedCommandsAsync("session", default)).Entries.Count);
    }

    private static async Task CloseOwnedFixtureAsync(SessionPermissionService service, OwnedProviderEventForwarding forwarding,
        OwnedProviderEventForwarding.Attachment attachment, List<Task<AgentPermissionDecision>> deliveries, IReadOnlyList<CancellationTokenSource> sources)
    {
        var disposal = service.DisposeAsync().AsTask();
        var completion = Task.WhenAll(deliveries.Cast<Task>().Append(disposal));
        Task? retirement = null;
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = forwarding.RetireAsync(attachment);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var source in sources) source.Dispose();
        }
        catch (Exception error)
        {
            // Failed/timed-out cleanup is not permission to release sources or lose the original work.
            error.Data["RetainedOwnedPermissionFixture"] = new { service, forwarding, attachment, deliveries, sources, disposal, completion, retirement };
            throw;
        }
    }
}
