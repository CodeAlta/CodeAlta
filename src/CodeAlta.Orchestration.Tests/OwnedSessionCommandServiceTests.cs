using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Source-audit gated real-host fixtures. No discovery initializer, shared runtime, event reader,
/// environment substitution, provider probe, or test-only production route.
/// </summary>
[TestClass]
public sealed class OwnedSessionCommandServiceTests
{
    [TestMethod]
    public void PluginEnvironment_UsesExplicitSnapshotWithoutAmbientFallback()
    {
        var options = new CodeAltaHostOptions
        {
            PluginEnvironment = FrozenDictionary<string, string?>.Empty,
        };
        var operationOptions = CodeAltaHost.CreatePluginOperationOptions(
            options,
            new CatalogOptions { GlobalRoot = @"Q:\owned-global" },
            new ProjectDescriptor { Id = "literal-project", ProjectPath = @"Q:\owned-project" });
        Assert.AreEqual(0, operationOptions.Environment.Count);
        Assert.AreEqual("literal-project", operationOptions.ProjectId);
        Assert.AreEqual(@"Q:\owned-project", operationOptions.ProjectPath);
    }

    [TestMethod]
    public void PluginEnvironment_CopiesUsingCaseInsensitiveKeys()
    {
        var supplied = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MiXeD"] = "before",
            ["NullValue"] = null,
        };
        var operationOptions = CodeAltaHost.CreatePluginOperationOptions(
            new CodeAltaHostOptions { PluginEnvironment = supplied },
            new CatalogOptions { GlobalRoot = @"Q:\owned-global" },
            new ProjectDescriptor { Id = "literal-project", ProjectPath = @"Q:\owned-project" });
        supplied["MiXeD"] = "after";
        supplied["added"] = "later";
        Assert.AreNotSame(supplied, operationOptions.Environment);
        Assert.AreEqual(2, operationOptions.Environment.Count);
        Assert.AreEqual("before", operationOptions.Environment["mixed"]);
        Assert.IsTrue(operationOptions.Environment.TryGetValue("nullvalue", out var nullValue));
        Assert.IsNull(nullValue);
        Assert.IsFalse(operationOptions.Environment.ContainsKey("added"));
    }

    [TestMethod]
    public void BuiltInRoot_RejectsNonAbsolutePaths()
    {
        Assert.Throws<ArgumentNullException>(() => new BuiltInCodeAltaSkillRootProvider(null!));
        foreach (var path in new[] { "", " ", "relative", "../relative", "C:relative", "\\rooted" })
            Assert.Throws<ArgumentException>(() => new BuiltInCodeAltaSkillRootProvider(path));
    }

    [TestMethod]
    public async Task BuiltInRoot_ReturnsExplicitRootWithUnchangedIdentityAndPrecedence()
    {
        var root = Path.DirectorySeparatorChar == '\\' ? @"Q:\owned-skills" : "/owned-skills";
        var provider = new BuiltInCodeAltaSkillRootProvider(root);
        var roots = await provider.GetRootsAsync(new SkillDiscoveryContext());
        Assert.HasCount(1, roots);
        Assert.AreEqual(Path.GetFullPath(root), roots[0].RootPath);
        Assert.AreEqual("builtin:codealta", roots[0].SourceId);
        Assert.AreEqual(SkillSourceKind.Builtin, roots[0].SourceKind);
        Assert.AreEqual(SkillScopeKind.Builtin, roots[0].Scope);
        Assert.AreEqual(4, roots[0].Precedence);
    }

    [TestMethod]
    public void BuiltInRoot_ValidatesContextAndCancellationWithoutDiscovery()
    {
        var provider = new BuiltInCodeAltaSkillRootProvider();
        Assert.Throws<ArgumentNullException>(() => provider.GetRootsAsync(null!));
        Assert.Throws<OperationCanceledException>(() => provider.GetRootsAsync(new SkillDiscoveryContext(), new CancellationToken(true)));
    }

    [TestMethod]
    public Task AdmitSend_UsesRealHostRuntimeAndRegisteredSessionProvider() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(f.SessionId, f.Provider.LastSessionId);
        Assert.AreEqual(1, f.Provider.RuntimeStarts);
        Assert.AreEqual(1, f.Provider.Sends);
        Assert.AreEqual(f.ProjectRoot, f.Provider.Options!.WorkingDirectory);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(receipt.Completion)).Outcome);
    });

    [TestMethod]
    public Task AdmitSend_ReturnsReceiptBeforeProviderCompletion() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.IsFalse(receipt.Completion.IsCompleted);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task DurableHeader_ResumesDuringOwnedPreparation() => Fixture.RunAsync(async f =>
    {
        Assert.AreEqual(0, f.Provider.Resumes);
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Resumes);
        Assert.AreEqual(0, f.Provider.Creates);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task ResumeNotFound_UsesExistingStartFallback() => Fixture.RunAsync(async f =>
    {
        f.Provider.ResumeNotFound = true;
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Resumes);
        Assert.AreEqual(1, f.Provider.Creates);
        Assert.AreEqual(f.SessionId, f.Provider.LastSessionId);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task AdmissionCancellation_DoesNotCancelAcceptedExecution() => Fixture.RunAsync(async f =>
    {
        using var caller = new CancellationTokenSource();
        var request = new OwnedTextSendRequest("send", f.SessionId, "text");
        var receipt = f.Accept(f.AdmitSend(request, caller.Token));
        var cancellation = f.Track(caller.CancelAsync());
        await f.Observe(cancellation);
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.IsFalse(f.Provider.SendToken.IsCancellationRequested);
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(receipt.Completion)).Outcome);
        Assert.Throws<OperationCanceledException>(() => f.AdmitSend(request with { ClientRequestId = "cancelled" }, caller.Token));
    });

    [TestMethod]
    public Task Retry_ReturnsSameReceiptAndRejectsConflictingPayload() => Fixture.RunAsync(async f =>
    {
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("leading", " " + f.SessionId, "text")));
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("trailing", f.SessionId + " ", "text")));
        var receipt = f.Send();
        var retry = f.AdmitSend(new("send", f.SessionId.ToUpperInvariant(), "text"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, retry.Kind);
        Assert.AreSame(receipt, retry.Receipt);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(new("send", f.SessionId, "different")).Kind);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitAbort(new("send", receipt.OperationId)).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
        Assert.AreSame(receipt, f.AdmitSend(new("send", f.SessionId, "text")).Receipt);
    });

    [TestMethod]
    public Task ReceiptCapacity_RejectsWithoutEvictingRetryProtection() => Fixture.RunAsync(async f =>
    {
        var receipt = f.Send();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Capacity, f.AdmitSend(new("other", f.SessionId, "text")).Kind);
        Assert.AreSame(receipt, f.AdmitSend(new("send", f.SessionId, "text")).Receipt);
    }, capacity: 1);

    [TestMethod]
    public Task ConcurrentAdmission_ReservesBeforeLookupAndAllowsOneActiveSend() => Fixture.RunAsync(async f =>
    {
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("alias", "\t" + f.SessionId + "\n", "text")));
        var launch = f.NewGate();
        async Task<OwnedSessionCommandAdmission> Admit(string id)
        {
            await launch.Task.ConfigureAwait(false);
            return f.AdmitSend(new(id, f.SessionId, "text"));
        }
        var first = f.Track(Admit("first"));
        var second = f.Track(Admit("second"));
        launch.TrySetResult();
        var a = await f.Observe(first);
        var b = await f.Observe(second);
        var accepted = a.Kind == OwnedSessionCommandAdmissionKind.Accepted ? a : b;
        var rejected = ReferenceEquals(accepted, a) ? b : a;
        var receipt = f.Accept(accepted);
        Assert.Throws<ArgumentException>(() => f.AdmitSend(new("active-alias", f.SessionId + " ", "text")));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, rejected.Kind);
        // The source fixture, not this provider barrier, proves reservation precedes lookup.
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(1, f.Provider.Sends);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task CapturedRequest_DoesNotExposeMutableDescriptorOrOptions() => Fixture.RunAsync(async f =>
    {
        var request = new OwnedTextSendRequest("send", f.SessionId, "captured");
        var receipt = f.Accept(f.AdmitSend(request));
        request = request with { Text = "replacement", SessionId = "different" };
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        Assert.AreEqual(f.SessionId, receipt.SessionId);
        Assert.AreEqual("captured", f.Provider.Input!.Items.OfType<AgentInputItem.Text>().Single().Value);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict, f.AdmitSend(request).Kind);
        Assert.AreEqual("fixture-model", f.Provider.Options!.Model);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(receipt.Completion);
    });

    [TestMethod]
    public Task Abort_BeforeExecutionDoesNotStartProviderWork() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        var abort = f.Abort(send);
        f.Provider.ReleasePreparation.TrySetResult();
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(send.Completion)).Outcome);
        Assert.AreEqual(0, f.Provider.Sends);
    }, holdPreparation: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Abort_DuringCreationWaitsForAttachmentAndCallsActualAbort(bool reviewPermissions) => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        var abort = f.Abort(send);
        Assert.IsFalse(abort.Completion.IsCompleted);
        Assert.AreEqual(0, f.Provider.Aborts);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.AreEqual(1, f.Provider.Aborts);
        Assert.AreEqual(0, f.Provider.Sends);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(send.Completion);
    }, holdPreparation: true, reviewPermissions: reviewPermissions);

    [TestMethod]
    public Task Abort_DuringSendUsesReservedRuntimeRoute() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var abort = f.Abort(send);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.IsFalse(send.Completion.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, f.AdmitSend(new("next", f.SessionId, "text")).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, (await f.Observe(send.Completion)).Outcome);
    });

    [TestMethod]
    public Task Abort_RetriesShareControlAndCannotReachNextSend() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var abort = f.Abort(send);
        var alias = f.Abort(send, "alias");
        Assert.AreSame(abort, f.AdmitAbort(new("abort", send.OperationId)).Receipt);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort");
        Assert.AreEqual(1, f.Provider.Aborts);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(alias.Completion);
        await f.Observe(send.Completion);
        var next = f.Send("next");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, next, "second send");
        var late = f.Abort(send, "late");
        Assert.AreEqual("already_terminal", (await f.Observe(late.Completion)).Code);
        Assert.AreEqual(1, f.Provider.Aborts);
        f.Provider.ReleaseSecondSend.TrySetResult();
        await f.Observe(next.Completion);
    });

    [TestMethod]
    public Task PreparationFailure_DoesNotCallMissingEntryAbort() => Fixture.RunAsync(async f =>
    {
        f.Provider.FailPreparation = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        var abort = f.Abort(send);
        f.Provider.ReleasePreparation.TrySetResult();
        Assert.AreEqual("not_attached", (await f.Observe(abort.Completion)).Code);
        Assert.AreEqual("preparation_failed", (await f.Observe(send.Completion)).Code);
        Assert.AreEqual(0, f.Provider.Aborts);
    }, holdPreparation: true);

    [TestMethod]
    public Task Dispose_JoinsPreparationSendAndControlBeforeDependencies() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.PreparationStarted.Task, send, "preparation");
        var disposal = f.BeginDisposal();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Closed, f.AdmitSend(new("closed", f.SessionId, "text")).Kind);
        Assert.AreEqual(0, f.Provider.RuntimeDisposals);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, send, "disposal abort");
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(0, f.Provider.RuntimeDisposals);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
        Assert.AreEqual(1, f.Provider.RuntimeDisposals);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    }, capacity: 1, holdPreparation: true);

    [TestMethod]
    public Task ProviderFault_IsObservedAndRetainedInReceipt() => Fixture.RunAsync(async f =>
    {
        f.Provider.FailSend = true;
        var receipt = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, receipt, "send");
        f.Provider.ReleaseSend.TrySetResult();
        var result = await f.Observe(receipt.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("send_failed", result.Code);
        Assert.AreSame(receipt, f.AdmitSend(new("send", f.SessionId, "text")).Receipt);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Interactions_DenyPermissionAndCancelUserInput(bool reviewPermissions) => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestInteractions = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PermissionDecision);
        Assert.IsTrue(f.Provider.InputCancelled);
        Assert.IsTrue(f.Provider.Options!.Tools is null || f.Provider.Options.Tools.Count == 0);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    }, reviewPermissions: reviewPermissions);

    [TestMethod]
    public Task OwnedPermission_DefaultAndPreparationRemainDenied() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        Assert.IsNull(f.Provider.FirstSendOptions!.OnPermissionRequest);
        var denied = f.Permission(f.Provider.Options!.OnPermissionRequest);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(denied)).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    });

    [TestMethod]
    public Task OwnedPermission_OptInUsesActualPerSendCallbackWithNullRunAndNoneToken() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPreparationPermission = true;
        f.Provider.RequestPerSendPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, f.Provider.PreparationDecision);
        Assert.IsNotNull(f.Provider.FirstSendOptions!.OnPermissionRequest);
        var pending = f.Track(f.Provider.SendPermission!);
        var handle = await f.PendingHandle();
        Assert.IsNull(handle.RunId);
        Assert.AreEqual(f.SessionId, handle.SessionId);
        Assert.IsFalse(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(handle, AgentPermissionDecisionKind.AllowForSession).AsTask()));
        Assert.IsTrue(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce).AsTask()));
        // Decision is inert data: this fake never executes a tool, shell or model turn.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(f.Provider.Options!.OnPermissionRequest))).Kind);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_PublicOwnedReviewUsesActualReceiptRuntimeAndAttachment() => Fixture.RunAsync(async f =>
    {
        f.Provider.RequestPerSendPermission = true;
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Track(f.Provider.SendPermission!);
        var permissions = f.Host.RuntimeService.Permissions;
        var page = await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask());
        var current = await f.Observe(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId, CancellationToken.None));
        Assert.HasCount(1, page.Entries);
        Assert.IsFalse(page.HasMore);
        Assert.IsNotNull(current.Entry);
        var entry = page.Entries.Single();
        Assert.AreEqual(send.OperationId, entry.Handle.OperationId);
        Assert.AreEqual(current.RuntimeInstanceId, entry.Handle.RuntimeInstanceId);
        Assert.AreNotEqual(Guid.Empty, entry.Handle.RuntimeInstanceId);
        Assert.AreEqual(current.Entry.AttachmentGeneration, entry.Handle.AttachmentGeneration);
        Assert.IsTrue(entry.Handle.AttachmentGeneration > 0);
        Assert.AreEqual(f.SessionId, entry.Handle.Attempt.SessionId);
        Assert.IsNull(entry.Handle.Attempt.RunId); // Do not invent a run from the separate current-runtime observation.
        Assert.AreEqual("owned-permission", entry.Handle.Attempt.InteractionId);
        Assert.AreNotEqual(Guid.Empty, entry.Handle.Attempt.AttemptId);
        Assert.AreEqual(entry.Handle.Attempt, entry.Request.Handle);
        Assert.AreEqual(f.Provider.Descriptor.ProviderId, entry.Request.ProviderId);
        Assert.AreEqual("commandExecution", entry.Request.Kind);
        Assert.AreEqual("inert fixture command", entry.Request.Command);
        Assert.AreEqual(f.ProjectRoot, entry.Request.WorkingDirectory);
        Assert.AreEqual("fixture", entry.Request.Reason);
        Assert.IsNull(entry.Request.GrantRoot);
        Assert.IsFalse(pending.IsCompleted);
        Assert.IsTrue(await f.Observe(permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        // AllowOnce is inert decision data here: the unchanged fake cannot execute a tool or model turn.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await f.Observe(pending)).Kind);
        Assert.IsFalse(await f.Observe(permissions.ResolveOwnedCommandAsync(entry.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None).AsTask()));
        f.Provider.ReleaseSend.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Observe(send.Completion)).Outcome);
        Assert.HasCount(0, (await f.Observe(permissions.ListOwnedCommandsAsync(f.SessionId, CancellationToken.None).AsTask())).Entries);
    }, reviewPermissions: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_SendReturnCancelsPendingAndOldDelegateCannotJoinReusedCoordinator(bool failSend) => Fixture.RunAsync(async f =>
    {
        f.Provider.FailSend = failSend;
        var first = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, first, "first send");
        var old = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(old);
        var oldHandle = await f.PendingHandle();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(first.Completion);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        f.Provider.FailSend = false;
        var second = f.Send("second");
        await f.ObserveReadiness(f.Provider.SecondSendStarted.Task, second, "second send");
        Assert.AreEqual(1, f.Provider.Resumes, "The actual coordinator must be reused.");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(old))).Kind);
        var current = f.Permission(f.Provider.SecondSendOptions!.OnPermissionRequest!);
        var handle = await f.PendingHandle();
        Assert.AreNotEqual(oldHandle.AttemptId, handle.AttemptId);
        Assert.IsFalse(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(oldHandle, AgentPermissionDecisionKind.AllowOnce).AsTask()));
        await f.Observe(f.Host.RuntimeService.Permissions.CancelAsync(handle).AsTask());
        await f.Observe(current);
        f.Provider.ReleaseSecondSend.TrySetResult();
        await f.Observe(second.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_AbortStartsWhileProviderCancellationCallbackIsHeld() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var entered = f.NewGate();
        var release = f.NewGate();
        var registration = f.Provider.SendToken.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Held provider cancellation expired.");
        });
        try
        {
            var abort = f.Abort(send);
            await f.Observe(entered.Task);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
            await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "independent abort");
            release.TrySetResult();
            f.Provider.ReleaseAbort.TrySetResult();
            f.Provider.ReleaseSend.TrySetResult();
            await f.Observe(abort.Completion);
            await f.Observe(send.Completion);
        }
        finally { release.TrySetResult(); registration.Dispose(); }
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_AbortCancelsBeforeDependentProviderJoin() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var abort = f.Abort(send);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        await f.ObserveReadiness(f.Provider.AbortStarted.Task, abort, "abort after permission");
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(abort.Completion);
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_DetachCancelsBeforeRetirementJoins() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var callback = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(callback);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var detach = f.Track(f.Host.RuntimeService.DetachRuntimeSessionAsync(f.SessionId));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(callback))).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(detach);
        await f.Observe(send.Completion);
    }, reviewPermissions: true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_ShutdownCancelsBeforeDependentJoins(bool directRuntime) => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var callback = f.Provider.FirstSendOptions!.OnPermissionRequest!;
        var pending = f.Permission(callback);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var disposal = directRuntime ? f.Track(f.Host.RuntimeService.DisposeAsync().AsTask()) : f.BeginDisposal();
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Observe(f.Permission(callback))).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
    }, reviewPermissions: true);

    [TestMethod]
    public Task OwnedPermission_CommandShutdownDoesNotDisposeTrustedTuiPermissions() => Fixture.RunAsync(async f =>
    {
        var send = f.Send();
        await f.ObserveReadiness(f.Provider.SendStarted.Task, send, "send");
        var pending = f.Permission(f.Provider.FirstSendOptions!.OnPermissionRequest!);
        await f.PendingHandle();
        f.Provider.AbortDependency = pending;
        var disposal = f.Track(f.Host.Commands.DisposeAsync().AsTask());
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Observe(pending)).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        f.Provider.ReleaseSend.TrySetResult();
        await f.Observe(send.Completion);
        await f.Observe(disposal);
        var tui = await f.Observe(f.Host.RuntimeService.Permissions.RegisterAsync(f.SessionId, f.Provider.CommandRequest(), false, CancellationToken.None));
        _ = f.Track(tui.Completion);
        Assert.IsTrue(await f.Observe(f.Host.RuntimeService.Permissions.ResolveAsync(tui.Snapshot.Handle, AgentPermissionDecisionKind.AllowForSession).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.AllowForSession, (await f.Observe(tui.Completion)).Kind);
    }, reviewPermissions: true);

    // Constructed only inside a selected real-route test. All gates and tasks are instance-owned.
    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<Task> _observers = [];
        private readonly List<Exception> _observedFaults = [];
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Exception> _failures = [];
        private readonly List<Task> _cleanupWaits = [];
        private readonly List<Task<bool>> _cleanupWorkers = [];
        private Task? _setup;
        private Task? _body;
        private Task? _disposal;
        private volatile CodeAltaHost? _host;
        private volatile bool _cleaning;
        private bool _ownsRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-" + Guid.NewGuid().ToString("N"));
        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal string ProjectRoot => Path.Combine(_root, "project");
        internal ControlledProvider Provider { get; } = new();
        internal CodeAltaHost Host => _host ?? throw new InvalidOperationException("Host setup has not completed.");

        internal static async Task RunAsync(Func<Fixture, Task> body, int capacity = 256, bool holdPreparation = false, bool reviewPermissions = false)
        {
            var fixture = new Fixture();
            try
            {
                fixture._setup = fixture.Track(fixture.SetupAsync(capacity, holdPreparation, reviewPermissions));
                await fixture.Observe(fixture._setup);
                var launch = fixture.NewGate();
                fixture._body = fixture.Track(fixture.RunBodyAsync(body, launch.Task));
                launch.TrySetResult();
                await fixture.Observe(fixture._body);
            }
            catch (Exception ex)
            {
                fixture._failures.Add(ex);
            }
            finally
            {
                await fixture.CleanupAsync();
            }
            if (fixture._failures.Count > 0)
            {
                var failure = new AggregateException("Owned fixture failures; roots retained at " + fixture._root, fixture._failures);
                failure.Data["RetainedFixture"] = fixture;
                throw failure;
            }
        }

        private async Task RunBodyAsync(Func<Fixture, Task> body, Task launch)
        {
            await launch.ConfigureAwait(false);
            await Track(body(this)).ConfigureAwait(false);
        }

        private async Task SetupAsync(int capacity, bool holdPreparation, bool reviewPermissions)
        {
            // Parent must admit these runtime I/O routes only after auditing this complete fixture.
            // Check existing ancestry before side effects; this is not a reparse-race sandbox.
            RejectReparseAncestors(Path.GetDirectoryName(_root)!);
            if (Directory.Exists(_root) || File.Exists(_root))
                throw new InvalidOperationException("The new fixture root already exists.");
            Directory.CreateDirectory(_root);
            _ownsRoot = true;
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var directory in new[] { home, global, ProjectRoot, builtin }) Directory.CreateDirectory(directory);
            // Only builtin exists as a skill root; other configured skill directories remain absent.
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            var skill = Path.Combine(builtin, "fixture-builtin");
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: fixture-builtin\ndescription: Owned fixture skill.\n---\nFixture text only.\n");
            var catalogOptions = new CatalogOptions { GlobalRoot = global };
            var projectTask = Track(new ProjectCatalog(catalogOptions).UpsertFromPathAsync(ProjectRoot, CancellationToken.None));
            var project = await projectTask.ConfigureAwait(false);
            var session = new SessionViewDescriptor
            {
                SessionId = SessionId,
                Kind = SessionViewKind.ProjectSession,
                ProjectRef = project.Id,
                ProviderId = Provider.Descriptor.ProviderId.Value,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = ProjectRoot,
                Title = "Owned fixture",
                ModelId = "fixture-model",
                AgentPromptId = "default",
                CreatedAt = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
            };
            var journal = new SessionViewJournalStore(catalogOptions);
            var header = Track(journal.EnsureHeaderAsync(session, CancellationToken.None));
            await header.ConfigureAwait(false);
            // Header/state cache writes only update rows; a genuine summary creates the row.
            var store = journal.CreateSessionStore();
            var summary = Track(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId,
                ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = session.WorkingDirectory,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await summary.ConfigureAwait(false);
            var state = Track(journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await state.ConfigureAwait(false);
            var readback = Track(store.GetSessionAsync(SessionId, CancellationToken.None));
            var metadata = await readback.ConfigureAwait(false);
            Assert.IsNotNull(metadata);
            Assert.AreEqual(session.SessionId, metadata.SessionId);
            Assert.AreEqual(session.WorkingDirectory, metadata.WorkspacePath);
            Assert.AreEqual(session.CreatedAt, metadata.CreatedAt);
            Assert.AreEqual(session.ProviderKey, metadata.ProviderKey);
            Assert.AreEqual(session.ModelId, metadata.ModelId);
            Assert.AreEqual(session.ReasoningEffort, metadata.ReasoningEffort);
            Assert.AreEqual(session.AgentPromptId, metadata.AgentPromptId);
            Assert.IsNotNull(metadata.ViewState);
            Assert.AreEqual(session.ProviderKey, metadata.ViewState.ProviderKey);
            Assert.AreEqual(session.ModelId, metadata.ViewState.ModelId);
            Assert.AreEqual(session.ReasoningEffort, metadata.ViewState.ReasoningEffort);
            Assert.AreEqual(session.AgentPromptId, metadata.ViewState.AgentPromptId);
            if (!holdPreparation) Provider.ReleasePreparation.TrySetResult();
            var creation = Track(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global,
                CurrentProjectPath = ProjectRoot,
                DiscoveryScope = new SessionDiscoveryScope(home, _root),
                BuiltInSkillRoot = builtin,
                OwnedCommandReceiptCapacity = capacity,
                ReviewOwnedCommandPermissions = reviewPermissions,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false,
                OwnsLogging = false,
                IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime),
            }, CancellationToken.None));
            _host = await creation.ConfigureAwait(false);
            // A setup observer may already have timed out. Never publish an unowned late host.
            if (_cleaning) await BeginDisposal().ConfigureAwait(false);
        }

        private static void RejectReparseAncestors(string root)
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture ancestry contains a reparse node.");
        }

        internal OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken = default)
            => RetainAdmission(Host.Commands.AdmitSend(request, cancellationToken));

        internal OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request)
            => RetainAdmission(Host.Commands.AdmitAbort(request));

        private OwnedSessionCommandAdmission RetainAdmission(OwnedSessionCommandAdmission admission)
        {
            // Retain even an unexpectedly accepted receipt before any rejection/replay assertion.
            if (admission.Receipt is not null) Track(admission.Receipt.Completion);
            return admission;
        }

        internal OwnedSessionCommandReceipt Send(string id = "send") => Accept(AdmitSend(new(id, SessionId, "text")));
        internal Task<AgentPermissionDecision> Permission(AgentPermissionRequestHandler handler)
            => Track(handler(Provider.CommandRequest(), CancellationToken.None));

        internal async Task<SessionPermissionHandle> PendingHandle()
        {
            var pending = await Observe(Host.RuntimeService.Permissions.ListAsync().AsTask());
            return pending.Single().Handle;
        }
        internal OwnedSessionCommandReceipt Abort(OwnedSessionCommandReceipt send, string id = "abort")
            => Accept(AdmitAbort(new(id, send.OperationId)));

        internal OwnedSessionCommandReceipt Accept(OwnedSessionCommandAdmission admission)
        {
            if (admission.Receipt is not null) Track(admission.Receipt.Completion);
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            Assert.IsNotNull(admission.Receipt);
            return admission.Receipt;
        }

        internal TaskCompletionSource NewGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _gates.Add(gate);
                if (_cleaning) gate.TrySetResult();
            }
            return gate;
        }

        internal Task Track(Task task)
        {
            lock (_gate)
            {
                _tasks.Add(task);
                _observers.Add(ObserveFaultAsync(task));
            }
            return task;
        }

        internal Task<T> Track<T>(Task<T> task)
        {
            Track((Task)task);
            return task;
        }

        private async Task ObserveFaultAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _observedFaults.Add(ex); }
        }

        internal Task Observe(Task task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Observe<T>(Task<T> task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));

        internal Task ObserveReadiness(Task readiness, OwnedSessionCommandReceipt receipt, string stage)
            => Track(ObserveReadinessAsync(readiness, receipt, stage));

        private async Task ObserveReadinessAsync(Task readiness, OwnedSessionCommandReceipt receipt, string stage)
        {
            // Notifications are not executable work: failed preparation may never signal them.
            // Retain the actual race and bounded observer, but not the bare notification.
            var completion = Track(receipt.Completion);
            var race = Track(Task.WhenAny(readiness, completion));
            var wait = Track(race.WaitAsync(TimeSpan.FromSeconds(5)));
            try { await wait.ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException($"Provider readiness timed out at {stage}."); }
            if (readiness.IsCompletedSuccessfully) return;
            // Receipt contracts expose stable results, never raw owner exception details.
            Assert.IsTrue(completion.IsCompletedSuccessfully, $"No successful readiness or receipt result at {stage}.");
            var result = await completion.ConfigureAwait(false);
            Assert.Fail($"Receipt completed before provider readiness at {stage}: Outcome={result.Outcome}; Code={result.Code}.");
        }

        internal Task BeginDisposal()
        {
            lock (_gate) return _disposal ??= Track(Host.DisposeAsync().AsTask());
        }

        private async Task CleanupAsync()
        {
            _cleaning = true;
            Provider.ReleaseAll();
            lock (_gate) foreach (var gate in _gates) gate.TrySetResult();
            var confirmed = true;
            if (_setup is not null) confirmed &= await DrainBatchAsync([_setup]).ConfigureAwait(false);
            var finalWork = new List<Task>();
            if (_host is not null) finalWork.Add(BeginDisposal());
            if (_body is not null) finalWork.Add(_body);
            confirmed &= await DrainBatchAsync(finalWork).ConfigureAwait(false);
            Task[] tasks;
            lock (_gate) tasks = [.. _tasks, .. _observers];
            confirmed &= await DrainBatchAsync(tasks).ConfigureAwait(false);
            lock (_gate)
                foreach (var fault in _observedFaults)
                    if (!_failures.Contains(fault)) _failures.Add(fault);
            if (confirmed && _failures.Count == 0 && _ownsRoot)
            {
                try
                {
                    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                }
                catch (Exception ex) { _failures.Add(ex); }
            }
        }

        private async Task<bool> DrainBatchAsync(IEnumerable<Task> tasks)
        {
            // Start independent observations together; cleanup does not grow _observers.
            var workers = tasks.Distinct().Select(DrainAsync).ToArray();
            _cleanupWorkers.AddRange(workers);
            var confirmed = true;
            foreach (var worker in workers) confirmed &= await worker.ConfigureAwait(false);
            return confirmed;
        }

        private async Task<bool> DrainAsync(Task task)
        {
            // Each task has its own timeout, not one already-expired shared cleanup token.
            var observer = task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (_gate) _cleanupWaits.Add(observer);
            try { await observer.ConfigureAwait(false); return true; }
            catch (Exception ex)
            {
                lock (_gate) _failures.Add(ex);
                // A faulted disposal does not prove that dependencies terminated all their work.
                return false;
            }
        }
    }

    private sealed class ControlledProvider
    {
        private int _runtimeStarts, _resumes, _creates, _sends, _aborts, _runtimeDisposals, _earlyDisposals, _active;
        internal bool ResumeNotFound { get; set; }
        internal bool FailPreparation { get; set; }
        internal bool FailSend { get; set; }
        internal bool RequestInteractions { get; set; }
        internal bool RequestPreparationPermission { get; set; }
        internal bool RequestPerSendPermission { get; set; }
        internal Task<AgentPermissionDecision>? SendPermission { get; private set; }
        internal AgentPermissionDecisionKind? PreparationDecision { get; private set; }
        internal AgentSendOptions? FirstSendOptions { get; private set; }
        internal AgentSendOptions? SecondSendOptions { get; private set; }
        internal Task? AbortDependency { get; set; }
        internal AgentCommandPermissionRequest CommandRequest() => new(Descriptor.ProviderId, LastSessionId!, DateTimeOffset.UtcNow,
            null, "owned-permission", null, "inert fixture command", Options!.WorkingDirectory, null, "fixture", null, null, null);
        internal TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePreparation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecondSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAbort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RuntimeStarts => Volatile.Read(ref _runtimeStarts);
        internal int Resumes => Volatile.Read(ref _resumes);
        internal int Creates => Volatile.Read(ref _creates);
        internal int Sends => Volatile.Read(ref _sends);
        internal int Aborts => Volatile.Read(ref _aborts);
        internal int RuntimeDisposals => Volatile.Read(ref _runtimeDisposals);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal string? LastSessionId { get; private set; }
        internal AgentSessionCreateOptions? Options { get; private set; }
        internal AgentInput? Input { get; private set; }
        internal CancellationToken SendToken { get; private set; }
        internal AgentPermissionDecisionKind? PermissionDecision { get; private set; }
        internal bool InputCancelled { get; private set; }
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("owned-fixture"), "Owned fixture") { DefaultModelId = "fixture-model" };
        public Task StartAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _runtimeStarts); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected provider probe.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Unexpected turn-executor route.");
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _creates);
            return PrepareAsync(options.SessionId ?? throw new InvalidOperationException("Missing preserved session ID."), options);
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resumes);
            if (ResumeNotFound) throw new KeyNotFoundException("Controlled resume miss.");
            return PrepareAsync(sessionId, options);
        }

        private async Task<IAgentSession> PrepareAsync(string sessionId, AgentSessionCreateOptions options)
        {
            Interlocked.Increment(ref _active);
            try
            {
                LastSessionId = sessionId;
                Options = options;
                if (RequestPreparationPermission)
                    PreparationDecision = (await options.OnPermissionRequest(CommandRequest(), CancellationToken.None).ConfigureAwait(false)).Kind;
                PreparationStarted.TrySetResult();
                await ReleasePreparation.Task.ConfigureAwait(false);
                if (FailPreparation) throw new InvalidOperationException("Controlled preparation failure.");
                return new Session(this, sessionId);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _runtimeDisposals);
            if (Volatile.Read(ref _active) != 0) Interlocked.Increment(ref _earlyDisposals);
            return ValueTask.CompletedTask;
        }

        internal void ReleaseAll()
        {
            ReleasePreparation.TrySetResult();
            ReleaseSend.TrySetResult();
            ReleaseSecondSend.TrySetResult();
            ReleaseAbort.TrySetResult();
        }

        // A fresh runtime for every real registry factory call, including resume-fallback creation.
        private sealed class Runtime(ControlledProvider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => owner.StartAsync(cancellationToken);
            public Task StopAsync(CancellationToken cancellationToken = default) => owner.StopAsync(cancellationToken);
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => owner.ProbeAsync(cancellationToken);
            public IModelProviderTurnExecutor CreateTurnExecutor() => owner.CreateTurnExecutor();
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
                => owner.CreateSessionAsync(options, cancellationToken);
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
                => owner.ResumeSessionAsync(sessionId, options, cancellationToken);
            public ValueTask DisposeAsync() => owner.DisposeAsync();
        }

        private sealed class Session(ControlledProvider owner, string sessionId) : IAgentSession
        {
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => sessionId;
            public string? WorkspacePath => owner.Options?.WorkingDirectory;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }
            public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
            public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._active);
                try
                {
                    var send = Interlocked.Increment(ref owner._sends);
                    owner.Input = options.Input;
                    owner.SendToken = cancellationToken;
                    if (send == 1) owner.FirstSendOptions = options;
                    else owner.SecondSendOptions = options;
                    if (owner.RequestInteractions)
                    {
                        var permission = owner.Options!.OnPermissionRequest(new AgentGenericPermissionRequest(ProviderId, SessionId, DateTimeOffset.UtcNow, null, "permission", "fixture", default), cancellationToken);
                        owner.PermissionDecision = (await permission.ConfigureAwait(false)).Kind;
                        var input = owner.Options.OnUserInputRequest!(new AgentUserInputRequest(ProviderId, SessionId, DateTimeOffset.UtcNow, null, "input", new AgentUserInputForm([])), cancellationToken);
                        try { await input.ConfigureAwait(false); }
                        catch (OperationCanceledException) { owner.InputCancelled = true; }
                    }
                    if (owner.RequestPerSendPermission)
                        owner.SendPermission = (options.OnPermissionRequest ?? owner.Options!.OnPermissionRequest)(owner.CommandRequest(), CancellationToken.None);
                    (send == 1 ? owner.SendStarted : owner.SecondSendStarted).TrySetResult();
                    if (owner.SendPermission is { } sendPermission)
                        owner.PermissionDecision = (await sendPermission.ConfigureAwait(false)).Kind;
                    await (send == 1 ? owner.ReleaseSend.Task : owner.ReleaseSecondSend.Task).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (owner.FailSend) throw new InvalidOperationException("Controlled provider fault.");
                    return new AgentRunId("owned-run-" + send);
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public async Task AbortAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._active);
                try
                {
                    Interlocked.Increment(ref owner._aborts);
                    if (owner.AbortDependency is { } dependency) await dependency.ConfigureAwait(false);
                    owner.AbortStarted.TrySetResult();
                    await owner.ReleaseAbort.Task.ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected steer.");
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected compaction.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync()
            {
                if (Volatile.Read(ref owner._active) != 0) Interlocked.Increment(ref owner._earlyDisposals);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
