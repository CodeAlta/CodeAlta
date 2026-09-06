using System.Threading.Channels;
using CodeAlta.LiveTool;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AltaReminderServiceTests
{
    [TestMethod]
    public async Task EditDuringDispatcherResolution_PreservesCapturedContent()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create();
        fixture.ResolvingDispatcher = () =>
            Assert.IsTrue(fixture.Service.TryUpdateContent(reminder.ReminderId, "later text", out _));
        var completed = fixture.WatchFiredCount(1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var delivery = await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("original text", delivery.Content);
        Assert.AreEqual("later text", fixture.Snapshot().ContentPreview);
    }

    [TestMethod]
    public async Task EditBeforeCapture_ChangesThisFiringWithoutChangingSchedule()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create();
        Assert.IsTrue(fixture.Service.TryUpdateContent(reminder.ReminderId.ToUpperInvariant(), " edited\ncontent ", out var edited));
        Assert.AreEqual(reminder.DueAt, edited!.DueAt);
        Assert.IsTrue(fixture.Service.TryGetContent(reminder.ReminderId, out var content));
        Assert.AreEqual(" edited\ncontent ", content);
        var completed = fixture.WatchFiredCount(1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var delivery = await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(content, delivery.Content);
        Assert.AreEqual("original text", reminder.ContentPreview, "Previously returned descriptors remain immutable.");
    }

    [TestMethod]
    public async Task EditAfterCapture_IsPreservedByBookkeepingAndUsedByNextRepeat()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create(repeatCount: 2);
        await fixture.Clock.NextTimerAsync();
        fixture.ResolvingDispatcher = () =>
        {
            fixture.ResolvingDispatcher = null;
            Assert.IsTrue(fixture.Service.TryUpdateContent(reminder.ReminderId, "next firing", out _));
        };
        var firstFinished = fixture.WatchFiredCount(1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var first = await fixture.NextDeliveryAsync();
        await firstFinished.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Clock.NextTimerAsync();

        Assert.AreEqual("original text", first.Content);
        Assert.AreEqual("next firing", fixture.Snapshot().ContentPreview);
        Assert.IsTrue(fixture.Service.TryGetContent(reminder.ReminderId, out var content));
        Assert.AreEqual("next firing", content);
        var completed = fixture.WatchFiredCount(2);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("next firing", second.Content);
        Assert.AreEqual(AltaReminderStates.Completed, fixture.Snapshot().State);
        Assert.AreEqual(2, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task DeleteBeforeCapture_CancelsTimerWithoutDispatch()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create(repeatCount: 2);
        var timer = await fixture.Clock.NextTimerAsync();

        Assert.IsTrue(fixture.Service.TryDelete(reminder.ReminderId.ToUpperInvariant(), out var deleted));
        await timer.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Clock.Advance(TimeSpan.FromDays(2));

        Assert.AreEqual(AltaReminderStates.Deleted, deleted!.State);
        Assert.AreEqual(0, deleted.FiredCount);
        Assert.AreEqual(0, fixture.DeliveryCount);
        Assert.AreEqual(0, fixture.Service.List(null, includeCompleted: true).Count);
        Assert.IsFalse(fixture.Service.TryGetContent(reminder.ReminderId, out _));
        Assert.IsFalse(fixture.Service.TryUpdateContent(reminder.ReminderId, "too late", out _));
    }

    [TestMethod]
    public async Task DeleteDuringDispatcherResolution_AllowsCapturedSendWithoutRepeatOrResurrection()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create(repeatCount: 2);
        AltaReminderDescriptor? deleted = null;
        fixture.ResolvingDispatcher = () =>
            Assert.IsTrue(fixture.Service.TryDelete(reminder.ReminderId, out deleted));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var delivery = await fixture.NextDeliveryAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(2));

        Assert.AreEqual("original text", delivery.Content);
        Assert.IsFalse(delivery.CancellationToken.CanBeCanceled);
        Assert.IsNotNull(deleted);
        Assert.AreEqual(0, deleted.FiredCount);
        Assert.AreEqual(1, fixture.DeliveryCount);
        Assert.AreEqual(0, fixture.Clock.PendingTimerCount);
        Assert.AreEqual(0, fixture.Service.List(null, includeCompleted: true).Count);
        Assert.IsFalse(fixture.Service.TryDelete(reminder.ReminderId, out _));
    }

    [TestMethod]
    public async Task Delivery_PreservesCapturedIdentityAndDispatchesOutsideOwnership()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create();
        // A different thread must acquire reminder ownership while resolving the dispatcher.
        // Same-thread edits alone would not detect accidentally holding a reentrant lock.
        fixture.ResolvingDispatcher = () => Task.Run(() =>
            Assert.IsTrue(fixture.Service.TryUpdateContent(reminder.ReminderId, "later edit", out _)))
            .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        var completed = fixture.WatchFiredCount(1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var delivery = await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));

        // Only session/send with this target, --stdin and --queue-if-busy is accepted by the fixture.
        Assert.AreEqual("Target-Session", delivery.Target);
        Assert.IsTrue(delivery.Stdin);
        Assert.IsTrue(delivery.QueueIfBusy);
        Assert.AreEqual("original text", delivery.Content);
        Assert.AreEqual("reminder", delivery.Caller.Kind);
        Assert.AreEqual("Source-Session", delivery.Caller.SourceSessionId);
        Assert.AreEqual("Source-Agent", delivery.Caller.SourceAgentId);
        Assert.AreEqual("Source-Project", delivery.Caller.SourceProjectId);
        Assert.AreEqual("Plugin-Key", delivery.Caller.PluginRuntimeKey);
        Assert.AreEqual("captured-cwd", delivery.Cwd);
        Assert.AreEqual(CancellationToken.None, delivery.CancellationToken);
        Assert.AreEqual(1, fixture.DeliveryCount);
    }

    [TestMethod]
    public async Task BlockedDelivery_PreventsOverlapAndSchedulesFromCompletion()
    {
        using var fixture = new ReminderFixture();
        fixture.Create(repeatCount: 2);
        await fixture.Clock.NextTimerAsync();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Send = _ => release.Task;
        var firstFinished = fixture.WatchFiredCount(1);
        DateTimeOffset completedAt;
        try
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.NextDeliveryAsync();
            fixture.Clock.Advance(TimeSpan.FromHours(2));
            Assert.AreEqual(1, fixture.DeliveryCount);
            Assert.AreEqual(0, fixture.Snapshot().FiredCount);
            Assert.AreEqual(0, fixture.Clock.PendingTimerCount);
            completedAt = fixture.Clock.GetUtcNow();
        }
        finally
        {
            release.TrySetResult(AltaExitCodes.Success);
            await firstFinished.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var nextTimer = await fixture.Clock.NextTimerAsync();
        Assert.AreEqual(completedAt, fixture.Snapshot().LastFiredAt);
        Assert.AreEqual(completedAt + TimeSpan.FromMinutes(1), fixture.Snapshot().DueAt);
        Assert.AreEqual(fixture.Snapshot().DueAt, nextTimer.DueAt);
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.AreEqual(1, fixture.DeliveryCount);
        var completed = fixture.WatchFiredCount(2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, fixture.DeliveryCount);
        Assert.IsNull(fixture.Snapshot().DueAt);
    }

    [TestMethod]
    public async Task FailedDeliveries_CountTowardRepeatAndRemainUntilDeleted()
    {
        using var fixture = new ReminderFixture();
        var reminder = fixture.Create(repeatCount: 2);
        await fixture.Clock.NextTimerAsync();
        fixture.Send = static _ => Task.FromResult(AltaExitCodes.Failure);
        var firstFinished = fixture.WatchFiredCount(1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.NextDeliveryAsync();
        await firstFinished.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Clock.NextTimerAsync();
        Assert.AreEqual(AltaReminderStates.Active, fixture.Snapshot().State);
        Assert.AreEqual(AltaExitCodes.Failure, fixture.Snapshot().LastExitCode);
        var completed = fixture.WatchFiredCount(2);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.NextDeliveryAsync();
        await completed.WaitAsync(TimeSpan.FromSeconds(10));

        var retained = fixture.Snapshot();
        Assert.AreEqual(AltaReminderStates.Completed, retained.State);
        Assert.AreEqual(2, retained.FiredCount);
        Assert.AreEqual(AltaExitCodes.Failure, retained.LastExitCode);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), retained.CompletedAt);
        Assert.IsNull(retained.DueAt);
        Assert.AreEqual(0, fixture.Service.List(null, includeCompleted: false).Count);
        Assert.AreEqual(1, fixture.Service.List("target-session", includeCompleted: true).Count);
        Assert.AreEqual(0, fixture.Service.List("other-session", includeCompleted: true).Count);
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        Assert.AreEqual(2, fixture.DeliveryCount);
        Assert.AreEqual(retained, fixture.Snapshot());
        Assert.IsTrue(fixture.Service.TryDelete(reminder.ReminderId, out _));
        Assert.AreEqual(0, fixture.Service.List(null, includeCompleted: true).Count);
    }

    // No runtime/catalog or default command contributors: only the real reminder -> dispatcher
    // route and a controlled, in-memory session/send command execute in these fixtures.
    private sealed class ReminderFixture : IServiceProvider, IDisposable
    {
        private readonly AltaCommandDispatcher _dispatcher;
        private readonly Channel<Delivery> _deliveries = Channel.CreateUnbounded<Delivery>();
        private readonly List<EventHandler> _subscriptions = [];
        private int _deliveryCount;

        public ReminderFixture()
        {
            _dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry([new SendContributor(this)]), this);
            Service = new AltaReminderService(this, Clock);
        }

        public ManualClock Clock { get; } = new();
        public AltaReminderService Service { get; }
        public Action? ResolvingDispatcher { get; set; }
        public Func<Delivery, Task<int>> Send { get; set; } = static _ => Task.FromResult(AltaExitCodes.Success);
        public int DeliveryCount => Volatile.Read(ref _deliveryCount);

        public AltaReminderDescriptor Create(int repeatCount = 1) => Service.Create(new AltaReminderCreateRequest
        {
            TargetSessionId = " Target-Session ",
            SourceSessionId = " Source-Session ",
            SourceAgentId = " Source-Agent ",
            SourceProjectId = " Source-Project ",
            PluginRuntimeKey = " Plugin-Key ",
            Cwd = " captured-cwd ",
            Content = "original text",
            Duration = TimeSpan.FromMinutes(1),
            RepeatCount = repeatCount,
        });

        public AltaReminderDescriptor Snapshot() => Service.List(null, includeCompleted: true).Single();

        public Task WatchFiredCount(int count)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (_, _) =>
            {
                if (Service.List(null, includeCompleted: true).Any(item => item.FiredCount == count))
                {
                    completion.TrySetResult();
                }
            };
            _subscriptions.Add(handler);
            Service.Changed += handler;
            return completion.Task;
        }

        public async Task<Delivery> NextDeliveryAsync()
            => await _deliveries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public object? GetService(Type serviceType)
        {
            if (serviceType != typeof(AltaCommandDispatcher))
            {
                throw new InvalidOperationException($"Unexpected service resolution: {serviceType.Name}");
            }

            ResolvingDispatcher?.Invoke();
            return _dispatcher;
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                Service.Changed -= subscription;
            }

            foreach (var reminder in Service.List(null, includeCompleted: true))
            {
                Service.TryDelete(reminder.ReminderId, out _);
            }

            Clock.Dispose();
        }

        private sealed class SendContributor(ReminderFixture fixture) : IAltaCommandContributor
        {
            public IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
            {
                string? target = null;
                var stdin = false;
                var queueIfBusy = false;
                var send = new Command("send", "Controlled reminder delivery.");
                send.Add("<session-id>", "Target.", value => target = value);
                send.Add("stdin", "Read content.", value => stdin = value is not null);
                send.Add("queue-if-busy", "Queue if busy.", value => queueIfBusy = value is not null);
                send.Add(async (_, _) =>
                {
                    var invocation = context.Invocation;
                    var content = await invocation.Stdin.ReadToEndAsync();
                    var delivery = new Delivery(target, stdin, queueIfBusy, content, invocation.Caller, invocation.Cwd, invocation.CancellationToken);
                    Interlocked.Increment(ref fixture._deliveryCount);
                    fixture._deliveries.Writer.TryWrite(delivery);
                    return await fixture.Send(delivery);
                });
                var session = new Command("session", "Controlled session commands.");
                session.Add(send);
                yield return session;
            }

            public IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context)
                => [];
        }
    }

    private sealed record Delivery(string? Target, bool Stdin, bool QueueIfBusy, string Content,
        AltaCallerIdentity Caller, string? Cwd, CancellationToken CancellationToken);

    // One-shot timers only, as used by Task.Delay. Time advances explicitly and callbacks always
    // run outside clock ownership. No wall-clock sleeps, discovery, or background clock worker.
    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly Channel<ManualTimer> _created = Channel.CreateUnbounded<ManualTimer>();
        private DateTimeOffset _now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

        public int PendingTimerCount
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
            ManualTimer timer;
            lock (_gate)
            {
                timer = new ManualTimer(this, callback, state, _now + dueTime);
                _timers.Add(timer);
            }

            _created.Writer.TryWrite(timer);
            return timer;
        }

        public async Task<ManualTimer> NextTimerAsync()
            => await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.DueAt <= _now).ToList();
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public void Dispose()
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                timers = _timers.ToArray();
            }

            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }

        public sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
        {
            private int _disposed;
            public DateTimeOffset DueAt { get; } = dueAt;
            public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("Only one-shot delays are expected.");

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }

                Disposed.TrySetResult();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
