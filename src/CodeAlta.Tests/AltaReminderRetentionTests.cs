using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;

namespace CodeAlta.Tests;

public sealed partial class AltaReminderServiceTests
{
    [TestMethod]
    public async Task Retention_ConcurrentCreationAndReservationHaveExactlyOneWinner()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var protection = ReadyRetention();
            await using var reminders = new AltaReminderService(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var create = Task.Run(async () =>
            {
                await start.Task;
                try { return reminders.Create(RetentionRequest()); }
                catch (InvalidOperationException) { return null; }
            });
            var retire = Task.Run(async () => { await start.Task; return protection.TryReserveRetirement("target"); });
            start.SetResult();
            var reminder = await create;
            using var reservation = await retire;
            Assert.AreNotEqual(reminder is null, reservation is null, "Both owners must arbitrate before publication.");
            Assert.AreEqual(reminder is null ? 0 : 1, reminders.List(null, true).Count);
        }
    }

    [TestMethod]
    public async Task Retention_RefusesCreationBeforePublicationOrScheduling()
    {
        var protection = ReadyRetention();
        using var retiring = protection.TryReserveRetirement("target");
        Assert.IsNotNull(retiring);
        using var clock = new ManualClock();
        var delivery = new HeldRetentionDelivery();
        await using var reminders = new AltaReminderService(
            new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection), clock, delivery);
        var notifications = 0;
        reminders.Changed += (_, _) => notifications++;

        Assert.Throws<InvalidOperationException>(() => reminders.Create(RetentionRequest()));

        Assert.IsEmpty(reminders.List(null, true));
        Assert.AreEqual(0, notifications);
        Assert.IsFalse(delivery.Started.Task.IsCompleted);
    }

    [TestMethod]
    public async Task Retention_DeletedFiringAndCancelledDisposeWaiterKeepOriginalClaim()
    {
        var protection = ReadyRetention();
        using var clock = new ManualClock();
        var delivery = new HeldRetentionDelivery();
        await using var reminders = new AltaReminderService(
            new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection), clock, delivery);
        var created = reminders.Create(RetentionRequest());
        try
        {
            Assert.IsNull(protection.TryReserveRetirement("target"), "Pending reminder must already own its target.");
            using var unrelated = protection.TryReserveRetirement("unrelated");
            Assert.IsNotNull(unrelated, "Known reminder targets must not blanket-protect unrelated sessions.");
            await clock.NextTimerAsync();
            clock.Advance(TimeSpan.FromSeconds(60));
            await delivery.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(reminders.TryDelete(created.ReminderId, out _));
            Assert.IsEmpty(reminders.List(null, true));
            var originalDispose = reminders.DisposeAsync().AsTask();
            using var cancellation = new CancellationTokenSource();
            var waiter = originalDispose.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            Assert.IsFalse(originalDispose.IsCompleted);
            Assert.IsNull(protection.TryReserveRetirement("target"), "Removal and waiter cancellation cannot release captured delivery.");
        }
        finally
        {
            delivery.Release.TrySetResult();
        }
        await reminders.DisposeAsync();
        using var retired = protection.TryReserveRetirement("target");
        Assert.IsNotNull(retired);
    }

    [TestMethod]
    public async Task Retention_CreationObserverKeepsClaimAfterWorkerDisposal()
    {
        var protection = ReadyRetention();
        await using var reminders = new AltaReminderService(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reminders.Changed += (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var creation = Task.Run(() => reminders.Create(RetentionRequest()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await reminders.DisposeAsync();
            Assert.IsFalse(creation.IsCompleted);
            Assert.IsNull(protection.TryReserveRetirement("target"), "Creation is still executing its original observer pass.");
        }
        finally
        {
            release.TrySetResult();
        }
        await creation.WaitAsync(TimeSpan.FromSeconds(10));
        using var reservation = protection.TryReserveRetirement("target");
        Assert.IsNotNull(reservation);
    }

    [TestMethod]
    public async Task Retention_RepeatTimerKeepsClaimBetweenDeliveries()
    {
        var protection = ReadyRetention();
        using var clock = new ManualClock();
        var delivery = new LiteralDelivery();
        await using var reminders = new AltaReminderService(
            new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection), clock, delivery);
        var created = reminders.Create(RetentionRequest() with { Content = "original prompt" });
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        await delivery.NextAsync();
        await clock.NextTimerAsync();
        Assert.IsNull(protection.TryReserveRetirement("target"));
        Assert.AreEqual(1, reminders.List(null, true).Single().FiredCount);
        Assert.IsTrue(reminders.TryDelete(created.ReminderId, out _));
        await reminders.DisposeAsync();
        using var reservation = protection.TryReserveRetirement("target");
        Assert.IsNotNull(reservation);
    }

    [TestMethod]
    public async Task Retention_OverlappingRemindersReleaseOnlyAfterLastOriginal()
    {
        var protection = ReadyRetention();
        await using var reminders = new AltaReminderService(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection));
        var first = reminders.Create(RetentionRequest());
        var second = reminders.Create(RetentionRequest());
        Assert.IsTrue(reminders.TryDelete(first.ReminderId, out _));
        Assert.IsNull(protection.TryReserveRetirement("target"));
        Assert.IsTrue(reminders.TryDelete(second.ReminderId, out _));
        await reminders.DisposeAsync();
        using var retired = protection.TryReserveRetirement("target");
        Assert.IsNotNull(retired);
    }

    private static SessionRetentionProtection ReadyRetention()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], ready: true));
        return protection;
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task Retention_DeliveryInspectsOriginalAggregateBeforeReleasing(bool retained, bool pluginMarker, bool nestedMarker)
    {
        var protection = ReadyRetention();
        using var clock = new ManualClock();
        var original = new TaskCompletionSource<AltaReminderDeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception marker = pluginMarker
            ? new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object())
            : new AgentDependencyRetentionException("synthetic", "delivery dependency", [new Exception("dependency")], new object());
        if (nestedMarker) marker = new InvalidOperationException("wrapper", new AggregateException(new Exception("ordinary nested fault"), marker));
        original.SetException([new InvalidOperationException("ordinary first fault"), retained ? marker : new Exception("ordinary sibling")]);
        var reminders = new AltaReminderService(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection), clock,
            new FaultedRetentionDelivery(original.Task));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reminders.Changed += (_, _) =>
        {
            if (reminders.List(null, true).Any(item => item.State == AltaReminderStates.Completed)) completed.TrySetResult();
        };
        reminders.Create(RetentionRequest() with { RepeatCount = 1 });
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Exception? disposalFailure = null;
        try { await reminders.DisposeAsync(); }
        catch (Exception failure) { disposalFailure = failure; }
        Assert.AreEqual(retained, disposalFailure is not null);
        if (disposalFailure is not null) Assert.IsTrue(AltaRetentionAdmission.ContainsRetainedDependency(disposalFailure));
        using var reservation = protection.TryReserveRetirement("target");
        Assert.AreEqual(retained, reservation is null);
    }

    private sealed class FaultedRetentionDelivery(Task<AltaReminderDeliveryResult> original) : IAltaReminderDelivery
    {
        public Task<AltaReminderDeliveryResult> DeliverAsync(AltaReminderDescriptor reminder, string content) => original;
    }

    private static AltaReminderCreateRequest RetentionRequest() => new()
    {
        TargetSessionId = " target ", Content = "synthetic", Duration = TimeSpan.FromSeconds(60), RepeatCount = 2,
    };

    private sealed class HeldRetentionDelivery : IAltaReminderDelivery
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AltaReminderDeliveryResult> DeliverAsync(AltaReminderDescriptor reminder, string content)
        {
            Started.TrySetResult();
            await Release.Task;
            return new AltaReminderDeliveryResult(0, null, string.Empty);
        }
    }
}
