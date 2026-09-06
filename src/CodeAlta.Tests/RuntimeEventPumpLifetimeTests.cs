using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

[TestClass]
public sealed class RuntimeEventPumpLifetimeTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(-1)]
    public async Task Core_ValidatesCallbacks(int missing)
    {
        var recording = new Recording();
        try
        {
            var calls = 0;
            Action operation = () => calls++;
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                // Deliberate nulls exercise the mandatory nonnullable callback contract.
                var task = RuntimeEventPump.DisposePumpAsync(null,
                    missing is -1 or 0 ? null! : operation,
                    missing is -1 or 1 ? null! : operation,
                    missing is -1 or 2 ? null! : operation,
                    missing is -1 or 3 ? null! : operation);
                recording.Track(task);
            });
            var parameters = new[] { "cancelDisposal", "cancelPump", "disposePumpCancellation", "disposeDisposalCancellation" };
            Assert.AreEqual(parameters[Math.Max(0, missing)], failure.ParamName);
            Assert.AreEqual(0, calls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_NoTaskStillAttemptsAllCallbacks()
    {
        var recording = new Recording();
        try
        {
            var disposal = recording.Core(null);
            Assert.IsTrue(disposal.IsCompletedSuccessfully);
            Assert.IsNull(await recording.ObserveAsync(disposal));
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false, "success")]
    [DataRow(false, "fault")]
    [DataRow(false, "canceled")]
    [DataRow(false, "faulted-oce")]
    [DataRow(false, "nested")]
    [DataRow(true, "success")]
    [DataRow(true, "fault")]
    [DataRow(true, "canceled")]
    [DataRow(true, "faulted-oce")]
    [DataRow(true, "nested")]
    public async Task Core_JoinsOriginalBeforeRelease(bool pending, string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            if (!pending) gate.TrySetResult(true);
            var expected = OriginalFailure(outcome);
            var original = recording.Original(gate.Task, outcome, expected);
            var joinedAtRelease = new bool[2];
            recording.OnEntry = stage => { if (stage >= 2) joinedAtRelease[stage - 2] = original.IsCompleted; };
            var disposal = recording.Core(original);
            if (pending)
            {
                Assert.IsFalse(disposal.IsCompleted);
                Assert.IsFalse(original.IsCompleted);
                recording.AssertStages(2);
            }
            else Assert.IsTrue(disposal.IsCompleted);
            gate.TrySetResult(true);
            var failure = await recording.ObserveAsync(disposal);
            Assert.AreSame(RetainsJoinFailure(outcome) ? expected : null, failure);
            Assert.AreEqual(RetainsJoinFailure(outcome), disposal.IsFaulted);
            Assert.IsFalse(disposal.IsCanceled);
            AssertOriginal(original, await recording.ObserveAsync(original), outcome, expected);
            CollectionAssert.AreEqual(new[] { true, true }, joinedAtRelease);
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(0, "ordinary")]
    [DataRow(0, "oce")]
    [DataRow(0, "nested")]
    [DataRow(1, "ordinary")]
    [DataRow(1, "oce")]
    [DataRow(1, "nested")]
    [DataRow(2, "ordinary")]
    [DataRow(2, "oce")]
    [DataRow(2, "nested")]
    [DataRow(3, "ordinary")]
    [DataRow(3, "oce")]
    [DataRow(3, "nested")]
    public async Task Core_CallbackFailuresContinue(int stage, string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, StageName(stage));
            recording.CallbackFailures[stage] = expected;
            var original = recording.Track(Task.CompletedTask);
            var disposal = recording.Core(original);
            Assert.AreSame(expected, await recording.ObserveAsync(disposal));
            Assert.AreEqual(kind == "oce", disposal.IsCanceled);
            Assert.AreEqual(kind != "oce", disposal.IsFaulted);
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(0, "success")]
    [DataRow(0, "fault")]
    [DataRow(0, "canceled")]
    [DataRow(0, "faulted-oce")]
    [DataRow(0, "nested")]
    [DataRow(1, "success")]
    [DataRow(1, "fault")]
    [DataRow(1, "canceled")]
    [DataRow(1, "faulted-oce")]
    [DataRow(1, "nested")]
    public async Task Core_CancelFailureStillJoinsPendingOriginal(int stage, string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var expected = OriginalFailure(outcome);
            var original = recording.Original(gate.Task, outcome, expected);
            var cancellation = CreateFailure("ordinary", StageName(stage));
            recording.CallbackFailures[stage] = cancellation;
            var joinedAtRelease = new bool[2];
            recording.OnEntry = index => { if (index >= 2) joinedAtRelease[index - 2] = original.IsCompleted; };
            var disposal = recording.Core(original);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertStages(2);
            gate.TrySetResult(true);
            var failure = await recording.ObserveAsync(disposal);
            if (RetainsJoinFailure(outcome)) AssertAggregate(failure, cancellation, expected);
            else Assert.AreSame(cancellation, failure);
            Assert.IsTrue(disposal.IsFaulted);
            AssertOriginal(original, await recording.ObserveAsync(original), outcome, expected);
            CollectionAssert.AreEqual(new[] { true, true }, joinedAtRelease);
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("nested")]
    [DataRow("mixed-oce")]
    [DataRow("repeated")]
    public async Task Core_OrdersDirectFailures(string kind)
    {
        var recording = new Recording();
        try
        {
            var shared = CreateFailure("ordinary", "same reference");
            for (var i = 0; i < 4; i++)
                recording.CallbackFailures[i] = kind == "repeated" ? shared
                    : CreateFailure(kind == "mixed-oce" ? (i == 2 ? "nested" : "oce") : kind, StageName(i));
            var expected = kind == "repeated" ? shared
                : CreateFailure(kind == "mixed-oce" ? "oce" : kind, "original");
            var outcome = kind == "mixed-oce" ? "faulted-oce" : kind == "nested" ? "nested" : "fault";
            var original = recording.Original(Task.CompletedTask, outcome, expected);
            var disposal = recording.Core(original);
            var failure = await recording.ObserveAsync(disposal);
            var callbacks = recording.CallbackFailures;
            if (kind == "mixed-oce") AssertAggregate(failure, callbacks[0], callbacks[1], callbacks[2], callbacks[3]);
            else AssertAggregate(failure, callbacks[0], callbacks[1], expected, callbacks[2], callbacks[3]);
            Assert.IsTrue(disposal.IsFaulted);
            AssertOriginal(original, await recording.ObserveAsync(original), outcome, expected);
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    public async Task Core_LinkedCancellationCallbackFailureStillJoins(string outcome)
    {
        var recording = new Recording();
        try
        {
            // Only these three rows allocate CTS/registrations. Retain every source first; both
            // parents are uncanceled when registering. No external cancellation caller races us.
            var external = recording.NewSource();
            var disposalSource = recording.NewSource();
            var linked = recording.NewLinkedSource(external.Token, disposalSource.Token);
            var token = linked.Token;
            var gate = recording.Gate();
            var expected = OriginalFailure(outcome);
            var original = recording.Original(gate.Task, outcome, expected, token);
            var callbackFailure = CreateFailure("ordinary", "linked callback");
            var callbackCalls = 0;
            Exception? ownCancellationFailure = null;
            Assert.IsFalse(external.IsCancellationRequested);
            Assert.IsFalse(disposalSource.IsCancellationRequested);
            Assert.IsFalse(linked.IsCancellationRequested);
            recording.Register(token, () =>
            {
                callbackCalls++;
                recording.Record("linked-callback");
                throw callbackFailure;
            });
            recording.Callbacks[0] = () =>
            {
                try { disposalSource.Cancel(); }
                catch (Exception ex) { ownCancellationFailure = ex; throw; }
            };
            recording.Callbacks[1] = linked.Cancel;
            var joinedAtRelease = new bool[2];
            recording.OnEntry = stage => { if (stage >= 2) joinedAtRelease[stage - 2] = original.IsCompleted; };
            var disposal = recording.Core(original);
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(original.IsCompleted);
            Assert.IsTrue(disposalSource.IsCancellationRequested);
            Assert.IsTrue(linked.IsCancellationRequested);
            Assert.IsFalse(external.IsCancellationRequested);
            recording.AssertEvents("cancel-disposal", "linked-callback", "cancel-pump");
            gate.TrySetResult(true);
            var failure = await recording.ObserveAsync(disposal);
            var own = Assert.IsInstanceOfType<AggregateException>(ownCancellationFailure);
            Assert.AreEqual(1, own.InnerExceptions.Count);
            AssertAggregate(own.InnerExceptions[0], callbackFailure);
            if (outcome == "fault") AssertAggregate(failure, own, expected);
            else Assert.AreSame(own, failure);
            Assert.IsTrue(disposal.IsFaulted);
            Assert.AreEqual(1, callbackCalls);
            AssertOriginal(original, await recording.ObserveAsync(original), outcome, expected);
            CollectionAssert.AreEqual(new[] { true, true }, joinedAtRelease);
            recording.AssertEvents("cancel-disposal", "linked-callback", "cancel-pump", "release-pump", "release-disposal");
            // Release-stage callbacks only recorded. Real registrations/sources belong to FinishAsync.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_StartsInline()
    {
        var recording = new Recording();
        try
        {
            var threads = new int[4];
            recording.OnEntry = stage => threads[stage] = Environment.CurrentManagedThreadId;
            var caller = Environment.CurrentManagedThreadId;
            var disposal = recording.Core(recording.Track(Task.CompletedTask));
            Assert.IsTrue(disposal.IsCompletedSuccessfully);
            foreach (var thread in threads) Assert.AreEqual(caller, thread);
            Assert.IsNull(await recording.ObserveAsync(disposal));
            recording.AssertStages();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("oce")]
    [DataRow("nested")]
    public async Task Frontend_PumpFailureStillAttemptsLaterStages(string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, "cancel-disposal");
            recording.CallbackFailures[0] = expected;
            var original = recording.Track(Task.CompletedTask);
            Task? pump = null;
            var frontend = recording.Frontend(() =>
            {
                var task = recording.Core(original);
                Assert.IsNull(pump, "Unexpected duplicate pump cleanup.");
                pump = task;
                return new ValueTask(task);
            });
            Assert.AreSame(expected, await recording.ObserveAsync(frontend));
            Assert.AreSame(expected, await recording.ObserveAsync(pump ?? throw new AssertFailedException("Pump not reached.")));
            Assert.AreEqual(kind == "oce", frontend.IsCanceled);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_PreservesPumpAggregateAsDirectStageError()
    {
        var recording = new Recording();
        try
        {
            var cancellation = CreateFailure("nested", "cancel-disposal");
            var expected = CreateFailure("ordinary", "original");
            var release = CreateFailure("oce", "release-disposal");
            var original = recording.Original(Task.CompletedTask, "fault", expected);
            recording.CallbackFailures[0] = cancellation;
            recording.CallbackFailures[3] = release;
            var projection = CreateFailure("ordinary", "projection");
            var controller = CreateFailure("oce", "controller");
            recording.FrontendFailures[0] = projection;
            recording.FrontendFailures[5] = controller;
            Task? pump = null;
            var frontend = recording.Frontend(() =>
            {
                var task = recording.Core(original);
                Assert.IsNull(pump, "Unexpected duplicate pump cleanup.");
                pump = task;
                return new ValueTask(task);
            });
            var failure = await recording.ObserveAsync(frontend);
            var pumpFailure = await recording.ObserveAsync(pump ?? throw new AssertFailedException("Pump not reached."));
            AssertAggregate(pumpFailure, cancellation, expected, release);
            AssertAggregate(failure, projection, pumpFailure, controller);
            Assert.IsTrue(frontend.IsFaulted);
            AssertOriginal(original, await recording.ObserveAsync(original), "fault", expected);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_PendingPumpPreservesContextBoundary()
    {
        var recording = new Recording();
        TaskCompletionSource<bool>? gate = null;
        SinglePostContext? context = null;
        Task? frontend = null;
        Task? dispatch = null;
        try
        {
            gate = recording.Gate();
            var original = recording.Original(gate.Task, "success", null);
            context = new SinglePostContext(recording);
            var pumpContexts = new SynchronizationContext?[4];
            var frontendContexts = new SynchronizationContext?[7];
            recording.OnEntry = stage => pumpContexts[stage] = SynchronizationContext.Current;
            recording.OnFrontendEntry = stage => frontendContexts[stage] = SynchronizationContext.Current;
            Task? pump = null;
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                frontend = recording.Frontend(() =>
                {
                    var task = recording.Core(original);
                    Assert.IsNull(pump, "Unexpected duplicate pump cleanup.");
                    pump = task;
                    return new ValueTask(task);
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.IsFalse(frontend.IsCompleted);
            recording.AssertEvents("projection", "reminder-ui", "persistence", "editors", "pump", "cancel-disposal", "cancel-pump");
            gate.TrySetResult(true);
            dispatch = recording.Track(context.InvokePostedAsync());
            var pumpTask = pump ?? throw new AssertFailedException("Pump not reached.");
            var observations = new[]
            {
                recording.ObserveAsync(dispatch),
                recording.ObserveAsync(frontend),
                recording.ObserveAsync(pumpTask),
            };
            foreach (var failure in await Task.WhenAll(observations)) Assert.IsNull(failure);
            Assert.AreEqual(1, context.PostCount);
            Assert.AreSame(context, pumpContexts[0]);
            Assert.AreSame(context, pumpContexts[1]);
            Assert.AreNotSame(context, pumpContexts[2]);
            Assert.AreNotSame(context, pumpContexts[3]);
            foreach (var observed in frontendContexts) Assert.AreSame(context, observed);
            recording.AssertFrontendEvents();
        }
        finally
        {
            gate?.TrySetResult(true);
            try
            {
                if (context is not null)
                {
                    if (frontend is null || frontend.IsCompleted) context.CompleteWithoutPost();
                    dispatch ??= recording.Track(context.InvokePostedAsync());
                }
            }
            finally { await recording.FinishAsync(); }
        }
    }

    private static string StageName(int stage) => stage switch
    {
        0 => "cancel-disposal",
        1 => "cancel-pump",
        2 => "release-pump",
        3 => "release-disposal",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static Exception CreateFailure(string kind, string stage) => kind switch
    {
        "ordinary" or "fault" => new InvalidOperationException(stage),
        "oce" or "faulted-oce" => new OperationCanceledException(stage),
        "nested" => new AggregateException(new InvalidOperationException(stage), new AggregateException(new OperationCanceledException("nested"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Exception? OriginalFailure(string outcome)
        => outcome is "success" or "canceled" ? null : CreateFailure(outcome, "original");

    private static bool RetainsJoinFailure(string outcome) => outcome is "fault" or "nested";

    private static void AssertOriginal(Task original, Exception? observed, string outcome, Exception? expected)
    {
        if (outcome == "canceled") Assert.IsInstanceOfType<OperationCanceledException>(observed);
        else Assert.AreSame(expected, observed);
        Assert.AreEqual(outcome == "canceled", original.IsCanceled);
        Assert.AreEqual(outcome is "fault" or "faulted-oce" or "nested", original.IsFaulted);
    }

    private static void AssertAggregate(Exception? actual, params Exception?[] expected)
    {
        var aggregate = Assert.IsInstanceOfType<AggregateException>(actual);
        Assert.AreEqual(expected.Length, aggregate.InnerExceptions.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.IsNotNull(expected[i]);
            Assert.AreSame(expected[i], aggregate.InnerExceptions[i]);
        }
    }

    private static async Task CompleteOriginalAsync(Task gate, TaskCompletionSource<bool> completion,
        string outcome, Exception? failure, CancellationToken token)
    {
        await gate.ConfigureAwait(false);
        switch (outcome)
        {
            case "success": completion.TrySetResult(true); break;
            case "canceled": completion.TrySetCanceled(token); break;
            case "fault" or "faulted-oce" or "nested":
                completion.TrySetException(failure ?? new AssertFailedException("Missing synthetic failure."));
                break;
            default: completion.TrySetException(new AssertFailedException("Unknown synthetic outcome.")); break;
        }
    }

    // All state is instance-owned. Release callbacks record/fail; real CTS are fixture-owned and
    // released only after successful observations and joins, with no earlier body timeout.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();
        private readonly ConcurrentQueue<TimeoutException> _timeouts = new();
        private readonly List<CancellationTokenSource> _sources = [];
        private readonly List<CancellationTokenRegistration> _registrations = [];

        public Exception?[] CallbackFailures { get; } = new Exception?[4];
        public Action?[] Callbacks { get; } = new Action?[4];
        public Exception?[] FrontendFailures { get; } = new Exception?[7];
        public Action<int>? OnEntry { get; set; }
        public Action<int>? OnFrontendEntry { get; set; }

        public void Record(string stage) => _events.Enqueue(stage);
        public Task Track(Task task) { _tasks.Enqueue(task); return task; }
        public void AssertEvents(params string[] expected) => CollectionAssert.AreEqual(expected, _events.ToArray());
        public void AssertStages(int count = 4) => AssertEvents(Enumerable.Range(0, count).Select(StageName).ToArray());
        public void AssertFrontendEvents()
            => AssertEvents(["projection", "reminder-ui", "persistence", "editors", "pump", .. Enumerable.Range(0, 4).Select(StageName), "controller", "drafts"]);

        public TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(gate);
            Track(gate.Task);
            return gate;
        }

        public Task Original(Task gate, string outcome, Exception? failure, CancellationToken token = default)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Track(completion.Task);
            Track(CompleteOriginalAsync(gate, completion, outcome, failure, token));
            return task;
        }

        public CancellationTokenSource NewSource()
        {
            var source = new CancellationTokenSource();
            _sources.Add(source);
            return source;
        }

        public CancellationTokenSource NewLinkedSource(CancellationToken external, CancellationToken disposal)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(external, disposal);
            _sources.Add(source);
            return source;
        }

        public void Register(CancellationToken token, Action callback) => _registrations.Add(token.Register(callback));

        private void Enter(int stage)
        {
            Record(StageName(stage));
            OnEntry?.Invoke(stage);
            Callbacks[stage]?.Invoke();
            if (CallbackFailures[stage] is { } failure) ExceptionDispatchInfo.Throw(failure);
        }

        public Task Core(Task? original) => Track(RuntimeEventPump.DisposePumpAsync(original,
            () => Enter(0), () => Enter(1), () => Enter(2), () => Enter(3)));

        // Static-core integration only, not concrete App/Shell disposal or event delivery.
        public Task Frontend(Func<ValueTask> pump) => Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
            () => FrontendStage(0, "projection"),
            () => FrontendStage(1, "reminder-ui"),
            () => { FrontendStage(2, "persistence"); return Task.CompletedTask; },
            () => { FrontendStage(3, "editors"); return ValueTask.CompletedTask; },
            () => { FrontendStage(4, "pump"); return pump(); },
            () => { FrontendStage(5, "controller"); return ValueTask.CompletedTask; },
            () => { FrontendStage(6, "drafts"); return ValueTask.CompletedTask; }));

        private void FrontendStage(int stage, string name)
        {
            Record(name);
            OnFrontendEntry?.Invoke(stage);
            if (FrontendFailures[stage] is { } failure) ExceptionDispatchInfo.Throw(failure);
        }

        public async Task<Exception?> ObserveAsync(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return null;
            }
            catch (TimeoutException ex)
            {
                // Includes body and context-dispatch observations, not just FinishAsync passes.
                _timeouts.Enqueue(ex);
                if (task.IsCompleted)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception) { /* Independently observed; timeout is never rehabilitated. */ }
                }
                throw;
            }
            catch (Exception) when (task.IsCompleted)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception original) { return original; }
                return null;
            }
        }

        public async Task FinishAsync()
        {
            foreach (var gate in _gates) gate.TrySetResult(true);
            var firstTasks = _tasks.ToArray();
            Exception? firstFailure = null;
            try
            {
                var observations = firstTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex) { firstFailure = ex; }

            // Callback-produced work is retained before assertions; callbacks create no new gates.
            // Start ALL independent observations before each aggregate, including this finite pass.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try
            {
                var observations = finalTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex) { finalFailure = ex; }

            if (firstFailure is null && finalFailure is null && _timeouts.IsEmpty &&
                firstTasks.All(static task => task.IsCompleted) && finalTasks.All(static task => task.IsCompleted))
            {
                foreach (var registration in _registrations) registration.Dispose();
                // Linked source first, then its two retained parents. No external Cancel caller exists.
                for (var i = _sources.Count - 1; i >= 0; i--) _sources[i].Dispose();
            }

            // Keep every timeout, including those already thrown from the body. Successful later
            // observations cannot permit source release or erase an earlier observation failure.
            List<Exception> failures = [.. _timeouts];
            if (firstFailure is not null and not TimeoutException) failures.Add(firstFailure);
            if (finalFailure is not null and not TimeoutException) failures.Add(finalFailure);
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    // One retained inert signal and explicitly awaited dispatch; no terminal dispatcher or pump.
    private sealed class SinglePostContext : SynchronizationContext
    {
        private readonly Recording _recording;
        private readonly TaskCompletionSource<(SendOrPostCallback Callback, object? State)> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _postCount;

        public SinglePostContext(Recording recording)
        {
            _recording = recording;
            recording.Track(_posted.Task);
        }

        public int PostCount => Volatile.Read(ref _postCount);
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _posted.TrySetResult((d, state));
        }

        public void CompleteWithoutPost() => _posted.TrySetResult((static _ => { }, null));
        public async Task InvokePostedAsync()
        {
            var failure = await _recording.ObserveAsync(_posted.Task).ConfigureAwait(false);
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            var posted = await _posted.Task.ConfigureAwait(false);
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                posted.Callback(posted.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
