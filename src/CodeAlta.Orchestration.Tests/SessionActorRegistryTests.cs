using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionActorRegistryTests
{
    [TestMethod]
    public async Task GetOrCreate_FromRacingCallers_MakesOneActorPerSession()
    {
        await using var registry = new SessionActorRegistry(mailboxCapacity: 2);
        using var start = new Barrier(16);
        var actors = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return registry.GetOrCreate("Session-1");
        })));

        Assert.AreEqual(1, actors.Distinct().Count());
        Assert.AreEqual(1, registry.Count);
        Assert.AreSame(actors[0], registry.GetOrCreate("session-1"), "Session identities are matched without case.");
    }

    [TestMethod]
    public async Task SessionActor_SerializesCommandsForSameSession()
    {
        await using var actor = new SessionActor("session-1", mailboxCapacity: 4);
        var observed = new List<int>();

        var first = actor.ExecuteAsync(async _ =>
        {
            observed.Add(1);
            await Task.Delay(25).ConfigureAwait(false);
            observed.Add(2);
        }).AsTask();
        var second = actor.ExecuteAsync(_ =>
        {
            observed.Add(3);
            return ValueTask.CompletedTask;
        }).AsTask();

        await Task.WhenAll(first, second);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, observed);
    }

    [TestMethod]
    public async Task SessionActorRegistry_AllowsDifferentSessionActorsToRunConcurrently()
    {
        await using var registry = new SessionActorRegistry(mailboxCapacity: 2);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = registry.GetOrCreate("session-1").ExecuteAsync(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task.ConfigureAwait(false);
        }).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = registry.GetOrCreate("session-2").ExecuteAsync(_ =>
        {
            secondStarted.SetResult();
            return ValueTask.CompletedTask;
        }).AsTask();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(first.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
    }

    [TestMethod]
    public async Task SessionActor_RoutesAsyncCallbacksBackThroughMailbox()
    {
        await using var actor = new SessionActor("session-1", mailboxCapacity: 4);
        var observed = new List<int>();
        var callbackQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await actor.ExecuteAsync(cancellationToken =>
        {
            observed.Add(1);
            _ = cancellationToken;
            _ = Task.Run(async () =>
            {
                await actor.ExecuteAsync(_ =>
                {
                    observed.Add(3);
                    return ValueTask.CompletedTask;
                });
                callbackQueued.SetResult();
            });
            observed.Add(2);
            return ValueTask.CompletedTask;
        });

        await callbackQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, observed);
    }

    [TestMethod]
    public async Task SessionActor_ReportsRecoverableFailuresAsCommandResults()
    {
        await using var actor = new SessionActor("session-1", mailboxCapacity: 2);
        var events = new List<SessionActorSupervisorEvent>();
        actor.SupervisorEventEmitted += (_, evt) => events.Add(evt);

        var result = await actor.ExecuteAsync(_ => throw new InvalidOperationException("recoverable"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Exception);
        Assert.AreEqual("recoverable", result.Message);
        Assert.AreEqual(SessionActorSupervisorDecision.FailCommand, events.Single().Decision);
    }

    [TestMethod]
    public async Task SessionActor_StopActorSupervisorDecisionStopsActor()
    {
        await using var actor = new SessionActor(
            "session-1",
            mailboxCapacity: 2,
            _ => SessionActorSupervisorDecision.StopActor);
        var events = new List<SessionActorSupervisorEvent>();
        actor.SupervisorEventEmitted += (_, evt) => events.Add(evt);

        var result = await actor.ExecuteAsync(_ => throw new InvalidOperationException("fatal"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(SessionActorSupervisorDecision.StopActor, events.Single().Decision);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await actor.PostAsync(_ => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task SessionActorRegistry_RemoveAsyncStopsAndRemovesActor()
    {
        await using var registry = new SessionActorRegistry(mailboxCapacity: 2);
        var actor = registry.GetOrCreate("session-1");

        var removed = await registry.RemoveAsync("session-1", cancelPending: true);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, registry.Count);
        Assert.IsFalse(registry.TryGet("session-1", out _));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await actor.PostAsync(_ => ValueTask.CompletedTask));
    }
}
