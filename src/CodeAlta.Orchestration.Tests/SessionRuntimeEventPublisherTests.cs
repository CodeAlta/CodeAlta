using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>In-memory original-reader admission tests; no host, provider, discovery or filesystem fixtures.</summary>
[TestClass]
public sealed class SessionRuntimeEventPublisherTests
{
    [TestMethod]
    public async Task CompetingReader_IsRejectedWithoutStealingOrReleasingIncumbent()
    {
        var publisher = new SessionRuntimeEventPublisher(3);
        var events = new[] { Event("first"), Event("second"), Event("third") };
        foreach (var value in events) Assert.IsTrue(publisher.TryPublish(value));
        // Complete up front: even the unfixed competing reader has a finite buffered read.
        publisher.Complete();
        await using var first = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await Move(first));
        Assert.AreSame(events[0], first.Current);
        await using (var competitor = publisher.ReadAllAsync().GetAsyncEnumerator())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                var moved = await Move(competitor);
                Console.WriteLine($"Competing reader stole second original: {moved && ReferenceEquals(events[1], competitor.Current)}");
            });
        // Disposal of a rejected iterator must not clear somebody else's claim.
        await using (var another = publisher.ReadAllAsync().GetAsyncEnumerator())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(another));
        foreach (var value in events.Skip(1))
        {
            Assert.IsTrue(await Move(first));
            Assert.AreSame(value, first.Current);
        }
        Assert.IsFalse(await Move(first));
        Assert.AreEqual(0L, publisher.DroppedCount);
    }

    [TestMethod]
    public async Task UnstartedEnumerators_NeitherClaimNorReleaseAdmission()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var sequence = publisher.ReadAllAsync();
        await using var unstarted = sequence.GetAsyncEnumerator();
        await using var unused = sequence.GetAsyncEnumerator();
        var value = Event("retained");
        publisher.TryPublish(value);
        publisher.Complete();
        await using var active = sequence.GetAsyncEnumerator();
        Assert.IsTrue(await Move(active));
        Assert.AreSame(value, active.Current);
        await unused.DisposeAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(unstarted));
        Assert.IsFalse(await Move(active));
        await using var successor = sequence.GetAsyncEnumerator();
        Assert.IsFalse(await Move(successor));
    }

    [TestMethod]
    public async Task DisposingIncumbent_AllowsSuccessorWithoutReplayOrBufferLoss()
    {
        var publisher = new SessionRuntimeEventPublisher(2);
        var firstEvent = Event("first");
        var secondEvent = Event("second");
        publisher.TryPublish(firstEvent);
        publisher.TryPublish(secondEvent);
        await using (var first = publisher.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.IsTrue(await Move(first));
            Assert.AreSame(firstEvent, first.Current);
        }
        Assert.IsFalse(publisher.Display.GetSnapshot().IsClosed);
        publisher.Complete();
        await using var successor = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await Move(successor));
        Assert.AreSame(secondEvent, successor.Current);
        Assert.IsFalse(await Move(successor));
    }

    [TestMethod]
    public async Task Completion_DoesNotReleaseReaderWhileBufferedEventsAreDraining()
    {
        var publisher = new SessionRuntimeEventPublisher(2);
        var firstEvent = Event("first");
        var secondEvent = Event("second");
        publisher.TryPublish(firstEvent);
        publisher.TryPublish(secondEvent);
        await using var first = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await Move(first));
        publisher.Complete();
        await using (var competitor = publisher.ReadAllAsync().GetAsyncEnumerator())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(competitor));
        Assert.IsTrue(await Move(first));
        Assert.AreSame(secondEvent, first.Current);
        // Even after the last value is yielded, ownership lasts until terminal MoveNext/disposal.
        await using (var competitor = publisher.ReadAllAsync().GetAsyncEnumerator())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(competitor));
        Assert.IsFalse(await Move(first));
        await using var successor = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsFalse(await Move(successor));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreCanceledAdmission_DoesNotClaimOrConsume(bool tokenOnEnumerator)
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        var value = Event("retained");
        publisher.TryPublish(value);
        publisher.Complete();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var canceled = tokenOnEnumerator
            ? publisher.ReadAllAsync().GetAsyncEnumerator(cancellation.Token)
            : publisher.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Move(canceled));
        await using var successor = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await Move(successor));
        Assert.AreSame(value, successor.Current);
        Assert.IsFalse(await Move(successor));
    }

    [TestMethod]
    public async Task PendingCancellation_ReleasesOnlyAfterEnumerationTerminates()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        using var cancellation = new CancellationTokenSource();
        await using var first = publisher.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        try
        {
            Assert.IsFalse(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var value = Event("successor");
            Assert.IsTrue(publisher.TryPublish(value));
            publisher.Complete();
            await using var successor = publisher.ReadAllAsync().GetAsyncEnumerator();
            Assert.IsTrue(await Move(successor));
            Assert.AreSame(value, successor.Current);
            Assert.IsFalse(await Move(successor));
        }
        finally
        {
            cancellation.Cancel();
            publisher.Complete();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuspendedCancellation_KeepsClaimAndUnderlyingBufferedReadSemantics(bool drainBuffered)
    {
        var publisher = new SessionRuntimeEventPublisher(2);
        var firstEvent = Event("first");
        var buffered = Event("buffered");
        publisher.TryPublish(firstEvent);
        publisher.TryPublish(buffered);
        using var cancellation = new CancellationTokenSource();
        await using var first = publisher.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        try
        {
            Assert.IsTrue(await Move(first));
            cancellation.Cancel();
            await using (var competitor = publisher.ReadAllAsync().GetAsyncEnumerator())
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(competitor));
            if (drainBuffered)
            {
                // Channel.ReadAllAsync may yield buffered values after cancellation. Do not add
                // a per-item token check that would consume then drop one of those original objects.
                Assert.IsTrue(await Move(first));
                Assert.AreSame(buffered, first.Current);
                await Assert.ThrowsAsync<OperationCanceledException>(() => Move(first));
            }
            else
            {
                await first.DisposeAsync();
            }
            var next = Event("next");
            Assert.IsTrue(publisher.TryPublish(next));
            publisher.Complete();
            await using var successor = publisher.ReadAllAsync().GetAsyncEnumerator();
            if (!drainBuffered)
            {
                Assert.IsTrue(await Move(successor));
                Assert.AreSame(buffered, successor.Current);
            }
            Assert.IsTrue(await Move(successor));
            Assert.AreSame(next, successor.Current);
            Assert.IsFalse(await Move(successor));
            Assert.AreEqual(0L, publisher.DroppedCount);
        }
        finally { publisher.Complete(); }
    }

    [TestMethod]
    public async Task ConcurrentClaims_AdmitExactlyOneReader()
    {
        var publisher = new SessionRuntimeEventPublisher(2);
        var firstEvent = Event("first");
        var secondEvent = Event("second");
        publisher.TryPublish(firstEvent);
        publisher.TryPublish(secondEvent);
        publisher.Complete();
        await using var first = publisher.ReadAllAsync().GetAsyncEnumerator();
        await using var second = publisher.ReadAllAsync().GetAsyncEnumerator();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(() => Claim(first));
        var b = Task.Run(() => Claim(second));
        start.SetResult();
        var outcomes = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, outcomes.Count(admitted => admitted));
        var winner = outcomes[0] ? first : second;
        Assert.AreSame(firstEvent, winner.Current);
        Assert.IsTrue(await Move(winner));
        Assert.AreSame(secondEvent, winner.Current);
        Assert.IsFalse(await Move(winner));

        async Task<bool> Claim(IAsyncEnumerator<SessionRuntimeEvent> reader)
        {
            await start.Task;
            try { return await Move(reader); }
            catch (InvalidOperationException) { return false; }
        }
    }

    [TestMethod]
    public async Task DisplayObservers_CoexistWithoutChangingOriginalDropsOrDelivery()
    {
        var publisher = new SessionRuntimeEventPublisher(1);
        await using var displayA = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        await using var displayB = publisher.Display.ObserveAsync().GetAsyncEnumerator();
        Assert.IsTrue(await displayA.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(await displayB.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var firstEvent = Event("first");
        var secondEvent = Event("second");
        var dropped = Event("dropped original, committed display");
        publisher.TryPublish(firstEvent);
        await using var original = publisher.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await Move(original));
        Assert.AreSame(firstEvent, original.Current);
        Assert.IsTrue(publisher.TryPublish(secondEvent));
        Assert.IsFalse(publisher.TryPublish(dropped));
        Assert.AreEqual(1L, publisher.DroppedCount);
        publisher.Complete();
        foreach (var display in new[] { displayA, displayB })
        {
            Assert.IsTrue(await display.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(display.Current.HasGap && display.Current.Snapshot.IsClosed);
            Assert.AreEqual(dropped.Message, display.Current.Snapshot.Sessions.Single().StatusMessage);
            Assert.IsFalse(await display.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await using (var competitor = publisher.ReadAllAsync().GetAsyncEnumerator())
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Move(competitor));
        Assert.IsTrue(await Move(original));
        Assert.AreSame(secondEvent, original.Current);
        Assert.IsFalse(await Move(original));
        Assert.AreEqual(1L, publisher.DroppedCount);
    }

    private static Task<bool> Move(IAsyncEnumerator<SessionRuntimeEvent> reader)
        => reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static SessionHostEvent Event(string message)
        => new("session", DateTimeOffset.UtcNow, AgentSessionUpdateKind.Warning, message);
}
