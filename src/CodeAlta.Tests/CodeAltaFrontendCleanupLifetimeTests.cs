using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaFrontendCleanupLifetimeTests
{
    [TestMethod]
    [DataRow(0, "disposeProjection")]
    [DataRow(1, "disposeReminderUi")]
    [DataRow(2, "persistViewState")]
    [DataRow(3, "disposeFileEditors")]
    [DataRow(4, "disposeRuntimeEventPump")]
    [DataRow(5, "disposeShellController")]
    [DataRow(6, "disposePromptDrafts")]
    [DataRow(-1, "disposeProjection")]
    public async Task Frontend_ValidatesCallbacks(int missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                recording.Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
                    missing is -1 or 0 ? null! : recording.DisposeProjection,
                    missing is -1 or 1 ? null! : recording.DisposeReminderUi,
                    missing is -1 or 2 ? null! : recording.PersistViewState,
                    missing is -1 or 3 ? null! : recording.DisposeFileEditors,
                    missing is -1 or 4 ? null! : recording.DisposeRuntimeEventPump,
                    missing is -1 or 5 ? null! : recording.DisposeShellController,
                    missing is -1 or 6 ? null! : recording.DisposePromptDrafts));
            });
            Assert.AreEqual(parameter, failure.ParamName);
            recording.AssertEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_PreservesSuccessfulOrder()
    {
        var recording = new Recording();
        try
        {
            // A typed result is deliberately discarded through Task, not interpreted as success.
            recording.Operations[2] = recording.Track(Task.FromResult(false));
            var disposal = recording.Dispose();
            Assert.IsTrue(disposal.IsCompletedSuccessfully);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(0, "ordinary")]
    [DataRow(0, "cancel")]
    [DataRow(0, "nested")]
    [DataRow(1, "ordinary")]
    [DataRow(1, "cancel")]
    [DataRow(1, "nested")]
    [DataRow(2, "ordinary")]
    [DataRow(2, "cancel")]
    [DataRow(2, "nested")]
    [DataRow(3, "ordinary")]
    [DataRow(3, "cancel")]
    [DataRow(3, "nested")]
    [DataRow(4, "ordinary")]
    [DataRow(4, "cancel")]
    [DataRow(4, "nested")]
    [DataRow(5, "ordinary")]
    [DataRow(5, "cancel")]
    [DataRow(5, "nested")]
    [DataRow(6, "ordinary")]
    [DataRow(6, "cancel")]
    [DataRow(6, "nested")]
    public async Task Frontend_SynchronousFailuresContinue(int stage, string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, stage);
            recording.SynchronousFailures[stage] = expected;
            var disposal = recording.Dispose();
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreEqual(kind == "cancel", disposal.IsCanceled);
            Assert.AreEqual(kind != "cancel", disposal.IsFaulted);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(2, "fault")]
    [DataRow(2, "cancel")]
    [DataRow(2, "faulted-oce")]
    [DataRow(2, "nested")]
    [DataRow(3, "fault")]
    [DataRow(3, "cancel")]
    [DataRow(3, "faulted-oce")]
    [DataRow(3, "nested")]
    [DataRow(4, "fault")]
    [DataRow(4, "cancel")]
    [DataRow(4, "faulted-oce")]
    [DataRow(4, "nested")]
    [DataRow(5, "fault")]
    [DataRow(5, "cancel")]
    [DataRow(5, "faulted-oce")]
    [DataRow(5, "nested")]
    [DataRow(6, "fault")]
    [DataRow(6, "cancel")]
    [DataRow(6, "faulted-oce")]
    [DataRow(6, "nested")]
    public async Task Frontend_ReturnedFailuresContinue(int stage, string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, stage);
            var original = kind == "cancel"
                ? recording.Operation(Task.CompletedTask, expected)
                : recording.Track(Task.FromException(expected));
            recording.Operations[stage] = original;
            Assert.AreEqual(kind == "cancel", original.IsCanceled);
            Assert.AreEqual(kind != "cancel", original.IsFaulted);
            var disposal = recording.Dispose();
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreSame(expected, await ObserveAsync(original));
            Assert.AreEqual(kind is "cancel" or "faulted-oce", disposal.IsCanceled);
            Assert.AreEqual(kind is "fault" or "nested", disposal.IsFaulted);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(2, "success")]
    [DataRow(2, "fault")]
    [DataRow(2, "cancel")]
    [DataRow(3, "success")]
    [DataRow(3, "fault")]
    [DataRow(3, "cancel")]
    [DataRow(4, "success")]
    [DataRow(4, "fault")]
    [DataRow(4, "cancel")]
    [DataRow(5, "success")]
    [DataRow(5, "fault")]
    [DataRow(5, "cancel")]
    [DataRow(6, "success")]
    [DataRow(6, "fault")]
    [DataRow(6, "cancel")]
    public async Task Frontend_PendingStagesAreJoined(int stage, string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var expected = outcome == "success" ? null : CreateFailure(outcome, stage);
            var original = recording.Operation(gate.Task, expected);
            recording.Operations[stage] = original;
            var disposal = recording.Dispose();
            Assert.IsFalse(original.IsCompleted);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertFrontendEvents(stage + 1);
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreSame(expected, await ObserveAsync(original));
            Assert.AreEqual(outcome == "cancel", disposal.IsCanceled);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(2, "ordinary")]
    [DataRow(2, "cancel")]
    [DataRow(3, "ordinary")]
    [DataRow(3, "cancel")]
    [DataRow(4, "ordinary")]
    [DataRow(4, "cancel")]
    [DataRow(5, "ordinary")]
    [DataRow(5, "cancel")]
    [DataRow(6, "ordinary")]
    [DataRow(6, "cancel")]
    public async Task Frontend_EarlierFailureStillJoinsPendingStage(int stage, string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, 0);
            recording.SynchronousFailures[0] = expected;
            var gate = recording.Gate();
            var original = recording.Operation(gate.Task);
            recording.Operations[stage] = original;
            var disposal = recording.Dispose();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(original.IsCompleted);
            recording.AssertFrontendEvents(stage + 1);
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.IsNull(await ObserveAsync(original));
            Assert.AreEqual(kind == "cancel", disposal.IsCanceled);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("cancel")]
    [DataRow("mixed-nested")]
    public async Task Frontend_OrdersMultipleDirectFailures(string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = new Exception[7];
            for (var stage = 0; stage < expected.Length; stage++)
            {
                var stageKind = kind == "mixed-nested" ? (stage % 2 == 0 ? "nested" : "cancel") : kind;
                expected[stage] = CreateFailure(stageKind, stage);
                if (stage < 2) recording.SynchronousFailures[stage] = expected[stage];
                else recording.Operations[stage] = recording.Operation(Task.CompletedTask, expected[stage]);
            }
            var disposal = recording.Dispose();
            AssertAggregate(await ObserveAsync(disposal), expected);
            Assert.IsTrue(disposal.IsFaulted);
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_StartsInline()
    {
        var recording = new Recording();
        try
        {
            var threads = new int[7];
            recording.OnEntry = stage => threads[stage] = Environment.CurrentManagedThreadId;
            var callerThread = Environment.CurrentManagedThreadId;
            var disposal = recording.Dispose();
            Assert.IsTrue(disposal.IsCompletedSuccessfully);
            foreach (var thread in threads) Assert.AreEqual(callerThread, thread);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertFrontendEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_AwaitPreservesFrontendContext()
    {
        var recording = new Recording();
        TaskCompletionSource<bool>? gate = null;
        SinglePostContext? context = null;
        Task? disposal = null;
        Task? dispatch = null;
        try
        {
            gate = recording.Gate();
            context = new SinglePostContext(recording);
            recording.Operations[2] = recording.Operation(gate.Task);
            var contexts = new SynchronizationContext?[7];
            recording.OnEntry = stage => contexts[stage] = SynchronizationContext.Current;
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                disposal = recording.Dispose();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertFrontendEvents(3);
            gate.TrySetResult(true);
            dispatch = recording.Track(context.InvokePostedAsync());
            Assert.IsNull(await ObserveAsync(dispatch));
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.AreEqual(1, context.PostCount);
            foreach (var observed in contexts) Assert.AreSame(context, observed);
            recording.AssertFrontendEvents();
        }
        finally
        {
            gate?.TrySetResult(true);
            try
            {
                if (context is not null)
                {
                    if (disposal is null || disposal.IsCompleted) context.CompleteWithoutPost();
                    dispatch ??= recording.Track(context.InvokePostedAsync());
                }
            }
            finally { await recording.FinishAsync(); }
        }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("cancel")]
    [DataRow("nested")]
    public async Task Deferred_FrontendFailureDoesNotSkipFinalStages(string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, 0);
            recording.SynchronousFailures[0] = expected;
            var unselectedFailure = new InvalidOperationException("unselected startup");
            var startup = recording.Track(Task.FromException<RecordingApp>(unselectedFailure));
            Task? frontend = null;
            var app = new RecordingApp(recording, () =>
            {
                var task = recording.Dispose();
                Assert.IsNull(frontend, "Unexpected duplicate frontend cleanup.");
                frontend = task;
                return new ValueTask(task);
            });
            var disposal = recording.Deferred(app, startup);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreSame(expected, await ObserveAsync(frontend ?? throw new AssertFailedException("Frontend not reached.")));
            Assert.AreSame(unselectedFailure, await ObserveAsync(startup));
            Assert.AreEqual(kind == "cancel", disposal.IsCanceled);
            Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertDeferredEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Deferred_PreservesFrontendAggregateAsDirectStageError()
    {
        var recording = new Recording();
        try
        {
            var projection = CreateFailure("nested", 0);
            var drafts = CreateFailure("cancel", 6);
            recording.SynchronousFailures[0] = projection;
            recording.Operations[6] = recording.Operation(Task.CompletedTask, drafts);
            var update = new OperationCanceledException("update");
            var presenter = new InvalidOperationException("presenter");
            var cancellation = new AggregateException(new InvalidOperationException("startup-cts"));
            recording.FinalFailures[0] = update;
            recording.FinalFailures[1] = presenter;
            recording.FinalFailures[2] = cancellation;
            var startup = recording.Track(Task.FromException<RecordingApp>(new InvalidOperationException("unselected startup")));
            Task? frontend = null;
            var app = new RecordingApp(recording, () =>
            {
                var task = recording.Dispose();
                Assert.IsNull(frontend, "Unexpected duplicate frontend cleanup.");
                frontend = task;
                return new ValueTask(task);
            });
            var disposal = recording.Deferred(app, startup);
            var failure = await ObserveAsync(disposal);
            var frontendFailure = await ObserveAsync(frontend ?? throw new AssertFailedException("Frontend not reached."));
            AssertAggregate(frontendFailure, projection, drafts);
            AssertAggregate(failure, frontendFailure ?? throw new AssertFailedException("Missing frontend aggregate."), update, presenter, cancellation);
            Assert.IsTrue(disposal.IsFaulted);
            Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertDeferredEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    private static string StageName(int stage) => stage switch
    {
        0 => "projection",
        1 => "reminder-ui",
        2 => "persistence",
        3 => "editors",
        4 => "event-pump",
        5 => "controller",
        6 => "drafts",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static Exception CreateFailure(string kind, int stage) => kind switch
    {
        "ordinary" or "fault" => new InvalidOperationException(StageName(stage)),
        "cancel" or "faulted-oce" => new OperationCanceledException(StageName(stage)),
        "nested" => new AggregateException(new InvalidOperationException(StageName(stage)), new AggregateException(new Exception("nested"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertAggregate(Exception? actual, params Exception[] expected)
    {
        Assert.IsInstanceOfType<AggregateException>(actual);
        var aggregate = (AggregateException)actual;
        Assert.AreEqual(expected.Length, aggregate.InnerExceptions.Count);
        for (var i = 0; i < expected.Length; i++) Assert.AreSame(expected[i], aggregate.InnerExceptions[i]);
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException)
        {
            // Independently observe a terminal original, but never rehabilitate the timeout.
            if (task.IsCompleted)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception) { /* The timeout remains the observation failure. */ }
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

    private static async Task CompleteAsync(Task gate, Exception? failure)
    {
        await gate.ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    // All operations are inert and instance-owned. There are no fixture CTS or registrations.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();

        public Exception?[] SynchronousFailures { get; } = new Exception?[7];
        public Task?[] Operations { get; } = new Task?[7];
        public Exception?[] FinalFailures { get; } = new Exception?[3];
        public Action<int>? OnEntry { get; set; }

        public void Record(string stage) => _events.Enqueue(stage);
        public Task Track(Task task) { _tasks.Enqueue(task); return task; }
        public Task<T> Track<T>(Task<T> task) { _tasks.Enqueue(task); return task; }
        public void AssertEvents(params string[] expected) => CollectionAssert.AreEqual(expected, _events.ToArray());
        public void AssertFrontendEvents(int count = 7) => AssertEvents(Enumerable.Range(0, count).Select(StageName).ToArray());
        public void AssertDeferredEvents()
            => AssertEvents(["startup-cancel", "app", .. Enumerable.Range(0, 7).Select(StageName), "update", "presenter", "startup-cts"]);

        public TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(gate);
            Track(gate.Task);
            return gate;
        }

        public Task Operation(Task gate, Exception? failure = null) => Track(CompleteAsync(gate, failure));

        private void Enter(int stage)
        {
            Record(StageName(stage));
            OnEntry?.Invoke(stage);
            if (SynchronousFailures[stage] is { } failure) ExceptionDispatchInfo.Throw(failure);
        }

        public void DisposeProjection() => Enter(0);
        public void DisposeReminderUi() => Enter(1);
        public Task PersistViewState() { Enter(2); return Operations[2] ?? Task.CompletedTask; }
        public ValueTask DisposeFileEditors() { Enter(3); return new(Operations[3] ?? Task.CompletedTask); }
        public ValueTask DisposeRuntimeEventPump() { Enter(4); return new(Operations[4] ?? Task.CompletedTask); }
        public ValueTask DisposeShellController() { Enter(5); return new(Operations[5] ?? Task.CompletedTask); }
        public ValueTask DisposePromptDrafts() { Enter(6); return new(Operations[6] ?? Task.CompletedTask); }

        public Task Dispose() => Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
            DisposeProjection, DisposeReminderUi, PersistViewState, DisposeFileEditors,
            DisposeRuntimeEventPump, DisposeShellController, DisposePromptDrafts));

        // This is core-to-Deferred integration through an inert app, not actual App/Shell traversal.
        public Task Deferred(RecordingApp app, Task<RecordingApp> startup)
            => Track(DeferredCodeAltaApp.DisposeDeferredStartupAsync(
                app, startup, null, CancellationToken.None,
                () => Record("startup-cancel"),
                () => { FinalStage(0, "update"); return ValueTask.CompletedTask; },
                () => FinalStage(1, "presenter"),
                () => FinalStage(2, "startup-cts")));

        private void FinalStage(int stage, string name)
        {
            Record(name);
            if (FinalFailures[stage] is { } failure) ExceptionDispatchInfo.Throw(failure);
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

            // Callback-created work is retained before assertions; callbacks create no new gates.
            // Launch ALL independent observations before each aggregate, including this finite pass.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try
            {
                var observations = finalTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex) { finalFailure = ex; }

            if (firstFailure is not null && finalFailure is not null) throw new AggregateException(firstFailure, finalFailure);
            if (firstFailure is not null) ExceptionDispatchInfo.Throw(firstFailure);
            if (finalFailure is not null) ExceptionDispatchInfo.Throw(finalFailure);
        }
    }

    private sealed class RecordingApp(Recording recording, Func<ValueTask> dispose) : IAsyncDisposable
    {
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            recording.Record("app");
            return dispose();
        }
    }

    // One retained signal and one explicitly awaited callback, not a UI dispatcher pump.
    private sealed class SinglePostContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<(SendOrPostCallback Callback, object? State)> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _postCount;
        public SinglePostContext(Recording recording) => recording.Track(_posted.Task);
        public int PostCount => Volatile.Read(ref _postCount);
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _posted.TrySetResult((d, state));
        }
        public void CompleteWithoutPost() => _posted.TrySetResult((static _ => { }, null));
        public async Task InvokePostedAsync()
        {
            var posted = await _posted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
