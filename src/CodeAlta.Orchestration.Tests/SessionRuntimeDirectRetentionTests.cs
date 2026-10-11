using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

public sealed partial class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    public Task DirectRetention_OriginalSiblingGraphPreventsClaimRelease() => Fixture.Run(async f =>
    {
        var ordinary = new IOException("first sibling is not retention");
        var retention = new AgentDependencyRetentionException("fixture", "retained sibling", [new IOException("unconfirmed")], f.Provider);
        var original = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = typeof(SessionRuntimeService);
        // The same original-task boundary is used before steer/compact/history release or fallback.
        Task<int> Body() => (Task<int>)service.GetMethod("AwaitRetentionAwareAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int)).Invoke(null, [original.Task])!;
        var invocation = (Task<int>)service.GetMethod("WithRetentionWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int)).Invoke(f.Runtime, [new Func<Task<int>>(Body), new[] { f.Session.SessionId }])!;
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        original.SetException([ordinary, retention]);
        var failure = await f.Expect<AggregateException>(f.Track(invocation));
        var graph = (AggregateException)failure.InnerExceptions.Single();
        CollectionAssert.AreEquivalent(new Exception[] { ordinary, retention }, graph.InnerExceptions.ToArray());
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        var closeFailure = await Assert.ThrowsAsync<Exception>(() => f.Wait(f.Close()));
        f.Expected(closeFailure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(closeFailure));
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task HostRetention_AttachedOnlyLineageProtectsDistinctSource(bool retirementWins) => Fixture.Run(async f =>
    {
        var target = f.NewSession();
        var parent = Guid.NewGuid().ToString("D");
        var source = Guid.NewGuid().ToString("D");
        target.ParentSessionId = parent;
        target.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(target, f.Options));
        f.Provider.Latest.EmitState(AgentSessionUpdateKind.Warning, new AgentRunId("attached-host"), "active");
        Assert.AreEqual("attached-host", (await f.Wait(f.Runtime.GetCurrentStateAsync(target.SessionId))).Entry!.ActiveRunId);
        // Evict only this synthetic fixture's public journal/cache; its attached original remains live.
        Assert.IsTrue(await f.Wait(f.Journal.CreateSessionStore().DeleteSessionAsync(target.SessionId)));
        Assert.IsNull(await f.Wait(f.Journal.CreateSessionStore().GetSessionAsync(target.SessionId)), "Force attached-entry fallback, not cached metadata.");
        using var retired = retirementWins ? TryClaimParentRetirement(f.Runtime, source) : null;
        var original = f.Track(f.Runtime.DeliverHostPromptAsync(target.SessionId, "inert attached host", "fixture"));
        if (retirementWins)
        {
            Assert.IsNotNull(retired);
            await f.Expect<InvalidOperationException>(original);
            Assert.AreEqual(0, f.Provider.Steers);
            return;
        }
        await f.Ready(f.Provider.SteerStarted.Task);
        AssertParentRetirementBlocked(f.Runtime, target.SessionId);
        AssertParentRetirementBlocked(f.Runtime, parent);
        AssertParentRetirementBlocked(f.Runtime, source);
        f.Provider.ReleaseSteer.TrySetResult();
        Assert.AreEqual("steered", await f.Wait(original));
        using var released = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task DirectRetention_PreparationAndCanceledWaitKeepLineage(bool history) => Fixture.Run(async f =>
    {
        var parent = Guid.NewGuid().ToString("D");
        var source = Guid.NewGuid().ToString("D");
        f.Session.ParentSessionId = parent;
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        f.Provider.HoldPreparation = true;
        using var cancellation = new CancellationTokenSource();
        var waiter = f.Track(history
            ? f.Runtime.GetOrResumeHistoryAsync(f.Session, f.Options, cancellation.Token)
            : f.Runtime.CompactAsync(f.Session, f.Options, cancellation.Token));
        await f.Ready(f.Provider.PreparationStarted.Task);
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        AssertParentRetirementBlocked(f.Runtime, parent);
        AssertParentRetirementBlocked(f.Runtime, source);
        cancellation.Cancel();
        await f.ExpectCancellation(waiter);
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        AssertParentRetirementBlocked(f.Runtime, parent);
        var close = f.Track(f.Runtime.DisposeAsync().AsTask());
        Assert.IsFalse(close.IsCompleted);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Wait(close);
        using var released = TryClaimParentRetirement(f.Runtime, f.Session.SessionId);
        using var releasedParent = TryClaimParentRetirement(f.Runtime, parent);
        using var releasedSource = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
        Assert.IsNotNull(releasedParent);
        Assert.IsNotNull(releasedSource);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    [DataRow("steer")]
    [DataRow("host")]
    [DataRow("running-host")]
    public Task DirectRetention_CanceledObserverKeepsOriginalWorker(string route) => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.EmitAndObserve(AgentSessionUpdateKind.Warning, new AgentRunId("retention-worker"));
        Task original = f.Track(route switch
        {
            "steer" => f.Runtime.SteerAsync(f.Session, f.Options, new AgentSteerOptions { Input = AgentInput.Text("inert steer") }),
            "host" => f.Runtime.DeliverHostPromptAsync(f.Session.SessionId, "inert host prompt", "fixture"),
            _ => f.Runtime.DeliverHostPromptToRunningTurnAsync(f.Session.SessionId, "inert host prompt", "fixture"),
        });
        await f.Ready(f.Provider.SteerStarted.Task);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await f.ExpectCancellation(original.WaitAsync(cancellation.Token));
        Assert.IsFalse(original.IsCompleted);
        AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
        f.Provider.ReleaseSteer.TrySetResult();
        await f.Wait(original);
        using var released = TryClaimParentRetirement(f.Runtime, f.Session.SessionId);
        Assert.IsNotNull(released);
        Assert.AreEqual(1, f.Provider.Steers);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task HostRetention_ClaimsBeforeMissingTargetResolutionAndReleasesTerminalAbsence(bool runningOnly) => Fixture.Run(async f =>
    {
        var absent = Guid.NewGuid().ToString("D");
        Task<string> Deliver() => runningOnly
            ? f.Runtime.DeliverHostPromptToRunningTurnAsync(absent, "inert", "fixture")
            : f.Runtime.DeliverHostPromptAsync(absent, "inert", "fixture");
        using (var retired = TryClaimParentRetirement(f.Runtime, absent))
        {
            Assert.IsNotNull(retired);
            await f.Expect<InvalidOperationException>(f.Track(Deliver()));
        }
        Assert.AreEqual("missing_session", await f.Wait(Deliver()));
        using var released = TryClaimParentRetirement(f.Runtime, absent);
        Assert.IsNotNull(released);
        Assert.AreEqual(0, f.Provider.Creates);
    });
}
