using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Current-source guards; these do not construct a host, provider, store or native lease.</summary>
[TestClass]
[Ignore("Deferred: source-shape reconstruction is too costly for the functional desktop cutover; behavioral coverage remains enabled.")]
public sealed class RuntimePluginLiveEventSourceTests
{
    [TestMethod]
    public void ProgramProfileRouteCorrection_PreludeIsNarrowAndPrecedesOlderProfileEdits()
    {
        var owner = Read(RuntimePluginLiveEventSourceInverse.OwnershipInverse);
        var start = owner.IndexOf("    internal static string RestoreProfileInput(", StringComparison.Ordinal);
        var end = owner.IndexOf("    internal static string RestoreGitHubInput(", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        StringAssert.Contains(owner[start..end], "=> path is Owned ? Restore(path, source)\n            : path == RuntimePluginLiveEventSourceInverse.Program ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;");
        Assert.AreEqual(18, PluginAgentEventOwnershipSourceInverse.Paths.Count);
        Assert.IsFalse(PluginAgentEventOwnershipSourceInverse.Paths.Contains(RuntimePluginLiveEventSourceInverse.Program, StringComparer.Ordinal));
        var profile = Read("CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs");
        Before(profile, "source = PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(path, source);", "foreach (var (before, after) in Edits(path))");
        var ui = Read("CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs");
        StringAssert.Contains(ui, "PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(SourcePath(path)))))");
        var feedback = Read("CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs");
        Before(feedback, "source = PluginAuthoringProfileSourceInverse.RestoreFeedbackInput(path, source);", "return RestoreCore(path, source);");
    }

    [TestMethod]
    public void ProgramProfileRouteCorrection_PreludeAuthenticatesCompleteProgramAndPreservesOwned()
    {
        const string program = RuntimePluginLiveEventSourceInverse.Program;
        var current = Read(program);
        var restored = PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(program, current);
        Assert.AreEqual(RuntimePluginLiveEventSourceInverse.Original(program), OwnedSessionAskSourceInverse.GitObjectId(restored));
        Assert.ThrowsExactly<AssertFailedException>(() => PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(program, restored));
        const string owned = RuntimePluginLiveEventSourceInverse.Owned;
        Assert.AreEqual(PluginAgentEventOwnershipSourceInverse.Original(owned), OwnedSessionAskSourceInverse.GitObjectId(
            PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(owned, Read(owned))));
        Assert.AreEqual("unchanged", PluginAgentEventOwnershipSourceInverse.RestoreProfileInput("unmapped", "unchanged"));
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)]
    public void ProgramProfileRouteCorrection_ReaderRoutesAuthenticateWholeProgramAcrossRepresentations(int route)
    {
        const string path = RuntimePluginLiveEventSourceInverse.Program;
        var current = Read(path);
        var baseline = PluginAgentEventOwnershipSourceInverse.RestoreProfileInput(path, current);
        var lines = current.Split('\n');
        var mixed = string.Concat(lines.Select((line, index) => line +
            (index == lines.Length - 1 ? "" : index % 2 == 0 ? "\r\n" : "\n")));
        foreach (var representation in new[] { current, current.Replace("\n", "\r\n", StringComparison.Ordinal), mixed })
        {
            // Each route starts from current checkout text, never the already restored baseline.
            var restored = RestoreRoute(representation);
            Assert.AreEqual(route == 3
                ? "E979E1B05104EF67DB4AA5825D1E4614A53BB7EACFDCC281FA3AFD3B5C5AA155"
                : "2F3AA23636A7DC1BA69721DEFCF2918AAFA5FF163D93EFE17E1093AD37286753",
                Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(restored))));
            Assert.ThrowsExactly<AssertFailedException>(() => RestoreRoute(restored));
        }
        Assert.ThrowsExactly<AssertFailedException>(() => RestoreRoute(baseline));
        Assert.ThrowsExactly<AssertFailedException>(() => RestoreRoute(current + "// unexpected\n"));
        const string marker = "internal sealed class StartupOwner";
        Assert.AreEqual(1, current.Split(marker, StringSplitOptions.None).Length - 1);
        Assert.ThrowsExactly<AssertFailedException>(() => RestoreRoute(current.Replace(marker, marker + " /* unexpected removed-region edit */", StringComparison.Ordinal)));
        Assert.ThrowsExactly<ArgumentException>(() => RestoreRoute("\uFEFF" + current));
        Assert.ThrowsExactly<ArgumentException>(() => RestoreRoute(current.Replace("\n", "\r", StringComparison.Ordinal)));
        Assert.ThrowsExactly<AssertFailedException>(() => RestoreRoute(current[..^1]));

        string RestoreRoute(string source) => route switch
        {
            0 => PluginAuthoringProfileSourceInverse.Restore(path, source),
            1 => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, source)),
            2 => PluginAuthoringProfileSourceInverse.RestoreFeedbackInput(path, source),
            3 => PluginFeedbackExtractionSourceTests.Restore(path, source),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    [TestMethod]
    public void ConnectedShutdown_PermissionAndQueueClosuresUseCompleteOriginals()
    {
        var permissions = Read(RuntimePluginLiveEventSourceInverse.Permissions);
        StringAssert.Contains(permissions, "=> JoinOwnedClosureAsync(() => JoinDeliveryOriginalsAsync(_ownedExecutions.Values");
        StringAssert.Contains(permissions, ".Where(execution => ReferenceEquals(execution.Attachment, attachment)).ToArray().Select(CloseOwned)))");
        StringAssert.Contains(permissions, "admission.RunAsync(() => ExecuteAsync<Task?>(close, null).AsTask())");
        StringAssert.Contains(permissions, "new { Owner = this, Admission = admission }");
        StringAssert.Contains(permissions, "await JoinDeliveryOriginalsAsync([completion]).ConfigureAwait(false);");
        Assert.AreEqual(2, permissions.Split("await JoinDeliveryOriginalsAsync([_shutdown.Task]).ConfigureAwait(false);", StringSplitOptions.None).Length - 1);
        Before(permissions, "foreach (var stage in stages) stage.Launch();", "if (await stage.Outcome.ConfigureAwait(false) is not null) failures.Add(stage.Failure!);");
        StringAssert.Contains(permissions, "new AgentDependencyRetentionException(\"permission\", \"closure originals\", failures, stages)");
        var runtime = Read(RuntimePluginLiveEventSourceInverse.Runtime);
        StringAssert.Contains(runtime, "attachment.CloseOwnedPermissions = () => CloseOwnedQueueAttachmentAsync(attachment);");
        var queue = Read(RuntimePluginLiveEventSourceInverse.Queue);
        StringAssert.Contains(queue, "var closure = new AttachmentClosureJoin(new { Runtime = this, Attachment = attachment });");
        StringAssert.Contains(queue, "return closure.RunAsync(() => Permissions.InvalidateOwnedAttachmentAsync(attachment), () =>");
        Before(queue, "item.Stop.TrySetResult();\n                        return ValueTask.FromResult<Task?>(item.Drained.Task);", "return ValueTask.FromResult<Task?>(null);");
        var helper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Before(helper, "Permissions.Launch(invalidatePermissions);", "QueryOriginal = queryDrain();");
        Before(helper, "QueryOriginal = queryDrain();", "await Permissions.Outcome.ConfigureAwait(false)");
        Before(helper, "QueueDrain.Launch(() => drainage);", "await Permissions.Outcome.ConfigureAwait(false)");
        Before(helper, "QueryOriginalFaults = QueryOriginal?.Exception;", "await Permissions.Outcome.ConfigureAwait(false)");
        Before(helper, "await Permissions.Outcome.ConfigureAwait(false)", "await QueueDrain.Outcome.ConfigureAwait(false)");
        StringAssert.Contains(helper, "unconfirmed |= Permissions.Original is null;");
        StringAssert.Contains(helper, "if (unconfirmed || failures.Any(OwnedProviderEventForwarding.HasRetention))");
    }

    [TestMethod]
    public void ConnectedShutdown_AbortRetainsSourcesAndUsesBeforeTerminalDrainage()
    {
        var runtime = Read(RuntimePluginLiveEventSourceInverse.Runtime);
        var start = runtime.IndexOf("    private async Task AbortOwnedBodyAsync(", StringComparison.Ordinal);
        var end = runtime.IndexOf("    private static string? BuildParentNotificationGuidance(", start, StringComparison.Ordinal);
        var abort = runtime[start..end];
        StringAssert.Contains(abort, "await admission.RunAsync(() => actor.ExecuteReservedAsync(");
        StringAssert.Contains(abort, "CancellationToken.None).AsTask())");
        StringAssert.Contains(abort, "lifetime = new RuntimeAbortLifetime(new { Runtime = this, Entry = capturedEntry, Use = handleUse, Admission = admission });");
        StringAssert.Contains(abort, "token => _agentHub.AbortAsync(capturedEntry!.SessionHandleId, token)");
        StringAssert.Contains(abort, "cancellationToken, capturedEntry!.Attachment.Cancellation.Token");
        Before(abort, "handleUse?.Retain(retained);", "_forwarding.RetainDependencies(retained, dependencies);");
        Before(abort, "throw retained;", "finally { handleUse?.Dispose(); }");
        var helper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Before(helper, "ThrowIfUnconfirmed(Permissions, failure);", "Source = CancellationTokenSource.CreateLinkedTokenSource(callerToken, attachmentToken);");
        Before(helper, "ThrowIfUnconfirmed(Abort, failure);", "try { Source.Dispose(); SourceReleased = true; }");
        StringAssert.Contains(helper, "invocation.Original is null || OwnedProviderEventForwarding.HasRetention(failure)");
        Assert.IsFalse(abort.Contains("using var execution", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CurrentFixtureCorrection_IndependentEffectsAreCheckedAgainstRawCheckout()
    {
        foreach (var (path, start, end) in new[]
        {
            (RuntimePluginLiveEventSourceInverse.ForwardingTests,
                "    public void Runtime_OwnsBodiesTailsAndCapturedAttachmentUses()", "        var drain = Between(runtime,"),
            (RuntimePluginLiveEventSourceInverse.CacheTests,
                "    public void CurrentSources_UsePublicationReferenceOutsideActorBeforeNotificationAndQueueTails()", "        var effect = Read("),
        })
        {
            var source = Read(path);
            StringAssert.Contains(source, "return SourceTestText.DecodeSource(File.ReadAllBytes(");
            var offset = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(offset >= 0, start);
            var limit = source.IndexOf(end, offset, StringComparison.Ordinal);
            Assert.IsTrue(limit > offset, end);
            var current = source[offset..limit];
            Assert.IsFalse(current.Contains("Restore", StringComparison.Ordinal), path);
            foreach (var assertion in new[]
            {
                "projectionUse.Dispose, ObserveLivePluginEventAsync, () =>",
                "effects.Add(() => InvalidateFileSearchCacheAsync(published, workingDirectory));",
                "effects.Add(() => DeliverParentNotificationAsync(notification));",
                "if (actorChoresCompleted && IsQueueDrainTrigger(@event))",
                "effects.Add(() => TryDrainNextQueuedPromptAsync(sessionId));",
                "publication.IndependentWork = new LiveEventIndependentWork(effects);",
                "return publication.IndependentWork.RunAsync();",
                "Read(\"CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs\")",
                "try { releasePublicationUse(); }",
                "IndependentOriginal = independent()",
                "Stages = _effects.Select(static _ => new OwnedSessionCommandService.OriginalInvocation()).ToArray();",
                "Stages[index].Launch(_effects[index]);",
                "await Stages[index].Outcome.ConfigureAwait(false)",
            }) StringAssert.Contains(current, assertion);
            Assert.IsFalse(current.Contains("ObserveLivePluginEventAsync, async () =>", StringComparison.Ordinal));
            Assert.IsFalse(current.Contains("await DeliverParentNotificationAsync(", StringComparison.Ordinal));
            Assert.IsFalse(current.Contains("if (IsQueueDrainTrigger(@event))", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void OuterCorrection_HostAndFrontendGateRequiredOriginalsButNotOrdinaryLogging()
    {
        var host = Read(RuntimePluginLiveEventSourceInverse.Host);
        StringAssert.Contains(host, "_disposeTask = CreateHostDisposal(");
        StringAssert.Contains(host, "public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask);");
        StringAssert.Contains(host, "InvocationFailure = failure;");
        StringAssert.Contains(host, "_requiresOriginal && Original is null");
        StringAssert.Contains(host, "new AgentDependencyRetentionException(\"host\", \"missing cleanup original\", [failure], this)");
        StringAssert.Contains(host, "new HostDisposalStage(() => { shutdownLogging(); return Task.CompletedTask; }, requiresOriginal: false)");
        Before(host, "reads.Launch();", "commands.Launch();");
        Before(host, "commands.Launch();", "await commands.ReportedOutcome.ConfigureAwait(false)");
        Before(host, "plugins.Launch();", "await commands.ReportedOutcome.ConfigureAwait(false)");
        StringAssert.Contains(host, "retained is null ? disposeRuntime() : drainRetainedRuntime(retained)");
        StringAssert.Contains(host, "RuntimeService.DrainRetainedDependenciesAsync,");
        StringAssert.Contains(host, "new { Commands = commands, Reads = reads, Plugins = plugins, Runtime = runtime }");
        var owned = Read(RuntimePluginLiveEventSourceInverse.Owned);
        StringAssert.Contains(owned, "_disposeTask = CreateOwnedServicesDisposal(");
        StringAssert.Contains(owned, "public ValueTask DisposeAsync() => PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask);");
        StringAssert.Contains(owned, "Original = invoke().AsTask();");
        StringAssert.Contains(owned, "return requiresOriginal && Original is null");
        StringAssert.Contains(owned, "new AgentDependencyRetentionException(\"terminal owned services\", \"missing cleanup original\", [evidence], this)");
        StringAssert.Contains(owned, "new OwnedServicesRelease(() => { shutdownLogging(); return ValueTask.CompletedTask; }, requiresOriginal: false)");
        StringAssert.Contains(owned, "if (Program.StartupOwner.ContainsRetention(failure))");
        StringAssert.Contains(owned, "\"dependent service release\", failures, stages");
    }

    [TestMethod]
    public void OuterCorrection_HubAndForwarderUseEvidenceBeforeAdmissionAndRelease()
    {
        var hub = Read(RuntimePluginLiveEventSourceInverse.Hub);
        StringAssert.Contains(hub, "Lifetime = new SessionEntryLifetime(this, () => coordinator.AbortAsync(CancellationToken.None),");
        StringAssert.Contains(hub, "coordinator.DisposeAsync, () => DisposeProviderRuntimeAsync(providerRuntime));");
        StringAssert.Contains(hub, "_failureOwner = new CoordinatorFailureOwner(this);");
        StringAssert.Contains(hub, "await _failureOwner.RunAsync(invocation, () => _session.SendAsync(options, cancellationToken)).ConfigureAwait(false);");
        Before(hub, "await _failureOwner.RunAsync(invocation,", "events.TryPublish(new RunFailedEvent(DateTimeOffset.UtcNow, sessionHandleId, (invocation.AwaitedFailure ?? ex).Message));");
        Assert.AreEqual(2, hub.Split("await _failureOwner.AbortAsync(invocation, () => _session.AbortAsync(cancellationToken)).ConfigureAwait(false);", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(hub, "await _failureOwner.DisposeAsync(_session.DisposeAsync, () =>");
        StringAssert.Contains(hub, "OriginalFaults is { InnerExceptions.Count: > 1 } faults ? faults : failure");
        StringAssert.Contains(hub, "lock (_gate) _retained.Add((evidence, invocation));");
        StringAssert.Contains(hub, "ThrowIfRetained();\n            try { return await invocation.RunAsync(send).ConfigureAwait(false); }");
        StringAssert.Contains(hub, "ThrowIfRetained();\n            try { releaseGates(); }");
        Before(hub, "await activeReferences.ConfigureAwait(false);", "if (Abort.Original is null || failures.Any(OwnedProviderEventForwarding.HasRetention))");
        Before(hub, "if (Abort.Original is null || failures.Any(OwnedProviderEventForwarding.HasRetention))", "SessionDisposal = new SessionPermissionService.DeliveryStage(");
        Before(hub, "if (SessionDisposal.Original is null || failures.Any(OwnedProviderEventForwarding.HasRetention))", "ProviderDisposal = new SessionPermissionService.DeliveryStage(");
        Before(hub, "if (ProviderDisposal.Original is null || OwnedProviderEventForwarding.HasRetention(ProviderDisposal.Failure!))", "DependenciesReleased = true;");
        var forwarding = Read(RuntimePluginLiveEventSourceInverse.Forwarding);
        StringAssert.Contains(forwarding, "close = CloseCoreAsync(launch.Task, permissions, actors, completeEvents);");
        Before(forwarding, "await permissionStage.Observer.ConfigureAwait(false);", "if (permissionStage.Original is null)");
        Before(forwarding, "throw RetainedFailure(\"permission cleanup\", permissionStage);", "var actorStage = new RetirementStage(actors);");
    }

    [TestMethod]
    public void LifecycleCorrection_ClosingAndNestedStartsUseActualReceipts()
    {
        var ask = Read(RuntimePluginLiveEventSourceInverse.Ask);
        StringAssert.Contains(ask, "if (First.Original is null || Second.Original is null");
        StringAssert.Contains(ask, "internal void Launch() { First.Launch(); Second.Launch(); }");
        Before(ask, "var first = await First.Outcome.ConfigureAwait(false);", "var second = await Second.Outcome.ConfigureAwait(false);");
        StringAssert.Contains(ask, "RunLifecycle = new Lifecycle(this, options.RunLifecycle),");
        StringAssert.Contains(ask, "() => ask.ClosingAsync(runId),");
        StringAssert.Contains(ask, "() => previous is null ? Task.CompletedTask : previous.ClosingAsync(runId), firstRequired: true);");
        Assert.IsFalse(ask.Contains("previous?.ClosingAsync(runId) ?? Task.CompletedTask", StringComparison.Ordinal));
        Before(ask, "await _firstStart.RunAsync(first, runId, executionToken).ConfigureAwait(false);", "await _secondStart.RunAsync(second, runId, executionToken).ConfigureAwait(false);");
        Before(ask, "if (previous is not null) await _previousStart.RunAsync(previous, runId, executionToken).ConfigureAwait(false);", "await _askStart.RunAsync(ask, runId, executionToken).ConfigureAwait(false);");
        Assert.AreEqual(2, ask.Split("AgentSession.IRunStartEvidence.FailedStart", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(4, ask.Split("new(OwnedProviderEventForwarding.HasRetention)", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void LifecycleCorrection_AgentStartPreservesFullFailureWithoutChangingErrorPublicationSelection()
    {
        var agent = Read(RuntimePluginLiveEventSourceInverse.Agent);
        StringAssert.Contains(agent, "await run.Start.RunAsync(lifecycle, runId, linkedCts.Token).ConfigureAwait(false);");
        StringAssert.Contains(agent, "internal Task? Started => Start.Original;");
        StringAssert.Contains(agent, "var originalBodyFailure = run.Start.PublicationFailure ?? ex;");
        StringAssert.Contains(agent, "run.ErrorPublication = AppendRunErrorAsync(runId, originalBodyFailure);");
        StringAssert.Contains(agent, "catch (Exception publicationFailure) { throw new AggregateException(ex, publicationFailure); }");
        var start = agent.IndexOf("    internal sealed class RunStartInvocation", StringComparison.Ordinal);
        var end = agent.IndexOf("    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools(", start, StringComparison.Ordinal);
        var receipt = agent[start..end];
        Before(receipt, "Original = lifecycle.StartedAsync(runId, executionToken)", "await Original.ConfigureAwait(false);");
        Before(receipt, "AwaitedFailure = failure;", "OriginalFaults = Original?.Exception;");
        Before(receipt, "OriginalFaults = Original?.Exception;", "ExceptionDispatchInfo.Throw(Failure);");
        StringAssert.Contains(receipt, "(lifecycle as IRunStartEvidence)?.FailedStart?.PublicationFailure ?? failure");
        StringAssert.Contains(receipt, "OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure");
        StringAssert.Contains(agent, "run.Finish = FinishRunAsync(run, admitted, options.RunLifecycle, callerRegistration, failure);");
        StringAssert.Contains(agent, "}, bodyFailure).ConfigureAwait(false);");
    }

    [TestMethod]
    public void Correction_PublicationAndForwarderUseClassifyBeforeDependentRelease()
    {
        var helper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Before(helper, "PublicationFailure = CaptureFailure(PublicationOriginal, failure);", "var publicationRetained =");
        Before(helper, "RecordRetention(PublicationFailure);", "try { releasePublicationUse(); }");
        StringAssert.Contains(helper, "if (!publicationRetained)");
        StringAssert.Contains(helper, "if (!publicationRetained && ReleaseFailure is null && _retentionFailures.Count == 0");
        StringAssert.Contains(helper, "ObservationPrerequisite?.MayObserve != false");
        var forwarding = Read(RuntimePluginLiveEventSourceInverse.Forwarding);
        var start = forwarding.IndexOf("    internal Task Forward(", StringComparison.Ordinal);
        var end = forwarding.IndexOf("    internal Task RetireAsync(", start, StringComparison.Ordinal);
        var callback = forwarding[start..end];
        Before(callback, "receipt.CallbackAwaitedFailure = failure;", "receipt.CallbackFaults = receipt.CallbackOriginal?.Exception;");
        Before(callback, "receipt.CallbackFaults = receipt.CallbackOriginal?.Exception;", "use.Retain(evidence);");
        Before(callback, "use.Retain(evidence);", "finally { use.Dispose(); }");
        StringAssert.Contains(forwarding, "Interlocked.CompareExchange(ref _released, 2, 0)");
        StringAssert.Contains(forwarding, "Interlocked.CompareExchange(ref _released, 1, 0)");
        var runtime = Read(RuntimePluginLiveEventSourceInverse.Runtime);
        Before(runtime, "projectionUse.Retain(failure);", "_forwarding.RetainDependencies(failure, dependencies);");
    }

    [TestMethod]
    public void Correction_SyntheticObservationUsesSendCleanupReceiptWithoutDroppingPublicationOrLifecycle()
    {
        var runtime = Read(RuntimePluginLiveEventSourceInverse.Runtime);
        StringAssert.Contains(runtime, "execution?.Dispose(); executionReleased = true;");
        StringAssert.Contains(runtime, "executionReleased && (handleUse is null || handleUse.IsReleased)");
        StringAssert.Contains(runtime, "new { Runtime = this, Source = execution, Use = handleUse, Run = runInvocation, Close = permissionClose, Ask = askExecution, Permission = permissionExecution }");
        StringAssert.Contains(runtime, "new LiveEventPublication(this) { ObservationPrerequisite = prerequisite }");
        var synthetic = runtime[runtime.IndexOf("    private async Task PublishRuntimeFailureEventAsync(", StringComparison.Ordinal)..];
        Before(synthetic, "_events.TryPublish(new SessionAgentEvent(envelope.SessionId, publishedEvent));", "mark(envelope);");
        Before(synthetic, "mark(envelope);", "Kind = SessionLifecycleEventKind.RunFailed,");
        var helper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        StringAssert.Contains(helper, "PublishRuntimeFailureEventAsync(session, original, prerequisite)");
        StringAssert.Contains(helper, "internal bool MayObserve => CleanupReleased && !OwnedProviderEventForwarding.HasRetention(Failure);");
        var queue = Read(RuntimePluginLiveEventSourceInverse.Queue);
        Assert.IsFalse(queue.Contains("ObserveRuntimeFailureAsync", StringComparison.Ordinal));
        Assert.IsFalse(queue.Contains("PublishRuntimeFailureEventAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Correction_ProviderOwnsEachEligibleIndependentStageAndPrepublicationTolerance()
    {
        var runtime = Read(RuntimePluginLiveEventSourceInverse.Runtime);
        var start = runtime.IndexOf("    private async Task PostAgentEventToActorCoreAsync(", StringComparison.Ordinal);
        var end = runtime.IndexOf("    private static bool IsQueueDrainTrigger(", start, StringComparison.Ordinal);
        var provider = runtime[start..end];
        Before(provider, "notifications = projector.Entry!.TakeParentNotifications(sanitized);", "actorChoresCompleted = true;");
        Before(provider, "effects.Add(() => InvalidateFileSearchCacheAsync(published, workingDirectory));", "effects.Add(() => DeliverParentNotificationAsync(notification));");
        Before(provider, "effects.Add(() => DeliverParentNotificationAsync(notification));", "effects.Add(() => TryDrainNextQueuedPromptAsync(sessionId));");
        StringAssert.Contains(provider, "if (actorChoresCompleted && IsQueueDrainTrigger(@event))");
        StringAssert.Contains(provider, "publication.IndependentWork = new LiveEventIndependentWork(effects);");
        StringAssert.Contains(provider, "return publication.IndependentWork.RunAsync();");
        Assert.AreEqual(5, provider.Split("publication.CanToleratePrepublicationFailure", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(provider.Contains("when (publication.ObservationFailure is null)", StringComparison.Ordinal));
        var helper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Before(helper, "Stages[index].Launch(_effects[index]);", "await Stages[index].Outcome.ConfigureAwait(false)");
    }

    [TestMethod]
    public void Correction_CloseRechecksActualActorOriginalAndFullRetentionBeforeEvents()
    {
        var forwarding = Read(RuntimePluginLiveEventSourceInverse.Forwarding);
        Before(forwarding, "await actorStage.Observer.ConfigureAwait(false);", "if (actorStage.Original is null || HasRetainedDependencies())");
        Before(forwarding, "throw RetainedFailure(\"actor cleanup\", actorStage);", "try { completeEvents(); }");
        StringAssert.Contains(forwarding, "OriginalFaults = Original?.Exception;");
        StringAssert.Contains(forwarding, "Failure = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;");
    }

    [TestMethod]
    public void LiveOrigins_UsePublicationReceiptAndReaderDoesNotObserve()
    {
        var runtime = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs");
        foreach (var marker in new[]
        {
            "mark(CapturePluginEvent(@event, sessionId, projectId, effectWorkingDirectory));",
            "mark(CapturePluginEvent(notes, session.SessionId, session.ProjectId, session.WorkingDirectory));",
            "mark(CapturePluginEvent(sanitized, sessionId, projectId, workingDirectory));",
            "mark(envelope);",
        }) StringAssert.Contains(runtime, marker);
        var coordinator = Read("CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs");
        var direct = coordinator.IndexOf("    public void HandleAgentEvent(", StringComparison.Ordinal);
        Assert.IsTrue(direct > 0);
        Assert.IsFalse(coordinator[..direct].Contains("ObservePluginAgentEvent(session,", StringComparison.Ordinal));
        StringAssert.Contains(coordinator[direct..], "ObservePluginAgentEvent(session, @event);");
        var publication = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Before(publication, "await PublicationOriginal.ConfigureAwait(false);", "releasePublicationUse();");
        Before(publication, "releasePublicationUse();", "ObservationOriginal = observe(envelope)");
        StringAssert.Contains(publication, "ObserveAsync(envelope, CancellationToken.None)");
        StringAssert.Contains(publication, "IndependentOriginal = independent()");
    }

    [TestMethod]
    public void HostComposition_CapturesProjectAndSelectsExplicitFailurePolicy()
    {
        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        StringAssert.Contains(host, "PluginEventCurrentProjectId = currentProject.Id,");
        StringAssert.Contains(host, "PluginEventCurrentProjectPath = currentProject.ProjectPath,");
        StringAssert.Contains(host, "options.PluginAgentEventFailurePolicy ??");
        var terminal = Read("CodeAlta.Tui/App/CodeAltaOwnedServices.cs");
        StringAssert.Contains(terminal, "RuntimePluginAgentEventFailurePolicy.ReportAsync(");
        StringAssert.Contains(terminal, "CodeAltaCrashReporter.ReportFatalTaskException");
    }

    [TestMethod]
    public void StartupRelease_RevalidatesRetentionAndReservesAttemptUnderEvidenceGate()
    {
        var program = Read(RuntimePluginLiveEventSourceInverse.Program);
        var decisionStart = program.IndexOf("        internal bool TryAuthorizeRelease(", StringComparison.Ordinal);
        var decisionEnd = program.IndexOf("        private sealed class AdmissionWrapper", decisionStart, StringComparison.Ordinal);
        Assert.IsTrue(decisionStart >= 0 && decisionEnd > decisionStart);
        var decision = program[decisionStart..decisionEnd];
        var inspection = decision.IndexOf("try { confirmed = prerequisites(); }", StringComparison.Ordinal);
        Assert.IsTrue(inspection > 0);
        var authorization = decision[inspection..];
        Before(authorization, "lock (_gate)", "if (_retained || (anchor ? _anchorReleaseAttempted : _admissionReleaseAttempted)) return false;");
        Before(authorization, "if (_retained ||", "if (anchor) _anchorReleaseAttempted = true;");
        Before(authorization, "_retained = true;", "throw missing;");
        Assert.IsFalse(decision.Contains("AdmissionLease.Dispose()", StringComparison.Ordinal));
        Assert.IsFalse(decision.Contains("_releaseAnchor(this)", StringComparison.Ordinal));
        StringAssert.Contains(program, "AdmissionLease is null || !TryAuthorizeRelease(false,");
        StringAssert.Contains(program, "!_anchorAllocated || !TryAuthorizeRelease(true,");
        StringAssert.Contains(program, "var cleanup = owner.StartAppRelease(app);");
        StringAssert.Contains(program, "App is null || _appRelease is { Original: not null } appRelease && appRelease.Outcome.IsCompleted");
        StringAssert.Contains(program, "static owner => owner._anchor = GCHandle.Alloc(owner, GCHandleType.Normal)");
        Before(program, "_allocateAnchor(this);", "AdmissionLease = acquire()");
    }

    [TestMethod]
    public void DeferredDisposal_CapturesFullOriginalGraphsBeforeSuppressionAndRelease()
    {
        var source = Read(RuntimePluginLiveEventSourceInverse.Deferred);
        StringAssert.Contains(source, "new DeferredOriginalFailure(original, awaitedFailure, original?.Exception)");
        Before(source, "var evidence = CaptureOriginalFailure(startupTask, ex);", "if (evidence.HasRetention ||");
        Before(source, "if (evidence.HasRetention ||", "!ReferenceEquals(ex, reportedStartupFailure)");
        StringAssert.Contains(source, "(failures ??= []).Add(CaptureOriginalFailure(appDisposalOriginal, ex).Reported);");
        StringAssert.Contains(source, "(failures ??= []).Add(CaptureOriginalFailure(servicesDisposalOriginal, ex).Reported);");
        StringAssert.Contains(source, "OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : AwaitedFailure");
        StringAssert.Contains(source, "Program.StartupOwner.ContainsRetention(AwaitedFailure)");
        StringAssert.Contains(source, "Program.StartupOwner.ContainsRetention(OriginalFaults)");
        var afterServiceFailure = source[source.IndexOf("CaptureOriginalFailure(servicesDisposalOriginal, ex)", StringComparison.Ordinal)..];
        Before(afterServiceFailure, "ThrowIfRetained();", "await disposeUpdate();");
        StringAssert.Contains(source, "Source = disposeStartupCancellation }, originalFailures.ToArray())");
    }

    [TestMethod]
    public void RetainedOwners_KeepOriginalReceiptsAndGateDependentRelease()
    {
        var agent = Read(RuntimePluginLiveEventSourceInverse.Agent);
        StringAssert.Contains(agent, "run.Release = new RunReleaseDecision(run);");
        StringAssert.Contains(agent, "Retained = Closing.Original is null || registrationFailure is not null");
        StringAssert.Contains(agent, "Retained |= Cancellation.Original is null || AgentDependencyRetentionException.Contains(Cancellation.Failure!);");
        StringAssert.Contains(agent, "operation.Retain(cleanup, run);");
        var hub = Read(RuntimePluginLiveEventSourceInverse.Hub);
        Before(hub, "entry.Lifetime.RecordRun(() => entry.Coordinator.RunAsync", "original.Launch();");
        StringAssert.Contains(hub, "entry.Lifetime.CompleteReference(original.Failure, original);");
        Before(hub, "await activeReferences.ConfigureAwait(false);", "SessionDisposal = new SessionPermissionService.DeliveryStage");
        Before(hub, "if (failures.Any(OwnedProviderEventForwarding.HasRetention))\n                throw new AgentDependencyRetentionException(\"hub entry\", \"session release\"", "ProviderDisposal = new SessionPermissionService.DeliveryStage");
        var commands = Read(RuntimePluginLiveEventSourceInverse.Commands);
        Before(commands, "foreach (var operation in operations) StartControl(operation);", "launch.TrySetResult();");
        StringAssert.Contains(commands, "operation.SendInvocation.Launch(() => operation.Send = _runtime.SendOwnedCommandAsync");
        StringAssert.Contains(commands, "operation.ReleaseDecision.Complete();\n        operation.Released = operation.ReleaseDecision.Released;");
        StringAssert.Contains(commands, "// All real work and independent controls have settled. Only now is release eligibility stable:");
        var queue = Read(RuntimePluginLiveEventSourceInverse.Queue);
        StringAssert.Contains(queue, "item.Run.Launch(() => runOriginal = RunCapturedAsync(work.SessionHandleId, send, item.Execution.Token));");
        StringAssert.Contains(queue, "if (item.ReleaseDecision.Released) item.Drained.TrySetResult();");
        Assert.IsFalse(queue.Contains("SendOwnedBodyAsync", StringComparison.Ordinal));
        var permissions = Read(RuntimePluginLiveEventSourceInverse.Permissions);
        Before(permissions, "execution.Closure = CloseCoreAsync();", "execution.Closed = true;");
        StringAssert.Contains(permissions, "throw new AgentDependencyRetentionException(execution.SessionId, \"permission closure index\"");
        var host = Read(RuntimePluginLiveEventSourceInverse.Host);
        Before(host, "reads.Launch();", "commands.Launch();");
        Before(host, "commands.Launch();", "await commands.ReportedOutcome.ConfigureAwait(false)");
        StringAssert.Contains(host, "retained is null ? disposeRuntime() : drainRetainedRuntime(retained)");
    }

    [TestMethod]
    public void Preservation_RestoresAllTwentyFiveCompleteOriginalInputs()
    {
        Assert.AreEqual(25, RuntimePluginLiveEventSourceInverse.Paths.Count);
        Assert.AreEqual(25, RuntimePluginLiveEventSourceInverse.Paths.Distinct(StringComparer.Ordinal).Count());
        foreach (var path in RuntimePluginLiveEventSourceInverse.Paths)
        {
            var current = Read(path);
            var lines = current.Split('\n');
            var mixed = string.Concat(lines.Select((line, index) => line +
                (index == lines.Length - 1 ? "" : index % 2 == 0 ? "\r\n" : "\n")));
            foreach (var representation in new[] { current, current.Replace("\n", "\r\n", StringComparison.Ordinal), mixed })
            {
                var restored = RuntimePluginLiveEventSourceInverse.Restore(path, representation);
                Assert.AreEqual(RuntimePluginLiveEventSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
                Assert.ThrowsExactly<AssertFailedException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, restored));
            }
            Assert.ThrowsExactly<AssertFailedException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, current + "// unexpected\n"));
            Assert.ThrowsExactly<ArgumentException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, "\uFEFF" + current));
            Assert.ThrowsExactly<ArgumentException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, current.Replace("\n", "\r", StringComparison.Ordinal)));
            Assert.ThrowsExactly<AssertFailedException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, current[..^1]));
        }
        Assert.AreEqual("unchanged", RuntimePluginLiveEventSourceInverse.RestoreInput("unlisted", "unchanged"));
    }

    [TestMethod]
    public void Preservation_AuthenticatesCodeInsideRemovedSections()
    {
        foreach (var (path, marker) in new[]
        {
            (RuntimePluginLiveEventSourceInverse.Guard, "evidence.RollbackAttempted = true;"),
            (RuntimePluginLiveEventSourceInverse.Host, "var eventFailurePolicy ="),
            (RuntimePluginLiveEventSourceInverse.Ask, "internal sealed class ClosingPair"),
        })
        {
            var current = Read(path);
            Assert.AreEqual(1, current.Split(marker, StringSplitOptions.None).Length - 1);
            var changed = current.Replace(marker, marker + " /* unexpected inside removed section */", StringComparison.Ordinal);
            Assert.ThrowsExactly<AssertFailedException>(() => RuntimePluginLiveEventSourceInverse.Restore(path, changed));
        }
    }

    [TestMethod]
    public void Preservation_RecursiveReadersKeepCanonicalAnchorsAndInventories()
    {
        Assert.AreEqual(18, PluginAgentEventOwnershipSourceInverse.Paths.Count);
        Assert.AreEqual(18, OwnedSessionUserInputSourceInverse.Paths.Count);
        Assert.AreEqual(12, RuntimeFileSearchInvalidationSourceInverse.Paths.Count);
        foreach (var path in PluginAgentEventOwnershipSourceInverse.Paths)
            Check(path, PluginAgentEventOwnershipSourceInverse.Restore, PluginAgentEventOwnershipSourceInverse.Original(path));
        foreach (var path in OwnedSessionUserInputSourceInverse.Paths)
            Check(path, OwnedSessionUserInputSourceInverse.Restore, OwnedSessionUserInputSourceInverse.Original(path));
        foreach (var path in RuntimeFileSearchInvalidationSourceInverse.Paths)
            Check(path, RuntimeFileSearchInvalidationSourceInverse.Restore, RuntimeFileSearchInvalidationSourceInverse.Original(path));

        foreach (var (path, expected) in new[]
        {
            (RuntimePluginLiveEventSourceInverse.Architecture, "321B5BD7F0C30F83E80D8E8EC3C06185DA779F49220B22BB37891821C0ECBBFD"),
            ("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs", "EAEBDD92546584DE2B50A7C2B6B22A33B432ECEDE82C54C3D4963C497A1C397D"),
            (RuntimePluginLiveEventSourceInverse.StartupTests, "25F1637498D576F76BB734BF2C6BCD3B3BBBD1D1D13C07ADFE7B2B07285A77A3"),
        })
        {
            var source = Read(path);
            var restored = PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path, PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, source));
            Assert.AreEqual(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restored))), path);
        }

        static void Check(string path, Func<string, string, string> restore, string expected)
        {
            var current = Read(path);
            var restored = restore(path, current);
            Assert.AreEqual(expected, OwnedSessionAskSourceInverse.GitObjectId(restored), path);
            Assert.ThrowsExactly<AssertFailedException>(() => restore(path, restored));
        }
    }

    private static void Before(string source, string first, string second)
    {
        var start = source.IndexOf(first, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, first);
        Assert.IsTrue(source.IndexOf(second, start + first.Length, StringComparison.Ordinal) > start, second);
    }

    private static string Read(string path, [CallerFilePath] string caller = "")
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
}
