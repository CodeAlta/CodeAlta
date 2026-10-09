using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ReminderRpcTests
{
    [TestMethod]
    public async Task SaveUsesExactOwnedSnapshotAndKeepsScheduleAcrossConflictsDeletionAndCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-reminder-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var clock = new LiteralClock();
            var sends = new List<OwnedTextSendRequest>();
            await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id is "one" or "two" or "ONE"), request =>
            {
                sends.Add(request);
                return new(OwnedSessionCommandAdmissionKind.Capacity);
            }, clock);
            var created = await service.Create(new("epoch", "one", "original", 60, 1), default);
            var id = created.ReminderId!;
            var before = (await service.List(new("epoch", "one"), default)).Reminders.Single();
            var detail = await service.Detail(new("epoch", "one", id), default);
            Assert.AreEqual("0", detail.EditRevision);
            var request = new ReminderSaveRequest("epoch", "one", id, detail.EditRevision!, "edited\nfull");
            Assert.AreEqual("stale_epoch", (await service.Save(request with { ExpectedEpoch = "other" }, default)).Status);
            Assert.AreEqual("missing_reminder", (await service.Save(request with { SessionId = "two" }, default)).Status);
            Assert.AreEqual("missing_reminder", (await service.Save(request with { SessionId = "ONE" }, default)).Status);
            Assert.AreEqual("missing_reminder", (await service.Save(request with { ReminderId = id.ToUpperInvariant() }, default)).Status);
            Assert.AreEqual("invalid_request", (await service.Save(request with { EditRevision = "-1" }, default)).Status);
            Assert.AreEqual("invalid_request", (await service.Save(request with { Content = "\ud800" }, default)).Status);
            Assert.AreEqual("ok", (await service.Save(request, default)).Status);
            var after = (await service.List(new("epoch", "one"), default)).Reminders.Single();
            Assert.AreEqual(before.DueAt, after.DueAt);
            Assert.AreEqual(before.DelaySeconds, after.DelaySeconds);
            Assert.AreEqual(before.RepeatCount, after.RepeatCount);
            Assert.AreEqual(before.FiredCount, after.FiredCount);
            Assert.AreEqual("conflict", (await service.Save(request, default)).Status);
            Assert.AreEqual("edited\nfull", (await service.Detail(new("epoch", "one", id), default)).Content);
            await clock.TimerCreated();
            clock.Advance(TimeSpan.FromSeconds(60));
            for (var i = 0; i < 100000 && (await service.List(new("epoch", "one"), default)).CompletedCount == 0; i++) await Task.Yield();
            Assert.HasCount(1, sends);
            Assert.AreEqual("edited\nfull", sends[0].Text);
            var current = await service.Detail(new("epoch", "one", id), default);
            Assert.AreEqual("completed", (await service.Save(request with { EditRevision = current.EditRevision! }, default)).Status);
            Assert.AreEqual("ok", service.Delete(new("epoch", "one", id, id), default).Status);
            Assert.AreEqual("missing_reminder", (await service.Save(request, default)).Status);
            service.CloseAdmission();
            Assert.AreEqual("closed", (await service.Save(request, default)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ActiveListsOnlySessionsWithActiveRemindersWithoutMessageText()
    {
        using var clock = new LiteralClock();
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id is "one" or "two"),
            _ => throw new AssertFailedException("No delivery expected."), clock);
        Assert.AreEqual(0, service.Active(new("epoch"), default).Sessions.Count);
        Assert.AreEqual("stale_epoch", service.Active(new("other"), default).Status);
        Assert.AreEqual("invalid_request", service.Active(new(" "), default).Status);
        Assert.AreEqual("ok", (await service.Create(new("epoch", "one", "private first", 60, 1), default)).Status);
        Assert.AreEqual("ok", (await service.Create(new("epoch", "one", "private second", 120, 2), default)).Status);
        var removed = await service.Create(new("epoch", "two", "private third", 30, 1), default);
        var active = service.Active(new("epoch"), default);
        Assert.AreEqual("ok", active.Status);
        CollectionAssert.AreEqual(new[] { "one", "two" }, active.Sessions.Select(row => row.SessionId).ToArray());
        Assert.AreEqual(2, active.Sessions[0].ActiveCount);
        Assert.AreEqual(1, active.Sessions[1].ActiveCount);
        var due = (await service.List(new("epoch", "one"), default)).Reminders.Min(row => row.DueAt);
        Assert.AreEqual(due, active.Sessions[0].NextDueAt);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(active, DesktopJsonContext.Default.ReminderActiveResponse).Contains("private", StringComparison.Ordinal));
        Assert.AreEqual("ok", service.Delete(new("epoch", "two", removed.ReminderId!, removed.ReminderId!), default).Status);
        CollectionAssert.AreEqual(new[] { "one" }, service.Active(new("epoch"), default).Sessions.Select(row => row.SessionId).ToArray());
        await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => service.Active(new("epoch"), new CancellationToken(true))));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.Active(new("epoch"), default).Status);
    }

    [TestMethod]
    public async Task SaveWaitingForSessionReadCannotPassClosedAdmission()
    {
        using var clock = new LiteralClock();
        var releaseRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new ReminderService("epoch", (id, _) =>
        {
            enteredRead.TrySetResult();
            return releaseRead.Task;
        }, _ => throw new AssertFailedException("No delivery expected."), clock);
        var request = new ReminderSaveRequest("epoch", "one", "reminder-1", "0", "new message");
        var pending = service.Save(request, default);
        try
        {
            await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.CloseAdmission();
        }
        finally { releaseRead.TrySetResult(true); }
        Assert.AreEqual("closed", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [TestMethod]
    public async Task DetailReadsExactRetainedMessageWithoutChangingDeliveryOrSchedule()
    {
        using var clock = new LiteralClock();
        var sends = new List<OwnedTextSendRequest>();
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id is "one" or "two" or "ONE"), request =>
        {
            sends.Add(request);
            return new(OwnedSessionCommandAdmissionKind.Capacity);
        }, clock);
        var content = "First 😀\r\nSecond\t" + new string('\\', 2000) + new string('"', 2000);
        var created = await service.Create(new("epoch", "one", content, 60, 1), default);
        Assert.AreEqual("ok", created.Status);
        var id = created.ReminderId!;
        Assert.AreEqual("stale_epoch", (await service.Detail(new("wrong", "one", id), default)).Status);
        Assert.AreEqual("invalid_request", (await service.Detail(new("epoch", "one", "bad\n"), default)).Status);
        Assert.AreEqual("missing_session", (await service.Detail(new("epoch", "absent", id), default)).Status);
        Assert.AreEqual("missing_reminder", (await service.Detail(new("epoch", "two", id), default)).Status);
        Assert.AreEqual("missing_reminder", (await service.Detail(new("epoch", "ONE", id), default)).Status);
        var detail = await service.Detail(new("epoch", "one", id), default);
        Assert.AreEqual("ok", detail.Status);
        Assert.AreEqual("one", detail.SessionId);
        Assert.AreEqual(id, detail.ReminderId);
        Assert.AreEqual(content, detail.Content);
        Assert.AreEqual(60, detail.DelaySeconds);
        Assert.AreEqual(1, detail.RepeatCount);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(detail, DesktopJsonContext.Default.ReminderDetailResponse).Length <= ReminderService.MaximumDetailResponseBytes);
        var beforeFiring = (await service.List(new("epoch", "one"), default)).Reminders.Single();
        Assert.AreEqual(0, beforeFiring.FiredCount);
        Assert.AreEqual(60, beforeFiring.DelaySeconds);
        Assert.AreEqual(1, beforeFiring.RepeatCount);
        Assert.IsNotNull(beforeFiring.DueAt);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if ((await service.List(new("epoch", "one"), default)).CompletedCount == 1) break;
            await Task.Yield();
        }
        Assert.AreEqual(content, (await service.Detail(new("epoch", "one", id), default)).Content);
        Assert.HasCount(1, sends);
        Assert.AreEqual(content, sends[0].Text);
        Assert.AreEqual("ok", service.Delete(new("epoch", "one", id, id), default).Status);
        Assert.AreEqual("missing_reminder", (await service.Detail(new("epoch", "one", id), default)).Status);
        service.CloseAdmission();
        Assert.AreEqual("closed", (await service.Detail(new("epoch", "one", id), default)).Status);
    }

    [TestMethod]
    public async Task DetailReadFailureDoesNotReflectPrivateDiagnostics()
    {
        await using var service = new ReminderService("epoch", (_, _) => throw new InvalidOperationException("private path"),
            _ => throw new AssertFailedException("No provider may be called."));
        var result = await service.Detail(new("epoch", "one", "reminder"), default);
        Assert.AreEqual("read_failed", result.Status);
        Assert.IsNull(result.Content);
        Assert.IsFalse(JsonSerializer.Serialize(result, DesktopJsonContext.Default.ReminderDetailResponse).Contains("private path", StringComparison.Ordinal));
    }

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
    public async Task ClockFiringUsesExactTargetAndRecordsRefusedOwnerAdmissionWithoutRetry()
    {
        using var clock = new LiteralClock();
        var sends = new List<OwnedTextSendRequest>();
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"), request => {
            sends.Add(request);
            return new(OwnedSessionCommandAdmissionKind.Capacity);
        }, clock);
        var created = await service.Create(new("epoch", "one", "message", 60, 1), CancellationToken.None);
        Assert.AreEqual("ok", created.Status);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        var completed = await Completed(service, "one");
        Assert.AreEqual(0, completed.ActiveCount);
        Assert.AreEqual(1, completed.CompletedCount);
        Assert.AreEqual(1, completed.Reminders.Single().FiredCount);
        Assert.AreEqual("send_capacity", completed.Reminders.Single().LastError);
        Assert.HasCount(1, sends);
        Assert.AreEqual("one", sends[0].SessionId);
        Assert.AreEqual("message", sends[0].Text);
        Assert.AreEqual($"reminder:{created.ReminderId}:0", sends[0].ClientRequestId);
    }

    [TestMethod]
    [DataRow("steered")]
    [DataRow("queued")]
    public async Task FiringDuringATurnGivesTheReminderToThatTurnWithoutASend(string given)
    {
        using var clock = new LiteralClock();
        var turns = new List<(string SessionId, string Content)>();
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"),
            _ => throw new AssertFailedException("A session that runs a turn is not sent to: the send would be refused."),
            (id, content) => { lock (turns) turns.Add((id, content)); return Task.FromResult(given); }, clock);
        var created = await service.Create(new("epoch", "one", "check the build", 60, 1), CancellationToken.None);
        Assert.AreEqual("ok", created.Status);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        var completed = await Completed(service, "one");
        var row = completed.Reminders.Single();
        Assert.AreEqual(1, row.FiredCount);
        Assert.AreEqual(0, row.LastExitCode);
        Assert.IsNull(row.LastError);
        Assert.HasCount(1, turns);
        Assert.AreEqual(("one", "check the build"), turns[0]);
    }

    [TestMethod]
    public async Task FiringWhileAnotherSendIsBeingAdmittedAsksAgainAndReachesItsTurn()
    {
        using var clock = new LiteralClock();
        var sends = new List<OwnedTextSendRequest>();
        var asked = 0;
        // The send of the user is admitted and its turn does not run yet; it runs when the firing asks again.
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"), request =>
        {
            lock (sends) sends.Add(request);
            return new(OwnedSessionCommandAdmissionKind.Busy);
        }, (_, _) => Task.FromResult(Interlocked.Increment(ref asked) == 1 ? "idle" : "steered"), clock);
        var created = await service.Create(new("epoch", "one", "message", 60, 1), CancellationToken.None);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        await clock.TimerCreated();
        Assert.AreEqual(1, (await service.List(new("epoch", "one"), CancellationToken.None)).ActiveCount, "The firing waits for its place.");
        clock.Advance(ReminderService.BusyRetryDelay);
        var completed = await Completed(service, "one");
        Assert.AreEqual(0, completed.Reminders.Single().LastExitCode);
        Assert.IsNull(completed.Reminders.Single().LastError);
        Assert.AreEqual(2, Volatile.Read(ref asked));
        Assert.HasCount(1, sends);
        Assert.AreEqual($"reminder:{created.ReminderId}:0", sends[0].ClientRequestId);
    }

    [TestMethod]
    public async Task ASessionThatStaysBusyWithoutATurnEndsTheFiringAsBusy()
    {
        using var clock = new LiteralClock();
        var sends = 0;
        await using var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"), _ =>
        {
            Interlocked.Increment(ref sends);
            return new(OwnedSessionCommandAdmissionKind.Busy);
        }, clock);
        await service.Create(new("epoch", "one", "message", 60, 1), CancellationToken.None);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        for (var attempt = 1; attempt < ReminderService.MaximumBusyAttempts; attempt++)
        {
            await clock.TimerCreated();
            clock.Advance(ReminderService.BusyRetryDelay);
        }
        var completed = await Completed(service, "one");
        Assert.AreEqual("send_busy", completed.Reminders.Single().LastError);
        Assert.AreEqual(ReminderService.MaximumBusyAttempts, Volatile.Read(ref sends));
    }

    [TestMethod]
    public async Task ClosingReleasesAFiringThatWaitsForItsPlace()
    {
        using var clock = new LiteralClock();
        var service = new ReminderService("epoch", (id, _) => Task.FromResult(id == "one"),
            _ => new(OwnedSessionCommandAdmissionKind.Busy), clock);
        await service.Create(new("epoch", "one", "message", 60, 1), CancellationToken.None);
        await clock.TimerCreated();
        clock.Advance(TimeSpan.FromSeconds(60));
        await clock.TimerCreated();
        // No clock tick: only the closing of the service ends the wait.
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<ReminderListResponse> Completed(ReminderService service, string sessionId)
    {
        for (var attempt = 0; attempt < 100000; attempt++)
        {
            var list = await service.List(new("epoch", sessionId), CancellationToken.None);
            if (list.CompletedCount == 1) return list;
            await Task.Yield();
        }
        Assert.Fail("The reminder did not complete.");
        throw new InvalidOperationException("Unreachable.");
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
