using System.Threading.Channels;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ReminderRpcTests
{
    [TestMethod]
    public async Task ExactSessionEpochValidationConfirmationAndCloseGateMutations()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-reminder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var clock = new LiteralClock();
            var lookups = 0;
            await using var service = new ReminderService("epoch", (id, _) => {
                lookups++; return Task.FromResult(id is "one" or "two");
            }, _ => throw new AssertFailedException("No provider may be called."), clock);
            Assert.AreEqual("stale_epoch", (await service.Create(new("other", "one", "text", 60, 1), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.Create(new("epoch", "one", "text", 0, 1), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.Create(new("epoch", "one", "text", 60, 21), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.Create(new("epoch", "one", "\ud800", 60, 1), CancellationToken.None)).Status);
            Assert.AreEqual(0, lookups);
            Assert.AreEqual("missing_session", (await service.Create(new("epoch", "missing", "text", 60, 1), CancellationToken.None)).Status);
            var created = await service.Create(new("epoch", "one", "message\nbody", 60, 2), CancellationToken.None);
            Assert.AreEqual("ok", created.Status);
            Assert.IsNotNull(created.ReminderId);
            var list = await service.List(new("epoch", "one"), CancellationToken.None);
            Assert.AreEqual(1, list.ActiveCount);
            Assert.AreEqual(0, list.CompletedCount);
            Assert.AreEqual(created.ReminderId, list.Reminders.Single().Id);
            Assert.AreEqual("ok", (await service.List(new("epoch", "two"), CancellationToken.None)).Status);
            Assert.AreEqual(0, (await service.List(new("epoch", "two"), CancellationToken.None)).Reminders.Count);
            Assert.AreEqual("missing_reminder", service.Delete(new("epoch", "two", created.ReminderId, created.ReminderId), CancellationToken.None).Status);
            Assert.AreEqual("invalid_request", service.Delete(new("epoch", "one", created.ReminderId, "not confirmed"), CancellationToken.None).Status);
            Assert.AreEqual("ok", service.Delete(new("epoch", "one", created.ReminderId, created.ReminderId), CancellationToken.None).Status);
            Assert.AreEqual("missing_reminder", service.Delete(new("epoch", "one", created.ReminderId, created.ReminderId), CancellationToken.None).Status);
            service.CloseAdmission();
            Assert.AreEqual("closed", (await service.Create(new("epoch", "one", "text", 60, 1), CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ClockFiringUsesExactTargetAndRecordsFailedOwnerAdmissionWithoutRetry()
    {
        using var clock = new LiteralClock();
        var sends = new List<OwnedTextSendRequest>();
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"), request => {
            sends.Add(request);
            return new(OwnedSessionCommandAdmissionKind.Busy);
        }, clock);
        var created = await service.Create(new("epoch", "one", "message", 60, 1), CancellationToken.None);
        Assert.AreEqual("ok", created.Status);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        ReminderListResponse? completed = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            completed = await service.List(new("epoch", "one"), CancellationToken.None);
            if (completed.CompletedCount == 1) break;
            await Task.Yield();
        }
        Assert.IsNotNull(completed);
        Assert.AreEqual(0, completed.ActiveCount);
        Assert.AreEqual(1, completed.CompletedCount);
        Assert.AreEqual(1, completed.Reminders.Single().FiredCount);
        Assert.AreEqual("send_busy", completed.Reminders.Single().LastError);
        Assert.HasCount(1, sends);
        Assert.AreEqual("one", sends[0].SessionId);
        Assert.AreEqual("message", sends[0].Text);
        Assert.AreEqual($"reminder:{created.ReminderId}:0", sends[0].ClientRequestId);
    }

    [TestMethod]
    public async Task UnconfirmedOwnerAdmissionDoesNotReplayCapturedFiring()
    {
        using var clock = new LiteralClock();
        var calls = 0;
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"), _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Admission uncertain after callback entered.");
        }, clock);
        var created = await service.Create(new("epoch", "one", "exact", 60, 1), default);
        Assert.AreEqual("ok", created.Status);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        ReminderListResponse? done = null;
        for (var attempt = 0; attempt < 100000; attempt++)
        {
            done = await service.List(new("epoch", "one"), default);
            if (done.CompletedCount == 1) break;
            await Task.Yield();
        }
        Assert.IsNotNull(done);
        Assert.AreEqual(1, done.CompletedCount);
        Assert.AreEqual(1, done.Reminders.Single().FiredCount);
        Assert.AreEqual("send_failed", done.Reminders.Single().LastError);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(1, Volatile.Read(ref calls));
    }

    [TestMethod]
    public async Task RetainedRowsAreBoundedAndDeletionReleasesCapacity()
    {
        using var clock = new LiteralClock();
        await using var service = new ReminderService("epoch", (_, _) => Task.FromResult(true),
            _ => throw new AssertFailedException("No provider may be called."), clock);
        var ids = new List<string>();
        for (var index = 0; index < 32; index++)
        {
            var created = await service.Create(new("epoch", "one", "text", 86400, 1), CancellationToken.None);
            Assert.AreEqual("ok", created.Status);
            ids.Add(created.ReminderId!);
        }
        Assert.AreEqual("capacity", (await service.Create(new("epoch", "one", "text", 86400, 1), CancellationToken.None)).Status);
        Assert.AreEqual(32, (await service.List(new("epoch", "one"), CancellationToken.None)).ActiveCount);
        Assert.AreEqual("ok", service.Delete(new("epoch", "one", ids[0], ids[0]), CancellationToken.None).Status);
        Assert.AreEqual("ok", (await service.Create(new("epoch", "one", "text", 86400, 1), CancellationToken.None)).Status);
    }

    internal sealed class LiteralClock : TimeProvider, IDisposable
    {
        private readonly object _gate = new();
        private readonly Channel<LiteralTimer> _created = Channel.CreateUnbounded<LiteralTimer>();
        private DateTimeOffset _now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        private LiteralTimer? _timer;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            var timer = new LiteralTimer(callback, state);
            lock (_gate) _timer = timer;
            _created.Writer.TryWrite(timer);
            return timer;
        }
        public async Task TimerCreated() => _ = await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Advance(TimeSpan amount)
        {
            LiteralTimer? timer;
            lock (_gate) { _now += amount; timer = _timer; }
            timer?.Fire();
        }
        public void Dispose() { lock (_gate) _timer?.Dispose(); }
        private sealed class LiteralTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
