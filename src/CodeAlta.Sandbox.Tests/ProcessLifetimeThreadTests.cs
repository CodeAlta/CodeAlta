using CodeAlta.Sandbox.Mxc;

namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class ProcessLifetimeThreadTests
{
    [TestMethod]
    public async Task Operations_RunOnOneThread_ThatOutlivesTheirCallers()
    {
        var lifetime = new ProcessLifetimeThread("test");
        Thread? first = null;
        var caller = new Thread(() => first = lifetime.RunAsync(() => Thread.CurrentThread, static _ => { }, CancellationToken.None).GetAwaiter().GetResult());
        caller.Start();
        caller.Join();

        var second = await Task.Run(() => lifetime.RunAsync(() => Thread.CurrentThread, static _ => { }, CancellationToken.None));

        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
        Assert.IsFalse(first.IsThreadPoolThread, "a thread of the pool ends after some time without work");
        Assert.IsTrue(first.IsAlive, "the caller has ended, the thread of the sandboxes has not");
    }

    [TestMethod]
    public async Task AFailure_ReachesTheCaller()
    {
        var lifetime = new ProcessLifetimeThread("test");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lifetime.RunAsync<int>(() => throw new InvalidOperationException("no"), static _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task ACanceledWait_ReleasesTheResultThatComesLate()
    {
        var lifetime = new ProcessLifetimeThread("test");
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var disposed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = lifetime.RunAsync(() =>
        {
            release.Wait();
            return "late";
        }, late => disposed.TrySetResult(late), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        release.Set();
        Assert.AreEqual("late", await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task AnOperationCanceledBeforeItStarts_DoesNotRun()
    {
        var lifetime = new ProcessLifetimeThread("test");
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var ran = false;

        var blocker = lifetime.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)), static _ => { }, CancellationToken.None);
        var canceled = lifetime.RunAsync(() => ran = true, static _ => { }, cancellation.Token);
        await cancellation.CancelAsync();
        release.Set();
        await blocker;
        await lifetime.RunAsync(() => 0, static _ => { }, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
        Assert.IsFalse(ran);
    }
}
