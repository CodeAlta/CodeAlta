using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Execution requires separate source/body admission. Only the registered fake implements provider operations.
/// Every fixture keeps its explicit root, original work and observers on failure or unconfirmed cleanup.
/// </summary>
[TestClass]
public sealed class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    public Task Activity_IsAttachmentLocalAdmissionOrderWithExplicitOmissions() => Fixture.Run(async f =>
    {
        var read = () => f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "project", f.ProjectId, f.ProjectPath);
        Assert.IsNull((await f.Wait(read())).State!.Entry);
        Assert.AreEqual(0, f.Provider.Creates);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.IsNull((await f.Wait(read())).State!.Entry!.Activity!.Timestamp);
        var creates = f.Provider.Creates;
        var when = DateTimeOffset.Parse("2026-01-01T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, timestamp: when);
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, timestamp: when.AddDays(-1));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, timestamp: DateTimeOffset.MinValue);
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, timestamp: when, eventSessionId: "foreign-session");
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, timestamp: when, eventProviderId: new ModelProviderId("foreign-provider"));
        var observed = (await f.Wait(read())).State!.Entry!;
        Assert.AreEqual(new SessionRuntimeActivity(when.AddDays(-1), 2, 3), observed.Activity);
        Assert.AreEqual(creates, f.Provider.Creates);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        f.Provider.HoldAbort = true;
        var replacement = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("replacement")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var retiring = (await f.Wait(read())).State!;
        Assert.IsTrue(retiring.Entry!.IsRetiring);
        Assert.AreEqual(observed.Activity, retiring.Entry.Activity);
        f.Provider.ReleaseAbort.TrySetResult(); await f.Wait(replacement);
        var fresh = (await f.Wait(read())).State!.Entry!;
        Assert.AreNotEqual(observed.AttachmentGeneration, fresh.AttachmentGeneration);
        Assert.AreEqual(new SessionRuntimeActivity(null, 0, 0), fresh.Activity);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var returned = (await f.Wait(read())).State!.Entry!;
        Assert.IsTrue(returned.AttachmentGeneration > fresh.AttachmentGeneration, "Returning to original configuration is a new attachment, not an activity ABA.");
        Assert.AreEqual(new SessionRuntimeActivity(null, 0, 0), returned.Activity);
        Assert.AreEqual(0, f.Provider.HistoryReads);
    });

    [TestMethod]
    public Task OwnedState_VerifiesScopeWithoutAcquisitionAndPreservesAbsentTransitionFacts() => Fixture.Run(async f =>
    {
        var read = () => f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "project", f.ProjectId, f.ProjectPath);
        var absent = await f.Wait(read());
        Assert.AreEqual("ok", absent.Status);
        Assert.IsNull(absent.State!.Entry);
        Assert.AreEqual(0, f.Provider.Creates);
        Assert.AreEqual(0, f.Provider.AttachmentCount);
        var global = f.NewSession(); global.Kind = SessionViewKind.GlobalSession; global.ProjectRef = null; global.WorkingDirectory = f.GlobalRoot;
        await f.Persist(global);
        var globalObserved = await f.Wait(f.Runtime.ObserveOwnedStateAsync(global.SessionId, global.CreatedAt, "global", null, null));
        Assert.AreEqual("ok", globalObserved.Status); Assert.IsNull(globalObserved.State!.Entry);
        Assert.AreEqual("scope_mismatch", (await f.Wait(f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "global", null, null))).Status);
        Assert.AreEqual("scope_mismatch", (await f.Wait(f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "project", f.ProjectId, f.GlobalRoot))).Status);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var attached = await f.Wait(read());
        Assert.AreEqual("ok", attached.Status);
        Assert.IsNotNull(attached.State!.Entry);
        var creates = f.Provider.Creates;
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("observed-run"));
        Assert.AreEqual("observed-run", (await f.Wait(read())).State!.Entry!.ActiveRunId);
        await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null, timestamp: DateTimeOffset.UnixEpoch);
        Assert.IsTrue((await f.Wait(read())).State!.Entry!.IsTerminated);
        Assert.AreEqual(creates, f.Provider.Creates);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = f.GlobalRoot });
        var project = (await catalog.GetByIdAsync(f.ProjectId))!;
        project.Archived = true; await catalog.SaveAsync(project);
        Assert.AreEqual("archived_project", (await f.Wait(read())).Status);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await f.ExpectCancellation(f.Track(f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "project", f.ProjectId, f.ProjectPath, canceled.Token)));
    });

    [TestMethod]
    public Task CurrentState_AbsentAndCanceledQueriesDoNotAcquire_AndClosureRejects() => Fixture.Run(async f =>
    {
        var absent = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsNull(absent.Entry);
        Assert.IsFalse(absent.CoordinatorTransitionInProgress);
        Assert.AreNotEqual(Guid.Empty, absent.RuntimeInstanceId);
        Assert.AreEqual(f.Session.SessionId, absent.SessionId);
        await f.Expect<ArgumentNullException>(f.Track(f.Runtime.GetCurrentStateAsync(null!)));
        await f.Expect<ArgumentException>(f.Track(f.Runtime.GetCurrentStateAsync(" ")));
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        await f.ExpectCancellation(f.Track(f.Runtime.GetCurrentStateAsync(f.Session.SessionId, preCanceled.Token)));
        var next = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual(absent, next);
        Assert.AreEqual(0, f.Provider.AttachmentCount);
        Assert.AreEqual(0, f.Provider.Creates);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        await f.Expect<ObjectDisposedException>(f.Track(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
    });

    [TestMethod]
    public Task UsageState_AbsentZeroUnknownAndTypedEvent_AreAttachmentScoped() => Fixture.Run(async f =>
    {
        var absent = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsNull(absent.AttachmentGeneration);
        Assert.IsNull(absent.Observation);
        Assert.AreEqual(0, f.Provider.Creates);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var empty = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsNotNull(empty.AttachmentGeneration);
        Assert.IsNull(empty.Observation);
        var when = new DateTimeOffset(2026, 9, 24, 1, 2, 3, TimeSpan.Zero);
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null, usage: new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(0, null, null, new string('x', 100_000)),
            LastOperation: new AgentOperationUsageSnapshot(InputTokens: 0, OutputTokens: 12, Model: new string('m', 100_000)),
            Scope: AgentUsageScope.LastOperation, Source: AgentUsageSource.CodexTokenCountEvent, UpdatedAt: when), timestamp: when);
        var state = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.AreEqual(empty.RuntimeInstanceId, state.RuntimeInstanceId);
        Assert.AreEqual(empty.AttachmentGeneration, state.AttachmentGeneration);
        Assert.AreEqual(1L, state.Observation!.Sequence);
        Assert.AreEqual(0L, state.Observation.Window?.CurrentTokens);
        Assert.IsNull(state.Observation.Window?.TokenLimit);
        Assert.AreEqual(0L, state.Observation.LastOperation?.InputTokens);
        Assert.AreEqual(12L, state.Observation.LastOperation?.OutputTokens);
        Assert.AreEqual(when, state.Observation.SourceUpdatedAt);
        Assert.AreEqual(AgentUsageScope.LastOperation, state.Observation.Scope);
        Assert.AreEqual(AgentUsageSource.CodexTokenCountEvent, state.Observation.Source);
        Assert.IsFalse(state.Observation.HadInvalidValues);
        Assert.IsTrue(state.Observation.HadOmittedData);
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(55, 90, null)),
            timestamp: when, eventProviderId: new ModelProviderId("other-provider"));
        var rejected = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.AreEqual(1L, rejected.OmittedUsageEvents);
        Assert.AreEqual(0L, rejected.Observation!.Window?.CurrentTokens);
        Assert.AreEqual(1L, rejected.Observation.Sequence);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null, timestamp: when);
        var terminated = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsTrue(terminated.IsTerminated);
        Assert.IsNull(terminated.Observation);
    });

    [TestMethod]
    public Task OwnedUsage_RequiresPersistedScopeAndCompleteUnarchivedCatalogWithoutActivation() => Fixture.Run(async f =>
    {
        var absent = await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath));
        Assert.AreEqual("missing_session", absent.Status);
        Assert.AreEqual(0, f.Provider.Creates);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.AreEqual("no_observation", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath))).Status);
        Assert.AreEqual("scope_mismatch", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "global", null, null))).Status);
        Assert.AreEqual("scope_mismatch", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", Guid.NewGuid().ToString("D"), f.ProjectPath))).Status);
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(0, long.MaxValue, null)), timestamp: DateTimeOffset.UnixEpoch);
        var observed = await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath));
        Assert.AreEqual("ok", observed.Status);
        Assert.AreEqual(long.MaxValue, observed.Usage!.Window!.Value.TokenLimit);
        Assert.AreEqual(0, f.Provider.HistoryReads);
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = f.GlobalRoot });
        var project = (await catalog.GetByIdAsync(f.ProjectId))!;
        project.Archived = true;
        await catalog.SaveAsync(project);
        Assert.AreEqual("archived_project", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath))).Status);
    });

    [TestMethod]
    public Task OwnedUsage_RefusesChangedPersistedHeaderAndIncompleteOrAmbiguousCatalog() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var read = () => f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath);
        var journal = new AgentRuntimePathLayout(f.GlobalRoot).GetSessionFilePath(f.Session.SessionId, f.Session.CreatedAt);
        var originalHeader = await File.ReadAllTextAsync(journal);
        File.Delete(journal);
        Assert.AreEqual("metadata_missing", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(journal, new string('x', SessionViewJournalStore.MaximumBoundedHeaderBytes + 1) + "\n");
        Assert.AreEqual("metadata_incomplete", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(journal, originalHeader.Replace(f.ProjectId, Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
        Assert.AreEqual("metadata_mismatch", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(journal, originalHeader);
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = f.GlobalRoot });
        var descriptor = (await catalog.GetByIdAsync(f.ProjectId))!;
        var projectFile = Path.Combine(f.GlobalRoot, "projects", descriptor.Slug + ".md");
        var originalProject = await File.ReadAllTextAsync(projectFile);
        File.Delete(projectFile);
        Assert.AreEqual("missing_project", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(projectFile, originalProject + new string('x', ProjectCatalog.MaximumOwnershipFileBytes));
        Assert.AreEqual("incomplete_project", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(projectFile, "not a project descriptor");
        Assert.AreEqual("invalid_project", (await f.Wait(read())).Status);
        await File.WriteAllTextAsync(projectFile, originalProject);
        descriptor.Id = Guid.NewGuid().ToString("D");
        descriptor.Slug = "different";
        await catalog.SaveAsync(descriptor);
        Assert.AreEqual("ambiguous_project", (await f.Wait(read())).Status);
        Assert.AreEqual(0, f.Provider.HistoryReads);
    });

    [TestMethod]
    public Task OwnedUsage_GlobalRequiresPositiveActorAndPersistedGlobalScope() => Fixture.Run(async f =>
    {
        var global = f.NewSession();
        global.Kind = SessionViewKind.GlobalSession;
        global.ProjectRef = null;
        await f.Persist(global);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(global, f.Options));
        Assert.AreEqual("no_observation", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(global.SessionId, "global", null, null))).Status);
        Assert.AreEqual("scope_mismatch", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(global.SessionId, "project", f.ProjectId, f.ProjectPath))).Status);
        Assert.AreEqual("missing_session", (await f.Wait(f.Runtime.ReadOwnedUsageAsync(Guid.NewGuid().ToString("D"), "global", null, null))).Status);
        Assert.AreEqual(0, f.Provider.HistoryReads);
    });

    [TestMethod]
    public Task OwnedUsage_TransitionCancellationAndClosedHostNeverReleasePriorObservation() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(5, 10, null)), timestamp: DateTimeOffset.UnixEpoch);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await f.ExpectCancellation(f.Track(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath, canceled.Token)));
        f.Provider.HoldAbort = true;
        var replacement = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("new-model")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var transition = await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath));
        Assert.AreEqual("transition", transition.Status);
        Assert.IsNull(transition.Usage);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(replacement);
        var after = await f.Wait(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath));
        Assert.AreEqual("no_observation", after.Status);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        await f.Expect<ObjectDisposedException>(f.Track(f.Runtime.ReadOwnedUsageAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath)));
    });

    [TestMethod]
    public Task OwnedUsage_RechecksOriginalAttachmentAfterAsyncPersistedRead() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(13, 50, null)), timestamp: DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = f.GlobalRoot });
        var read = f.Track(f.Runtime.ReadOwnedUsageWithReadersAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath,
            async (id, at, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return await f.Journal.ReadBoundedHeaderAsync(id, at, token);
            }, catalog.ReadBoundedOwnershipAsync));
        try
        {
            await f.Ready(entered.Task);
            await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("replacement")));
        }
        finally { release.TrySetResult(); }
        var result = await f.Wait(read);
        Assert.AreEqual("stale_attachment", result.Status);
        Assert.IsNull(result.Usage);
        Assert.IsNull(result.AttachmentGeneration);
        Assert.AreEqual(0, f.Provider.HistoryReads);
    });

    [TestMethod]
    public Task OwnedUsage_HostClosureDuringAsyncOwnershipReadDoesNotReleaseObservation() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(22, 50, null)), timestamp: DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = f.GlobalRoot });
        var read = f.Track(f.Runtime.ReadOwnedUsageWithReadersAsync(f.Session.SessionId, "project", f.ProjectId, f.ProjectPath,
            async (id, at, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return await f.Journal.ReadBoundedHeaderAsync(id, at, token);
            }, catalog.ReadBoundedOwnershipAsync));
        await f.Ready(entered.Task);
        Task close;
        try { close = f.Track(f.Runtime.DisposeAsync().AsTask()); }
        finally { release.TrySetResult(); }
        await f.Expect<ObjectDisposedException>(read);
        await f.Wait(close);
    });

    [TestMethod]
    public Task UsageState_InvalidValuesDoNotBecomeZeroOrReusePriorFields() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null, usage: new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(23, 100, 1)), timestamp: DateTimeOffset.UnixEpoch);
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null, usage: new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(-1, 0, -2),
            LastOperation: new AgentOperationUsageSnapshot(InputTokens: -5, OutputTokens: 0, Cost: double.NaN, DurationMs: double.PositiveInfinity),
            Scope: (AgentUsageScope)1000, Source: (AgentUsageSource)1000), timestamp: DateTimeOffset.UnixEpoch);
        var observation = (await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId))).Observation!;
        Assert.AreEqual(2L, observation.Sequence);
        Assert.IsTrue(observation.HadInvalidValues);
        Assert.IsNull(observation.Window?.CurrentTokens);
        Assert.IsNull(observation.Window?.TokenLimit);
        Assert.IsNull(observation.Window?.MessageCount);
        Assert.IsNull(observation.LastOperation?.InputTokens);
        Assert.AreEqual(0L, observation.LastOperation?.OutputTokens);
        Assert.IsNull(observation.LastOperation?.Cost);
        Assert.IsNull(observation.LastOperation?.DurationMs);
        Assert.AreEqual(AgentUsageSource.Unknown, observation.Source);
        Assert.AreEqual(AgentUsageScope.Unknown, observation.Scope);
    });

    [TestMethod]
    public Task UsageState_IsolatedFromOtherSessionsAndRetiredCallbacks() => Fixture.Run(async f =>
    {
        var second = f.NewSession();
        await f.Persist(second);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var firstSession = f.Provider.Latest;
        var staleCallback = firstSession.CapturedCallback;
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(7, 30, null)),
            timestamp: DateTimeOffset.UnixEpoch);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(second, f.Options));
        var secondState = await f.Wait(f.Runtime.GetUsageStateAsync(second.SessionId));
        Assert.IsNull(secondState.Observation);
        var firstState = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.AreEqual(7L, firstState.Observation!.Window?.CurrentTokens);
        Assert.AreNotEqual(firstState.SessionId, secondState.SessionId);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("new-model")));
        var replacement = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.AreNotEqual(firstState.AttachmentGeneration, replacement.AttachmentGeneration);
        Assert.IsNull(replacement.Observation);
        staleCallback(new AgentSessionUpdateEvent(f.Provider.Descriptor.ProviderId, f.Session.SessionId,
            DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.UsageUpdated, "stale", Usage:
            new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(999, 1000, null))));
        var stillEmpty = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsNull(stillEmpty.Observation);
        Assert.AreEqual(0L, stillEmpty.OmittedUsageEvents);
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(0, null, null)),
            timestamp: DateTimeOffset.UnixEpoch, eventSessionId: "wrong-session");
        var omitted = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsNull(omitted.Observation);
        Assert.AreEqual(1L, omitted.OmittedUsageEvents);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        Assert.IsNull((await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId))).Observation);
    });

    [TestMethod]
    public Task UsageState_TransitionCancellationAndCloseWithholdObservation() => Fixture.Run(async f =>
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await f.ExpectCancellation(f.Track(f.Runtime.GetUsageStateAsync(f.Session.SessionId, canceled.Token)));
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.UsageUpdated, null,
            usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(3, 10, null)),
            timestamp: DateTimeOffset.UnixEpoch);
        f.Provider.HoldAbort = true;
        var replacement = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("new-model")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var transitioning = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        Assert.IsTrue(transitioning.CoordinatorTransitionInProgress);
        Assert.IsTrue(transitioning.IsRetiring);
        Assert.IsNull(transitioning.Observation);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(replacement);
        Assert.IsNull((await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId))).Observation);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        await f.Expect<ObjectDisposedException>(f.Track(f.Runtime.GetUsageStateAsync(f.Session.SessionId)));
    });

    [TestMethod]
    public Task UsageState_CancellationAfterAdmissionStopsWaitWithoutReplacingObservation() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var before = await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId));
        var held = new HeldEmptyTools();
        Task? ensure = null;
        try
        {
            ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", held)));
            await f.Ready(held.Entered.Task);
            using var cancellation = new CancellationTokenSource();
            var query = f.Track(f.Runtime.GetUsageStateAsync(f.Session.SessionId, cancellation.Token));
            Assert.IsFalse(query.IsCompleted);
            cancellation.Cancel();
            await f.ExpectCancellation(query);
            Assert.IsFalse(ensure.IsCompleted);
        }
        finally { held.Release.TrySetResult(); }
        await f.Wait(ensure!);
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetUsageStateAsync(f.Session.SessionId)));
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
    });

    [TestMethod]
    public Task CurrentState_CancellationAfterAdmissionStopsWait_NotOwnedActorWork() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var held = new HeldEmptyTools();
        Task? ensure = null;
        try
        {
            // Matches reads Count on the real session actor. Hold that existing operation so the
            // current-state query is admitted but cannot execute, without a runtime test hook.
            ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", held)));
            await f.Ready(held.Entered.Task);
            using var cancellation = new CancellationTokenSource();
            var query = f.Track(f.Runtime.GetCurrentStateAsync(f.Session.SessionId, cancellation.Token));
            Assert.IsFalse(query.IsCompleted);
            cancellation.Cancel();
            await f.ExpectCancellation(query);
            Assert.IsFalse(ensure.IsCompleted);
        }
        finally { held.Release.TrySetResult(); }
        await f.Wait(ensure!);
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        // Real owner shutdown joins admitted work, including the query whose caller stopped waiting.
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
    });

    [TestMethod]
    public Task CurrentState_CapturesConfigurationAndReplacementWithoutMutatingPriorSnapshot() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsNotNull(before.Entry);
        Assert.AreEqual("fixture-model", before.Entry.ModelId);
        Assert.AreEqual(f.Provider.Descriptor.ProviderId.Value, before.Entry.ProviderId);
        Assert.AreEqual(f.Options.ProviderKey, before.Entry.ProviderKey);
        Assert.AreEqual(f.Options.ReasoningEffort, before.Entry.ReasoningEffort);
        Assert.AreEqual("default", before.Entry.AgentPromptId);
        Assert.IsNull(before.Entry.PendingAgentPromptId);
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        var pending = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual("default", pending.Entry!.AgentPromptId);
        Assert.AreEqual("plan", pending.Entry.PendingAgentPromptId);
        f.Provider.HoldAbort = true;
        var replace = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("replacement-model")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var during = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsTrue(during.CoordinatorTransitionInProgress);
        Assert.IsTrue(during.Entry!.IsRetiring);
        var scoped = await f.Wait(f.Runtime.ObserveOwnedStateAsync(f.Session.SessionId, f.Session.CreatedAt, "project", f.ProjectId, f.ProjectPath));
        Assert.AreEqual("ok", scoped.Status);
        Assert.AreEqual(during, scoped.State);
        Assert.AreEqual(before.Entry.AttachmentGeneration, during.Entry.AttachmentGeneration);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(replace);
        var after = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual(before.RuntimeInstanceId, after.RuntimeInstanceId);
        Assert.AreNotEqual(before.Entry.AttachmentGeneration, after.Entry!.AttachmentGeneration);
        Assert.IsFalse(after.CoordinatorTransitionInProgress);
        Assert.IsFalse(after.Entry.IsRetiring);
        Assert.AreEqual("replacement-model", after.Entry.ModelId);
        Assert.AreEqual("plan", after.Entry.AgentPromptId);
        Assert.IsNull(after.Entry.PendingAgentPromptId);
        Assert.IsFalse(before.Entry.IsRetiring);
        Assert.AreEqual("fixture-model", before.Entry.ModelId);
        Assert.IsNull(before.Entry.PendingAgentPromptId);
    });

    [TestMethod]
    public Task CurrentState_ReportsEntrylessPreparation_AndQueueDrainWithoutInventingRunState() => Fixture.Run(async f =>
    {
        f.Provider.HoldPreparation = true;
        var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Ready(f.Provider.PreparationStarted.Task);
        var preparing = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsNull(preparing.Entry);
        Assert.IsTrue(preparing.CoordinatorTransitionInProgress);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Wait(ensure);
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "held", "send", null));
        f.Provider.Latest.EmitIdle();
        await f.Ready(f.Provider.SendStarted.Task);
        var draining = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsTrue(draining.Entry!.QueueDrainInProgress);
        Assert.IsFalse(draining.Entry.IsTerminated);
        f.Provider.ReleaseSend.TrySetResult();
        var settled = await f.Wait(DrainSettled());
        Assert.IsFalse(settled.Entry!.QueueDrainInProgress);
        Assert.AreEqual(draining.Entry.AttachmentGeneration, settled.Entry.AttachmentGeneration);
        // Detach joins the actual held work and removes the entry, not a simulated drain state.
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        var detached = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsNull(detached.Entry);
        Assert.IsFalse(detached.CoordinatorTransitionInProgress);
        Assert.IsTrue(draining.Entry.QueueDrainInProgress);

        async Task<SessionRuntimeCurrentState> DrainSettled()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var snapshot = await f.Track(f.Runtime.GetCurrentStateAsync(f.Session.SessionId, timeout.Token));
                if (snapshot.Entry is { QueueDrainInProgress: false }) return snapshot;
                await Task.Delay(10, timeout.Token);
            }
        }
    });

    [TestMethod]
    public Task OwnedSteer_RejectsAbsentAndUnownedWithoutAcquisitionOrRunMutation() => Fixture.Run(async f =>
    {
        var absent = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedTextSteerRequest("steer", f.Session.SessionId, absent.RuntimeInstanceId, 1, "recorded", "text");
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(0, f.Provider.AttachmentCount);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("recorded"));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        request = request with { ExpectedAttachmentGeneration = before.Entry!.AttachmentGeneration };
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        Assert.AreEqual(0, f.Provider.Steers);
    });

    [TestMethod]
    public Task OwnedSteer_RejectsEveryStaleTargetAndNullRunWithoutRetargeting() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
        var state = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedTextSteerRequest("steer", f.Session.SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration, "recorded", "text");
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(request, CancellationToken.None)));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("recorded"));
        foreach (var stale in new[] { request with { SessionId = "missing" }, request with { ExpectedRunId = "old" },
            request with { ExpectedRuntimeInstanceId = Guid.NewGuid() }, request with { ExpectedAttachmentGeneration = request.ExpectedAttachmentGeneration + 1 } })
            await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(stale, CancellationToken.None)));
        Assert.AreEqual("recorded", (await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId))).Entry!.ActiveRunId);
        await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null);
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(1, f.Provider.AttachmentCount);
        Assert.AreEqual(0, f.Provider.Steers);
    });

    [TestMethod]
    public Task OwnedSteer_RetirementJoinsCapturedUseBeforeDisposal() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("recorded"));
        var state = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedTextSteerRequest("steer", f.Session.SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration, "recorded", "text");
        var steer = f.Track(f.Runtime.SteerOwnedCommandAsync(request, CancellationToken.None));
        await f.Ready(f.Provider.SteerStarted.Task);
        var detach = f.Track(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        // Cancellation is an independent observation; the fake deliberately stays in its original call.
        await f.Ready(f.Provider.SteerCancelled.Task);
        Assert.IsFalse(steer.IsCompleted);
        Assert.IsFalse(detach.IsCompleted);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.SteerOwnedCommandAsync(request with { ClientRequestId = "retiring" }, CancellationToken.None)));
        f.Provider.ReleaseSteer.TrySetResult();
        await f.ExpectCancellation(steer);
        await f.Wait(detach);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    public Task OwnedCompact_AbsentUnownedAndStaleTargetsDoNotMutateOrAcquire() => Fixture.Run(async f =>
    {
        var absent = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedCompactRequest("compact", f.Session.SessionId, absent.RuntimeInstanceId, 1);
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(0, f.Provider.AttachmentCount);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        request = request with { ExpectedAttachmentGeneration = before.Entry!.AttachmentGeneration };
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
        var owned = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        foreach (var stale in new[] { request, request with { ExpectedRuntimeInstanceId = Guid.NewGuid() }, request with { SessionId = "missing" } })
            await f.Expect<InvalidOperationException>(f.Track(f.Runtime.CompactOwnedCommandAsync(stale, CancellationToken.None)));
        Assert.AreEqual(owned, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null);
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.CompactOwnedCommandAsync(request with { ExpectedAttachmentGeneration = owned.Entry!.AttachmentGeneration }, CancellationToken.None)));
        Assert.AreEqual(2, f.Provider.AttachmentCount);
        Assert.AreEqual(0, f.Provider.IdleCompactions);
    });

    [TestMethod]
    public async Task AbortRun_ExistingOnlyCaptureRefusesAbsentUnownedStaleTerminatedAndDrain()
    {
        await Fixture.Run(async f =>
        {
            var absent = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
            var request = new OwnedAbortRunRequest("abort-run", f.Session.SessionId, absent.RuntimeInstanceId, 1, "original-run");
            Assert.IsNull(await f.Wait(f.Runtime.AbortRunOwnedCommandAsync(request, CancellationToken.None)));
            Assert.AreEqual(0, f.Provider.AttachmentCount);
            await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
            var unowned = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
            request = request with { ExpectedAttachmentGeneration = unowned.Entry!.AttachmentGeneration };
            Assert.IsNull(await f.Wait(f.Runtime.AbortRunOwnedCommandAsync(request, CancellationToken.None)));
            Assert.AreEqual(unowned, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
            Assert.AreEqual(1, f.Provider.AttachmentCount);
        });

        // Retirement releases the fake provider's send gate. Use fresh ownership/gates for drain.
        await Fixture.Run(async f =>
        {
            await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
            var owned = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
            var request = new OwnedAbortRunRequest("abort-run", f.Session.SessionId, owned.RuntimeInstanceId,
                owned.Entry!.AttachmentGeneration, "original-run");
            var otherAttachment = request.ExpectedAttachmentGeneration == 1 ? 2 : 1;
            foreach (var stale in new[] { request with { ExpectedAttachmentGeneration = otherAttachment },
                request with { SessionId = "missing" }, request with { ExpectedRuntimeInstanceId = Guid.NewGuid() } })
                Assert.IsNull(await f.Wait(f.Runtime.AbortRunOwnedCommandAsync(stale, CancellationToken.None)));
            // No recorded run is required for capture; unsupported provider, not event timing, rejects.
            await f.Expect<NotSupportedException>(f.Track(f.Runtime.AbortRunOwnedCommandAsync(request, CancellationToken.None)));
            await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "inert queued input", "send", null));
            await f.EmitAndObserve(AgentSessionUpdateKind.Idle, null);
            await f.Ready(f.Provider.SendStarted.Task);
            Assert.IsTrue((await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId))).Entry!.QueueDrainInProgress);
            Assert.IsNull(await f.Wait(f.Runtime.AbortRunOwnedCommandAsync(request, CancellationToken.None)));
            await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null);
            Assert.IsNull(await f.Wait(f.Runtime.AbortRunOwnedCommandAsync(request, CancellationToken.None)));
            Assert.AreEqual(1, f.Provider.AttachmentCount);
        });
    }

    [TestMethod]
    public Task OwnedCompact_RecordedRunAndQueueDrainRefuseBeforeProvider() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("active"));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedCompactRequest("compact", f.Session.SessionId, before.RuntimeInstanceId, before.Entry!.AttachmentGeneration);
        Assert.IsNull(await f.Wait(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "queued", "send", null));
        await f.EmitAndObserve(AgentSessionUpdateKind.Idle, null);
        await f.Ready(f.Provider.SendStarted.Task);
        Assert.IsTrue((await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId))).Entry!.QueueDrainInProgress);
        Assert.IsNull(await f.Wait(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(0, f.Provider.IdleCompactions);
    });

    [TestMethod]
    public Task OwnedCompact_RetirementJoinsCapturedUseAndRejectsTransition() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureOwnedCoordinatorSessionAsync(f.Session, f.OptionsFor("fixture-model", ownedDefaults: true)));
        var state = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        var request = new OwnedCompactRequest("compact", f.Session.SessionId, state.RuntimeInstanceId, state.Entry!.AttachmentGeneration);
        f.Provider.HoldIdleCompact = true;
        var compact = f.Track(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None));
        await f.Ready(f.Provider.IdleCompactStarted.Task);
        var detach = f.Track(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Ready(f.Provider.IdleCompactCancelled.Task);
        Assert.IsFalse(compact.IsCompleted);
        Assert.IsFalse(detach.IsCompleted);
        await f.Expect<InvalidOperationException>(f.Track(f.Runtime.CompactOwnedCommandAsync(request, CancellationToken.None)));
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        f.Provider.ReleaseIdleCompact.TrySetResult();
        await f.ExpectCancellation(compact);
        await f.Wait(detach);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    public Task CurrentState_RecordsRunAndShutdown_WithoutTreatingDetachAsCompletion() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("recorded-run"));
        var running = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual("recorded-run", running.Entry!.ActiveRunId);
        Assert.IsFalse(running.Entry.IsTerminated);
        Assert.IsFalse(running.Entry.QueueDrainInProgress);
        await f.EmitAndObserve(AgentSessionUpdateKind.Shutdown, null);
        var terminated = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.IsTrue(terminated.Entry!.IsTerminated);
        Assert.IsNull(terminated.Entry.ActiveRunId);
        Assert.AreEqual(running.Entry.AttachmentGeneration, terminated.Entry.AttachmentGeneration);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        Assert.IsNull((await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId))).Entry);
        Assert.AreEqual("recorded-run", running.Entry.ActiveRunId);
        Assert.IsFalse(running.Entry.IsTerminated);
    });

    [TestMethod]
    public Task Display_ActualRuntimePublishesWithoutAnEventReader_AndClosesObservation() => Fixture.Run(async f =>
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var display = f.Runtime.Display.ObserveAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await display.MoveNextAsync());
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "queued", "send", null));
        f.Session.AgentPromptId = "plan";
        await f.Wait(f.Runtime.PersistSessionLocalStateAsync(f.Session));
        await f.Wait(f.Runtime.AppendSessionEventAsync(f.Session, new AgentContentCompletedEvent(
            f.Provider.Descriptor.ProviderId, f.Session.SessionId, DateTimeOffset.UtcNow, null,
            AgentContentKind.Assistant, "display-item", null, "committed renderer text")));
        var snapshot = f.Runtime.Display.GetSnapshot();
        var session = snapshot.Sessions.Single();
        Assert.AreEqual(f.Session.SessionId, session.SessionId);
        Assert.AreEqual(1, session.QueuedPromptCount);
        Assert.AreEqual("plan", session.Configuration!.Value.AgentPromptId);
        Assert.AreEqual("committed renderer text", session.Text.Single().Text);
        Assert.IsTrue(session.Text.Single().IsComplete);
        Assert.IsNotNull(session.Lifecycle);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        Assert.IsTrue(await display.MoveNextAsync());
        Assert.IsTrue(display.Current.Snapshot.IsClosed);
        Assert.IsTrue(display.Current.HasGap);
        Assert.IsFalse(await display.MoveNextAsync());
        Assert.IsFalse(snapshot.IsClosed);
        Assert.AreEqual(0, f.Runtime.Display.SubscriberCount);
    });

    [TestMethod]
    public Task PendingAgentPrompt_QueuedTailReplacesAttachmentWithoutSelfJoin() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var old = f.Provider.Latest;
        var first = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "queued", "send", null));
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        f.Provider.HoldPreparation = true;
        old.EmitIdle();
        await f.Ready(f.Provider.ReplacementPreparationStarted.Task);
        // Replacement owns a transition, but no actor: an admitted queue mutation must finish
        // while provider setup remains held, and survive setup's later local-state update.
        var second = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "during replacement", "send", null));
        Assert.IsFalse(f.Provider.ReleasePreparation.Task.IsCompleted);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Ready(f.Provider.SendStarted.Task);
        Assert.AreNotSame(old, f.Provider.Latest);
        Assert.AreEqual("plan", f.Provider.Latest.Options.AgentPromptId);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Ready(f.Provider.SecondSendStarted.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        var state = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(state);
        Assert.AreEqual(2, state.QueuedPrompts.Count);
        CollectionAssert.AreEquivalent(new[] { first.QueueItemId, second.QueueItemId }, state.QueuedPrompts.Select(item => item.QueueItemId).ToArray());
        Assert.IsTrue(state.QueuedPrompts.All(item => item.State == "submitted"));
        Assert.AreEqual("plan", state.AgentPromptId);
    });

    [TestMethod]
    public Task FastQueuedRun_PreservesCompletionAndRecursiveDrain() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var first = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "first", "send", null));
        var second = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "second", "send", null));
        f.Provider.ReleaseSend.TrySetResult();
        f.Provider.Latest.EmitIdle();
        await f.Ready(f.Provider.SecondSendStarted.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        var state = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(state);
        Assert.AreEqual(2, state.QueuedPrompts.Count);
        CollectionAssert.AreEquivalent(new[] { first.QueueItemId, second.QueueItemId }, state.QueuedPrompts.Select(item => item.QueueItemId).ToArray());
        Assert.IsTrue(state.QueuedPrompts.All(item => item.State == "submitted"));
    });

    [TestMethod]
    public Task ConcurrentEnsure_SerializesReplacementAndRevalidatesOptions() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        f.Provider.HoldAbort = true;
        var first = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("first-model")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var second = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("second-model")));
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(1, f.Provider.AttachmentCount);
        f.Provider.ReleaseAbort.TrySetResult();
        Assert.AreNotEqual(await f.Wait(first), await f.Wait(second));
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        // Either waiter can win mailbox admission after the ticket settles. Both must revalidate
        // and receive distinct handles for their incompatible options, irrespective of winner.
        Assert.IsTrue(f.Provider.AttachmentCount >= 3);
        CollectionAssert.Contains(f.Provider.AttachmentModels, "first-model");
        CollectionAssert.Contains(f.Provider.AttachmentModels, "second-model");
    });

    [TestMethod]
    public Task BlankSession_ConcurrentEnsureReservesOneIdentity() => Fixture.Run(async f =>
    {
        f.Session.SessionId = string.Empty;
        var first = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var second = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.AreEqual(await f.Wait(first), await f.Wait(second));
        Assert.IsTrue(Guid.TryParse(f.Session.SessionId, out _));
        Assert.AreEqual(1, f.Provider.Creates);
    });

    [TestMethod]
    public Task Compact_UsesRetirementProtocolOutsideActor() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        await f.Wait(f.Runtime.CompactAsync(f.Session, f.Options));
        Assert.AreEqual(1, f.Provider.Compactions);
        Assert.AreEqual(2, f.Provider.AttachmentCount);
    });

    [TestMethod]
    public Task History_ReusesLiveAttachmentAndResumesMissingAttachment() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.Options));
        var first = f.Provider.Latest;
        await f.Wait(f.Runtime.GetHistoryAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.OptionsFor("ignored-live-model")));
        Assert.AreSame(first, f.Provider.Latest);
        Assert.AreEqual(3, f.Provider.HistoryReads);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.OptionsFor("resumed-model")));
        Assert.AreNotSame(first, f.Provider.Latest);
        Assert.AreEqual("resumed-model", f.Provider.Latest.Options.Model);
    });

    [TestMethod]
    public Task Detach_ConcurrentEnsureDoesNotRemoveReplacementActor() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        f.Provider.HoldAbort = true;
        var detach = f.Track(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Ready(f.Provider.AbortStarted.Task);
        var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.IsFalse(detach.IsCompleted);
        Assert.IsFalse(ensure.IsCompleted);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(detach);
        await f.Wait(ensure);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.IsTrue(await f.Wait(f.Runtime.HasActiveCoordinatorSessionAsync(f.Session.SessionId)));
    });

    [TestMethod]
    public Task Shutdown_AbortsQueuedRunBeforeJoiningItsTail() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "held", "send", null));
        f.Provider.Latest.EmitIdle();
        await f.Ready(f.Provider.SendStarted.Task);
        var close = f.Track(f.Runtime.DisposeAsync().AsTask());
        await f.Ready(f.Provider.AbortStarted.Task);
        await f.Wait(close);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    public async Task Shutdown_RetiresLateAttachmentAndSubscription()
    {
        await Fixture.Run(async f =>
        {
        f.Provider.HoldPreparation = true;
        var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Ready(f.Provider.PreparationStarted.Task);
        var close = f.Track(f.Runtime.DisposeAsync().AsTask());
        Assert.IsFalse(close.IsCompleted);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Expect<ObjectDisposedException>(ensure);
        await f.Wait(close);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        });
        await Fixture.Run(async f =>
        {
            var closed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Provider.BeforeSubscriptionReturn = () => closed.TrySetResult(f.Track(f.Runtime.DisposeAsync().AsTask()));
            var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
            await f.Ready(closed.Task);
            await f.Expect<ObjectDisposedException>(ensure);
            await f.Wait(await closed.Task);
            Assert.AreEqual(1, f.Provider.SubscriptionsDisposed);
            Assert.AreEqual(0, f.Provider.EarlyDisposals);
        });
    }

    [TestMethod]
    public Task ParentDelivery_SelfAndCyclicRoutesReleaseSourceUses() => Fixture.Run(async f =>
    {
        // Cached metadata lookup is the real parent finishing route; no catalog enumeration/reader.
        f.Session.ParentSessionId = f.Session.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var self = f.Provider.Latest;
        self.EmitNotification("self");
        await f.Ready(self.SendStarted.Task);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Ready(self.SendFinished.Task);
        var parent = f.NewSession();
        parent.ParentSessionId = f.Session.SessionId;
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(parent);
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var source = f.Provider.Latest;
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(parent, f.Options));
        var target = f.Provider.Latest;
        source.EmitNotification("to-parent");
        await f.Ready(target.SendStarted.Task);
        await f.Ready(target.SendFinished.Task);
        target.EmitNotification("to-child");
        await f.Ready(source.SendStarted.Task);
        await f.Ready(source.SendFinished.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_RejectsCoordinatorWithDifferentSessionDefaults(bool reviewPermissions) => Fixture.Run(async f =>
    {
        // Same execution configuration, but this non-owned caller installed different callbacks.
        // These remain inert denial/cancellation handlers; no real tool is involved.
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var original = f.Provider.Latest;
        // Same prompt as the live configuration: normal matching would consume this pending choice.
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "default"));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("pre-existing-run"));
        var before = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual("pre-existing-run", before.Entry!.ActiveRunId);
        Assert.AreEqual("default", before.Entry.PendingAgentPromptId);
        var startedAt = f.Session.StartedAt;
        var status = f.Session.Status;
        Assert.IsNull(startedAt, "The original-event fixture must not have marked a send started.");
        var admission = f.Commands.AdmitSend(new("different-defaults", f.Session.SessionId, "inert input"));
        if (admission.Receipt is { } admitted) _ = f.Track(admitted.Completion);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
        var result = await f.Wait(admission.Receipt!.Completion);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("send_failed", result.Code, "Rejection must reach send admission without consuming preparation state.");
        Assert.AreEqual(before, await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId)));
        Assert.AreSame(original, f.Provider.Latest, "Do not silently replace another caller's coordinator.");
        Assert.AreEqual(1, f.Provider.AttachmentCount);
        Assert.IsNull(original.LastSend, "The provider must not receive an owned send on different defaults.");
        Assert.AreEqual(startedAt, f.Session.StartedAt);
        Assert.AreEqual(status, f.Session.Status);
        var observed = new List<SessionRuntimeEvent>();
        // Null run on the trailing marker cannot restore a run cleared by a faulty rejection path.
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, null, observed);
        Assert.IsFalse(observed.Any(value => value is SessionAgentEvent
            { Event: AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown } }));
        Assert.IsFalse(observed.Any(value => value is SessionLifecycleRuntimeEvent
            { Event.Kind: SessionLifecycleEventKind.RunFailed or SessionLifecycleEventKind.RunAborted
                or SessionLifecycleEventKind.RunCompleted or SessionLifecycleEventKind.RunSubmitted or SessionLifecycleEventKind.SessionStarted }));
        Assert.IsFalse(observed.Any(value => value is SessionCatalogRuntimeEvent { Session.StartedAt: not null }),
            "A rejected owned send must not publish a started catalog snapshot for its resolved descriptor.");
        var after = await f.Wait(f.Runtime.GetCurrentStateAsync(f.Session.SessionId));
        Assert.AreEqual(before, after with { Entry = after.Entry! with { Activity = before.Entry.Activity } });
        Assert.AreEqual(before.Entry.Activity!.AdmittedEvents + 1, after.Entry!.Activity!.AdmittedEvents,
            "The trailing warning marker is observed, without changing command/configuration state.");
    }, reviewPermissions: reviewPermissions);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task OwnedPermission_ReplacementAndDirectAbortInvalidateActualAttachment(bool replacement) => Fixture.Run(async f =>
    {
        var admission = f.Commands.AdmitSend(new("owned-permission", f.Session.SessionId, "inert fixture input"));
        if (admission.Receipt is { } admitted) _ = f.Track(admitted.Completion);
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
        var receipt = admission.Receipt!;
        await f.Ready(f.Provider.SendStarted.Task);
        var original = f.Provider.Latest;
        var callback = original.LastSend!.OnPermissionRequest;
        Assert.IsNotNull(callback);
        var request = new AgentCommandPermissionRequest(original.ProviderId, original.SessionId, DateTimeOffset.UtcNow,
            null, "pending", null, "inert command", original.WorkspacePath, null, null, null, null, null);
        var pending = f.Track(callback(request, CancellationToken.None));
        var handle = (await f.Wait(f.Runtime.Permissions.ListAsync().AsTask())).Single().Handle;
        original.AbortDependency = pending;
        f.Provider.HoldAbort = true;
        var control = f.Track(replacement
            ? f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("replacement-model"))
            : f.Runtime.AbortAsync(f.Session.SessionId));
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, (await f.Wait(pending)).Kind);
        await f.Ready(f.Provider.AbortStarted.Task);
        Assert.IsFalse(await f.Wait(f.Runtime.Permissions.ResolveAsync(handle, AgentPermissionDecisionKind.AllowOnce).AsTask()));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await f.Wait(callback(request, CancellationToken.None))).Kind);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(control);
        await f.Wait(receipt.Completion);
        if (replacement) Assert.AreNotSame(original, f.Provider.Latest);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    }, reviewPermissions: true);

    private sealed class HeldEmptyTools : IReadOnlyList<AgentToolDefinition>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count
        {
            get
            {
                Entered.TrySetResult();
                if (!Release.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture actor hold expired.");
                return 0;
            }
        }
        public AgentToolDefinition this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<AgentToolDefinition> GetEnumerator() => Enumerable.Empty<AgentToolDefinition>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly List<Task> _observers = [];
        private readonly List<Exception> _failures = [];
        private readonly List<Exception> _observed = [];
        private readonly HashSet<Exception> _expected = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-forwarding-" + Guid.NewGuid().ToString("N"));
        private CodeAltaHost? _host;
        private Task? _close;
        private Task? _setup;
        private Task? _body;
        private bool _cleaning;
        private string _projectId = string.Empty;
        internal string ProjectId => _projectId;
        internal string GlobalRoot => Path.Combine(_root, "global");
        internal string ProjectPath => Path.Combine(_root, "project");
        internal Provider Provider { get; } = new();
        internal SessionViewDescriptor Session { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionRuntimeService Runtime { get { lock (_gate) return _host!.RuntimeService; } }
        internal OwnedSessionCommandService Commands { get { lock (_gate) return _host!.Commands; } }
        internal SessionExecutionOptions Options => OptionsFor("fixture-model");
        internal SessionExecutionOptions OptionsFor(string model, IReadOnlyList<AgentToolDefinition>? tools = null, bool ownedDefaults = false) => new()
        {
            ProviderId = Provider.Descriptor.ProviderId,
            ProviderKey = Provider.Descriptor.ProviderId.Value,
            WorkingDirectory = Path.Combine(_root, "project"),
            Model = model,
            Tools = tools,
            ProjectRoots = [Path.Combine(_root, "project")],
            OnPermissionRequest = ownedDefaults ? Runtime.Permissions.OwnedDefaultPermissionHandler : static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            OnUserInputRequest = ownedDefaults ? Runtime.Permissions.OwnedDefaultUserInputHandler : static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true)),
        };

        internal static async Task Run(Func<Fixture, Task> body, bool reviewPermissions = false)
        {
            var f = new Fixture();
            try
            {
                await f.Wait(f.Start(() => f.Setup(reviewPermissions), setup: true));
                await f.Wait(f.Start(() => body(f), setup: false));
            }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                lock (f._gate) f._cleaning = true;
                f.Attempt(f.Provider.ReleaseAll);
                // Observe setup, body and available cleanup independently, even if setup times out.
                var producers = new List<Task>();
                if (f._setup is not null) producers.Add(f._setup);
                if (f._body is not null) producers.Add(f._body);
                f.Attempt(() => { if (f.HasHost) producers.Add(f.Close()); });
                await f.Drain(producers);
                var lateCleanup = new List<Task>();
                f.Attempt(() => { if (f.HasHost) lateCleanup.Add(f.Close()); });
                await f.Drain(lateCleanup);
                Task[] available;
                lock (f._gate) available = [.. f._work, .. f._observers];
                await f.Drain(available);
                bool settled;
                lock (f._gate) settled = (f._setup is null || f._setup.IsCompleted) && (f._body is null || f._body.IsCompleted) && (f._close is null || f._close.IsCompleted);
                if (settled)
                {
                    Task[] final;
                    lock (f._gate) final = [.. f._work, .. f._observers];
                    await f.Drain(final);
                }
                // Roots deliberately remain available for parent inspection. No fixture deletes data.
            }
            Exception[] failures;
            lock (f._gate) failures = f._failures.Concat(f._observed.Where(ex => !f._expected.Contains(ex))).Distinct().ToArray();
            if (failures.Length != 0)
            {
                var failure = new AggregateException("Forwarding fixture retained at " + f._root, failures);
                failure.Data["RetainedFixture"] = f;
                throw failure;
            }
        }

        private Task Start(Func<Task> operation, bool setup)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Track(Invoke());
            lock (_gate) { if (setup) _setup = work; else _body = work; }
            launch.TrySetResult();
            return work;
            async Task Invoke() { await launch.Task.ConfigureAwait(false); await operation().ConfigureAwait(false); }
        }

        private bool HasHost { get { lock (_gate) return _host is not null; } }
        private void Attempt(Action action) { try { action(); } catch (Exception ex) { lock (_gate) _failures.Add(ex); } }

        internal async Task<T> Expect<T>(Task task) where T : Exception
        {
            var error = await Assert.ThrowsExactlyAsync<T>(() => task);
            lock (_gate) _expected.Add(error);
            return error;
        }

        internal void Expected(Exception error) { lock (_gate) _expected.Add(error); }
        internal async Task ExpectCancellation(Task task)
            => Expected(await Assert.ThrowsAsync<OperationCanceledException>(() => task));

        internal async Task EmitAndObserve(AgentSessionUpdateKind kind, AgentRunId? runId, List<SessionRuntimeEvent>? observed = null,
            AgentSessionUsage? usage = null, DateTimeOffset? timestamp = null, string? eventSessionId = null,
            ModelProviderId? eventProviderId = null)
        {
            using var cancellation = new CancellationTokenSource();
            var marker = Guid.NewGuid().ToString();
            Task observation = Task.CompletedTask;
            try
            {
                Provider.Latest.EmitState(kind, runId, marker, usage, timestamp, eventSessionId, eventProviderId);
                observation = Track(Observe());
                await Wait(observation);
            }
            catch (Exception ex)
            {
                // Preserve the bounded observer's failure even if cancellation/iterator cleanup
                // subsequently faults. A timeout is never permission to release the source.
                lock (_gate) _failures.Add(ex);
                throw;
            }
            finally
            {
                Task traversal;
                try { traversal = Track(cancellation.CancelAsync()); }
                catch (Exception ex) { traversal = Track(Task.FromException(ex)); }
                // Initiate cancellation before dependent joins. Neither join has a timeout:
                // the outer fixture retains this work/source/root if actual cleanup stays pending.
                await Track(Task.WhenAll(observation, traversal));
            }
            async Task Observe()
            {
                await foreach (var value in Runtime.StreamEventsAsync(cancellation.Token))
                {
                    observed?.Add(value);
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent update } && update.Message == marker) return;
                }
                Assert.Fail("The actual runtime did not forward the fixture state event.");
            }
        }

        private async Task Setup(bool reviewPermissions)
        {
            for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { home, global, project, builtin }) Directory.CreateDirectory(path);
            // Close the existing builtin Glob/Git configuration lookup to this task-owned root.
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            // SystemPromptContentLocator maps GlobalRoot/prompts; the builder requires a named
            // replacement resource. Default system content remains the existing shipped resource.
            var prompts = Path.Combine(global, "prompts", "agents");
            Directory.CreateDirectory(prompts);
            File.WriteAllText(Path.Combine(prompts, "plan.prompt.md"), "---\nname: plan\ndescription: Task-owned forwarding fixture.\nmode: replace\nsystem: default\n---\nFixture plan instructions.\n");
            var catalog = new CatalogOptions { GlobalRoot = global };
            _projectId = (await Track(new ProjectCatalog(catalog).UpsertFromPathAsync(project))).Id;
            Journal = new SessionViewJournalStore(catalog);
            Session = NewSession();
            await Persist(Session);
            var creation = Track(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, _root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ReviewOwnedCommandPermissions = reviewPermissions,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime),
            }));
            var host = await creation;
            bool close;
            lock (_gate) { _host = host; close = _cleaning; }
            if (close) await Close();
        }

        internal SessionViewDescriptor NewSession() => new()
        {
            SessionId = Guid.CreateVersion7().ToString(), Kind = SessionViewKind.ProjectSession,
            ProjectRef = _projectId, ProviderId = Provider.Descriptor.ProviderId.Value,
            ProviderKey = Provider.Descriptor.ProviderId.Value, WorkingDirectory = Path.Combine(_root, "project"),
            Title = "Forwarding fixture", ModelId = "fixture-model", AgentPromptId = "default",
            CreatedAt = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        };

        internal async Task Persist(SessionViewDescriptor session)
        {
            var workingDirectory = session.WorkingDirectory;
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            var providerKey = session.ProviderKey;
            ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
            await Track(Journal.EnsureHeaderAsync(session));
            var store = Journal.CreateSessionStore();
            await Track(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId, ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = providerKey, WorkingDirectory = workingDirectory,
                Title = session.Title, CreatedAt = session.CreatedAt, UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId, AgentPromptId = session.AgentPromptId,
            }));
            await Track(Journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = providerKey, ModelId = session.ModelId,
                AgentPromptId = session.AgentPromptId, ParentSessionId = session.ParentSessionId,
            }));
            var readback = await Track(store.GetSessionAsync(session.SessionId));
            Assert.IsNotNull(readback);
            Assert.AreEqual(session.SessionId, readback.SessionId);
            Assert.AreEqual(session.WorkingDirectory, readback.WorkspacePath);
            Assert.AreEqual(session.ParentSessionId, readback.ViewState?.ParentSessionId);
        }

        internal Task Track(Task task)
        {
            lock (_gate) { _work.Add(task); _observers.Add(Observe(task)); }
            return task;
        }
        internal Task<T> Track<T>(Task<T> task) { Track((Task)task); return task; }
        internal Task Wait(Task task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task Ready(Task readiness) => Track(readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        private async Task Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _observed.Add(ex); }
        }
        private Task Close()
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task close;
            CodeAltaHost host;
            lock (_gate)
            {
                if (_close is not null) return _close;
                host = _host!;
                close = Track(Dispose());
                _close = close;
            }
            launch.TrySetResult();
            return close;
            async Task Dispose() { await launch.Task.ConfigureAwait(false); await host.DisposeAsync().ConfigureAwait(false); }
        }
        private async Task Drain(IEnumerable<Task> work)
        {
            var waits = work.Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            var observers = waits.Select(ObserveCleanup).ToArray();
            foreach (var observer in observers) await observer.ConfigureAwait(false);
        }

        private async Task ObserveCleanup(Task wait)
        {
            try { await wait.ConfigureAwait(false); }
            catch (TimeoutException ex) { lock (_gate) _failures.Add(ex); }
            catch { /* Original observers retain all other errors; Expect confirms exact instances. */ }
        }
    }

    private sealed class Provider
    {
        private readonly object _gate = new();
        private readonly List<Session> _sessions = [];
        private int _sends, _creates, _compactions, _historyReads, _earlyDisposals, _subscriptionsDisposed;
        private bool _cleaning, _holdPreparation, _holdAbort;
        internal bool HoldPreparation { get { lock (_gate) return _holdPreparation; } set { lock (_gate) _holdPreparation = !_cleaning && value; } }
        internal bool HoldAbort { get { lock (_gate) return _holdAbort; } set { lock (_gate) _holdAbort = !_cleaning && value; } }
        internal Action? BeforeSubscriptionReturn { get; set; }
        internal TaskCompletionSource PreparationStarted { get; } = NewGate();
        internal TaskCompletionSource ReplacementPreparationStarted { get; } = NewGate();
        internal TaskCompletionSource ReleasePreparation { get; } = NewGate();
        internal TaskCompletionSource SendStarted { get; } = NewGate();
        internal TaskCompletionSource SecondSendStarted { get; } = NewGate();
        internal TaskCompletionSource ReleaseSend { get; } = NewGate();
        internal TaskCompletionSource AbortStarted { get; } = NewGate();
        internal TaskCompletionSource ReleaseAbort { get; } = NewGate();
        internal TaskCompletionSource SteerStarted { get; } = NewGate();
        internal TaskCompletionSource SteerCancelled { get; } = NewGate();
        internal TaskCompletionSource ReleaseSteer { get; } = NewGate();
        private int _steers;
        internal int Steers => Volatile.Read(ref _steers);
        internal bool HoldIdleCompact { get; set; }
        internal TaskCompletionSource IdleCompactStarted { get; } = NewGate();
        internal TaskCompletionSource IdleCompactCancelled { get; } = NewGate();
        internal TaskCompletionSource ReleaseIdleCompact { get; } = NewGate();
        private int _idleCompactions;
        internal int IdleCompactions => Volatile.Read(ref _idleCompactions);
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("forwarding-fixture"), "Forwarding fixture") { DefaultModelId = "fixture-model" };
        internal Session Latest { get { lock (_gate) return _sessions[^1]; } }
        internal int AttachmentCount { get { lock (_gate) return _sessions.Count; } }
        internal string?[] AttachmentModels { get { lock (_gate) return _sessions.Select(session => session.Options.Model).ToArray(); } }
        internal int Creates => Volatile.Read(ref _creates);
        internal int Compactions => Volatile.Read(ref _compactions);
        internal int HistoryReads => Volatile.Read(ref _historyReads);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal int SubscriptionsDisposed => Volatile.Read(ref _subscriptionsDisposed);
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);
        internal void ReleaseAll()
        {
            lock (_gate) { _cleaning = true; _holdPreparation = false; _holdAbort = false; }
            ReleasePreparation.TrySetResult(); ReleaseSend.TrySetResult(); ReleaseAbort.TrySetResult();
            ReleaseSteer.TrySetResult();
            ReleaseIdleCompact.TrySetResult();
        }

        private sealed class Runtime(Provider owner) : IModelProviderSessionRuntime
        {
            private Session? _session;
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe forbidden.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Turn executor forbidden.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._creates); return Prepare(options.SessionId!, options); }
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => Prepare(sessionId, options);
            private async Task<IAgentSession> Prepare(string id, AgentSessionCreateOptions options)
            {
                owner.PreparationStarted.TrySetResult();
                if (owner.AttachmentCount > 0) owner.ReplacementPreparationStarted.TrySetResult();
                if (owner.HoldPreparation) await owner.ReleasePreparation.Task.ConfigureAwait(false);
                _session = new Session(owner, id, options);
                lock (owner._gate) owner._sessions.Add(_session);
                return _session;
            }
            public ValueTask DisposeAsync()
            {
                if (_session?.Active != 0 && _session is not null) Interlocked.Increment(ref owner._earlyDisposals);
                return ValueTask.CompletedTask;
            }
        }

        internal sealed class Session(Provider owner, string id, AgentSessionCreateOptions options) : IAgentSession, IAgentIdleCompactionProvider
        {
            private readonly object _gate = new();
            private Action<AgentEvent>? _handler;
            internal Action<AgentEvent> CapturedCallback { get { lock (_gate) return _handler!; } }
            private int _active;
            internal int Active => Volatile.Read(ref _active);
            internal TaskCompletionSource SendStarted { get; } = NewGate();
            internal TaskCompletionSource SendFinished { get; } = NewGate();
            internal AgentSessionCreateOptions Options => options;
            internal AgentSendOptions? LastSend { get; private set; }
            internal Task? AbortDependency { get; set; }
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => id;
            public string? WorkspacePath => options.WorkingDirectory;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                lock (_gate) _handler = handler;
                EmitIdle(); // Deliberately synchronous and before the receipt is returned.
                owner.BeforeSubscriptionReturn?.Invoke();
                return new Subscription(this);
            }
            private void Emit(AgentEvent value) { Action<AgentEvent>? handler; lock (_gate) handler = _handler; handler?.Invoke(value); }
            internal void EmitIdle() => Emit(new AgentSessionUpdateEvent(ProviderId, id, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, "fixture"));
            internal void EmitState(AgentSessionUpdateKind kind, AgentRunId? runId, string message,
                AgentSessionUsage? usage = null, DateTimeOffset? timestamp = null, string? eventSessionId = null,
                ModelProviderId? eventProviderId = null)
                => Emit(new AgentSessionUpdateEvent(eventProviderId ?? ProviderId, eventSessionId ?? id,
                    timestamp ?? DateTimeOffset.UtcNow, runId, kind, message, Usage: usage));
            internal void EmitNotification(string content) => Emit(new AgentContentCompletedEvent(ProviderId, id, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, Guid.NewGuid().ToString(), null, "<notify-parent>" + content + "</notify-parent>"));
            public async Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _active);
                try
                {
                    var count = Interlocked.Increment(ref owner._sends);
                    LastSend = send;
                    (count == 1 ? owner.SendStarted : owner.SecondSendStarted).TrySetResult();
                    SendStarted.TrySetResult();
                    // Abort, not cancellation alone, releases this controlled provider operation.
                    await owner.ReleaseSend.Task.ConfigureAwait(false);
                    EmitIdle();
                    return new AgentRunId("fixture-" + count);
                }
                finally { Interlocked.Decrement(ref _active); SendFinished.TrySetResult(); }
            }
            public async Task AbortAsync(CancellationToken cancellationToken = default)
            {
                if (AbortDependency is { } dependency) await dependency.ConfigureAwait(false);
                owner.AbortStarted.TrySetResult();
                if (owner.HoldAbort) await owner.ReleaseAbort.Task.ConfigureAwait(false);
                owner.ReleaseSend.TrySetResult();
            }
            public async Task<AgentRunId> SteerAsync(AgentSteerOptions steer, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _active);
                try
                {
                    Interlocked.Increment(ref owner._steers);
                    using var registration = cancellationToken.Register(() => owner.SteerCancelled.TrySetResult());
                    owner.SteerStarted.TrySetResult();
                    await owner.ReleaseSteer.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return steer.ExpectedRunId ?? new AgentRunId("steered");
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            public Task CompactAsync(CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._compactions); EmitIdle(); return Task.CompletedTask; }
            public async Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _active);
                try
                {
                    Interlocked.Increment(ref owner._idleCompactions);
                    using var readiness = cancellationToken.Register(() => owner.IdleCompactCancelled.TrySetResult());
                    owner.IdleCompactStarted.TrySetResult();
                    if (owner.HoldIdleCompact) await owner.ReleaseIdleCompact.Task.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(true, "inert compaction settled");
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._historyReads); return Task.FromResult<IReadOnlyList<AgentEvent>>([]); }
            public ValueTask DisposeAsync()
            { if (Active != 0) Interlocked.Increment(ref owner._earlyDisposals); return ValueTask.CompletedTask; }
            private void Unsubscribe()
            {
                lock (_gate) _handler = null;
                Interlocked.Increment(ref owner._subscriptionsDisposed);
            }
            private sealed class Subscription(Session session) : IDisposable
            { public void Dispose() => session.Unsubscribe(); }
        }
    }
}
