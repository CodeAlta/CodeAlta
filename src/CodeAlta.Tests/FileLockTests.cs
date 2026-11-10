using CodeAlta.Core.IO;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileLockTests
{
    private static readonly TimeSpan Hang = TestTime.Seconds(30);

    [TestMethod]
    public async Task ALockFile_HasOneOwner_AndAWaiterTakesItOnceReleased()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "state.lock");

        var held = LockFile.Acquire(path, TimeSpan.Zero);
        Assert.IsFalse(LockFile.TryAcquire(path, out _));
        Assert.Throws<TimeoutException>(() => LockFile.Acquire(path, TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<TimeoutException>(async () => await LockFile.AcquireAsync(path, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        using (var canceled = new CancellationTokenSource())
        {
            var wait = LockFile.AcquireAsync(path, canceled.Token).AsTask();
            await canceled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WithinAsync(Hang));
        }

        var waiter = LockFile.AcquireAsync(path, CancellationToken.None).AsTask();
        Assert.IsFalse(waiter.IsCompleted);
        await held.DisposeAsync();
        await using (await waiter.WithinAsync(Hang))
        {
            Assert.IsFalse(LockFile.TryAcquire(path, out _));
        }

        Assert.IsTrue(LockFile.TryAcquire(path, out var again));
        await again.DisposeAsync();
        Assert.IsTrue(File.Exists(path), "The lock file stays once released.");
    }

    [TestMethod]
    public async Task APathLock_MakesTheOwnersOfOnePathWait_NotThoseOfAnother()
    {
        using var temp = TestTempDirectory.Create();
        var locks = new FilePathLocks();
        var path = Path.Combine(temp.Path, "config.toml");
        var other = Path.Combine(temp.Path, "other.toml");

        var held = await locks.AcquireAsync(path, CancellationToken.None);
        var otherOwner = locks.AcquireAsync(other, CancellationToken.None);
        Assert.IsTrue(otherOwner.IsCompleted, "Another path does not wait.");
        (await otherOwner).Dispose();

        // The same file under another spelling of its path.
        var samePath = Path.Combine(temp.Path, ".", OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "CONFIG.toml" : "config.toml");
        var waiter = locks.AcquireAsync(samePath, CancellationToken.None).AsTask();
        var blocked = Task.Run(() => locks.Acquire(path));
        Assert.IsFalse(waiter.IsCompleted);
        held.Dispose();
        held.Dispose();
        (await waiter.WithinAsync(Hang)).Dispose();
        (await blocked.WithinAsync(Hang)).Dispose();
        Assert.AreEqual(0, locks.Count, "A lock no owner holds or waits for is forgotten.");
    }

    [TestMethod]
    public async Task APathLockWhoseWaitIsCanceled_IsForgottenWithItsOwner()
    {
        using var temp = TestTempDirectory.Create();
        var locks = new FilePathLocks();
        var path = Path.Combine(temp.Path, "journal.jsonl");

        var held = locks.Acquire(path);
        using (var canceled = new CancellationTokenSource())
        {
            var wait = locks.AcquireAsync(path, canceled.Token).AsTask();
            Assert.AreEqual(1, locks.Count);
            await canceled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => wait.WithinAsync(Hang));
        }

        Assert.AreEqual(1, locks.Count, "Its owner still holds it.");
        held.Dispose();
        Assert.AreEqual(0, locks.Count);
        Assert.Throws<ArgumentException>(() => locks.Acquire(" "));
    }

    [TestMethod]
    public async Task ManyOwnersOfOnePath_HoldItOneAtATime()
    {
        using var temp = TestTempDirectory.Create();
        var locks = new FilePathLocks();
        var path = Path.Combine(temp.Path, "state.json");
        var inside = 0;
        var most = 0;
        using var start = new ManualResetEventSlim();
        var owners = Enumerable.Range(0, 8).Select(owner => Task.Run(async () =>
        {
            start.Wait();
            for (var index = 0; index < 50; index++)
            {
                using var held = owner % 2 == 0 ? locks.Acquire(path) : await locks.AcquireAsync(path, CancellationToken.None);
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(owners).WithinAsync(Hang);
        Assert.AreEqual(1, most);
        Assert.AreEqual(0, locks.Count);

        static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
