using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexSubscriptionConcurrencyLimiterTests
{
    // Tells a hang from a slow machine: the waits below end as soon as the limiter does its part.
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task AccountWaiters_AreAdmittedInTheOrderTheyCame_OneByRelease()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        var holder = await AcquireAsync(limiter, "holder", accountId, limit: 1);

        // With no wait callback, a request that finds the account full is queued before AcquireAsync returns.
        var waiters = new List<Task<IAsyncDisposable>>();
        for (var index = 0; index < 5; index++)
        {
            waiters.Add(AcquireAsync(limiter, "waiter-" + index, accountId, limit: 1));
            Assert.AreEqual(index + 1, limiter.GetAccountWaiterCount(accountId));
        }

        var lease = holder;
        for (var index = 0; index < waiters.Count; index++)
        {
            await lease.DisposeAsync();
            lease = await waiters[index].WaitAsync(Hang);
            // The release admitted the oldest waiter and no other.
            Assert.IsTrue(waiters.Skip(index + 1).All(static waiter => !waiter.IsCompleted));
            Assert.AreEqual(waiters.Count - index - 1, limiter.GetAccountWaiterCount(accountId));
        }

        await lease.DisposeAsync();
        await (await AcquireAsync(limiter, "after", accountId, limit: 1).WaitAsync(Hang)).DisposeAsync();
    }

    [TestMethod]
    public async Task CancelledAccountWaiter_LeavesTheQueue_AndLosesNoSlot()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        var holder = await AcquireAsync(limiter, "holder", accountId, limit: 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = AcquireAsync(limiter, "cancelled", accountId, limit: 1, cancellation.Token);
        var next = AcquireAsync(limiter, "next", accountId, limit: 1);

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(Hang));
        Assert.AreEqual(1, limiter.GetAccountWaiterCount(accountId));

        await holder.DisposeAsync();
        var lease = await next.WaitAsync(Hang);
        await lease.DisposeAsync();

        // The slot the cancelled waiter never took is still there, and only it.
        var first = AcquireAsync(limiter, "first", accountId, limit: 1);
        Assert.IsTrue(first.IsCompletedSuccessfully);
        var second = AcquireAsync(limiter, "second", accountId, limit: 1);
        Assert.IsFalse(second.IsCompleted);
        await (await first).DisposeAsync();
        await (await second.WaitAsync(Hang)).DisposeAsync();
        Assert.AreEqual(0, limiter.SessionGateCount);
    }

    [TestMethod]
    public async Task CancellationRacingARelease_KeepsEverySlotOfTheAccount()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var first = await AcquireAsync(limiter, "first", accountId, limit: 2);
            var second = await AcquireAsync(limiter, "second", accountId, limit: 2);
            using var cancellation = new CancellationTokenSource();
            var waiter = AcquireAsync(limiter, "waiter", accountId, limit: 2, cancellation.Token);
            Assert.IsFalse(waiter.IsCompleted);

            using var start = new Barrier(2);
            var cancel = Task.Run(() =>
            {
                start.SignalAndWait();
                cancellation.Cancel();
            });
            var release = Task.Run(async () =>
            {
                start.SignalAndWait();
                await first.DisposeAsync();
            });
            await Task.WhenAll(cancel, release).WaitAsync(Hang);

            try
            {
                // Admitted before it was cancelled: the waiter owns the slot and gives it back.
                await (await waiter.WaitAsync(Hang)).DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }

            await second.DisposeAsync();
            Assert.AreEqual(0, limiter.GetAccountWaiterCount(accountId));
        }

        // Exactly the two slots of the account remain.
        var a = AcquireAsync(limiter, "a", accountId, limit: 2);
        var b = AcquireAsync(limiter, "b", accountId, limit: 2);
        var c = AcquireAsync(limiter, "c", accountId, limit: 2);
        Assert.IsTrue(a.IsCompletedSuccessfully);
        Assert.IsTrue(b.IsCompletedSuccessfully);
        Assert.IsFalse(c.IsCompleted);
        await (await a).DisposeAsync();
        await (await c.WaitAsync(Hang)).DisposeAsync();
        await (await b).DisposeAsync();
        Assert.AreEqual(0, limiter.SessionGateCount);
    }

    [TestMethod]
    public async Task SessionGate_IsRemovedOnceNoRequestHoldsOrWaitsForIt()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        for (var index = 0; index < 10; index++)
        {
            await (await AcquireAsync(limiter, "session-" + index, accountId, limit: 4)).DisposeAsync();
        }

        Assert.AreEqual(0, limiter.SessionGateCount);

        var holder = await AcquireAsync(limiter, "busy", accountId, limit: 4);
        var queued = AcquireAsync(limiter, "busy", accountId, limit: 4);
        using var cancellation = new CancellationTokenSource();
        var cancelled = AcquireAsync(limiter, "busy", accountId, limit: 4, cancellation.Token);
        Assert.AreEqual(1, limiter.SessionGateCount);

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(Hang));
        Assert.AreEqual(1, limiter.SessionGateCount);

        await holder.DisposeAsync();
        var second = await queued.WaitAsync(Hang);
        Assert.AreEqual(1, limiter.SessionGateCount);
        await second.DisposeAsync();
        Assert.AreEqual(0, limiter.SessionGateCount);

        // A session whose gate was removed still runs one request at a time.
        var again = await AcquireAsync(limiter, "busy", accountId, limit: 4);
        var blocked = AcquireAsync(limiter, "busy", accountId, limit: 4);
        await Task.Delay(25);
        Assert.IsFalse(blocked.IsCompleted);
        await again.DisposeAsync();
        await (await blocked.WaitAsync(Hang)).DisposeAsync();
        Assert.AreEqual(0, limiter.SessionGateCount);
    }

    [TestMethod]
    public async Task SessionGate_IsRemovedWhenTheAccountWaitIsCancelled()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        var holder = await AcquireAsync(limiter, "holder", accountId, limit: 1);
        using var cancellation = new CancellationTokenSource();
        var cancelled = AcquireAsync(limiter, "cancelled", accountId, limit: 1, cancellation.Token);
        Assert.AreEqual(2, limiter.SessionGateCount);

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled.WaitAsync(Hang));
        Assert.AreEqual(1, limiter.SessionGateCount);

        // The session of the cancelled request is free again.
        await (await AcquireAsync(limiter, "cancelled", "other-" + accountId, limit: 1).WaitAsync(Hang)).DisposeAsync();
        await holder.DisposeAsync();
        Assert.AreEqual(0, limiter.SessionGateCount);
    }

    [TestMethod]
    public async Task RequestWithAHigherLimit_IsAdmittedWhileOneWithALowerLimitWaits()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        var holder = await AcquireAsync(limiter, "holder", accountId, limit: 1);
        var lowLimit = AcquireAsync(limiter, "low", accountId, limit: 1);
        var highLimit = AcquireAsync(limiter, "high", accountId, limit: 2);
        Assert.IsFalse(lowLimit.IsCompleted);
        Assert.IsTrue(highLimit.IsCompletedSuccessfully);

        // Two requests still run: the waiter with a limit of one is not admitted yet.
        await holder.DisposeAsync();
        await Task.Delay(25);
        Assert.IsFalse(lowLimit.IsCompleted);

        await (await highLimit).DisposeAsync();
        await (await lowLimit.WaitAsync(Hang)).DisposeAsync();
    }

    [TestMethod]
    public async Task Lease_DisposedTwice_GivesBackOneSlot()
    {
        var limiter = new CodexSubscriptionConcurrencyLimiter();
        var accountId = NewAccountId();
        var first = await AcquireAsync(limiter, "first", accountId, limit: 1);
        await first.DisposeAsync();
        await first.DisposeAsync();

        var second = AcquireAsync(limiter, "second", accountId, limit: 1);
        var third = AcquireAsync(limiter, "third", accountId, limit: 1);
        Assert.IsTrue(second.IsCompletedSuccessfully);
        Assert.IsFalse(third.IsCompleted);
        await (await second).DisposeAsync();
        await (await third.WaitAsync(Hang)).DisposeAsync();
    }

    private static string NewAccountId() => "acct_" + Guid.NewGuid().ToString("N");

    private static Task<IAsyncDisposable> AcquireAsync(
        CodexSubscriptionConcurrencyLimiter limiter,
        string sessionId,
        string accountId,
        int limit,
        CancellationToken cancellationToken = default)
        => limiter.AcquireAsync("codex", sessionId, accountId, limit, cancellationToken).AsTask();
}
