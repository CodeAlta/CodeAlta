using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

/// <summary>Inert reminder-first routing and frontend-context seam tests, not concrete owner tests.</summary>
/// <remarks>
/// Only the future routing core and accepted seven-stage static frontend core are called.
/// No Shell/App/service/CTS/registry/provider/UI/State/host/logger/version is constructed.
/// This does not qualify failed acquisition, fallback ownership, CRUD notification drains,
/// posted UI/dialog/output work, detached sends/providers, lower owners or full quiescence.
/// Future ownership is limited to frontend-created service workers of a successfully returned
/// App; these supplied-task cases do not prove actual BCL cancellation races.
/// Pending operations remain unbounded in production; five-second bounds are fixture failures,
/// never a shutdown policy or authority to release an actual resource. M2-M7 remain open.
/// </remarks>
[TestClass]
public sealed class ReminderShutdownRoutingTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Cleanup_ValidatesBeforeCallbacks(int nullMask)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var calls = 0;
            Func<ValueTask> callback = () => { calls++; return ValueTask.CompletedTask; };
            var error = Assert.Throws<ArgumentNullException>(() => Route(scope,
                (nullMask & 1) != 0 ? null! : callback,
                (nullMask & 2) != 0 ? null! : callback));
            Assert.AreEqual((nullMask & 1) != 0 ? "disposeReminders" : "disposeExisting", error.ParamName);
            Assert.AreEqual(0, calls);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success", "success")]
    [DataRow("sync-fault", "success")]
    [DataRow("fault", "success")]
    [DataRow("canceled", "success")]
    [DataRow("faulted-oce", "success")]
    [DataRow("success", "sync-fault")]
    [DataRow("success", "fault")]
    [DataRow("success", "canceled")]
    [DataRow("success", "faulted-oce")]
    [DataRow("fault", "nested")]
    [DataRow("sync-oce", "sync-nested")]
    public async Task TerminalReminderFailureStillAttemptsExistingCleanup(string reminderOutcome, string existingOutcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var reminderError = ReminderTaskScope.Failure(reminderOutcome, "reminder");
            var existingError = ReminderTaskScope.Failure(existingOutcome, "existing");
            var reminderOriginal = reminderOutcome.StartsWith("sync-", StringComparison.Ordinal)
                ? null : scope.Original(reminderOutcome, reminderError);
            var existingOriginal = existingOutcome.StartsWith("sync-", StringComparison.Ordinal)
                ? null : scope.Original(existingOutcome, existingError);
            var events = new List<string>();
            var core = Route(scope,
                () =>
                {
                    events.Add("reminder");
                    if (reminderOriginal is null) throw reminderError!;
                    return new ValueTask(reminderOriginal);
                },
                () =>
                {
                    events.Add("existing");
                    if (existingOriginal is null) throw existingError!;
                    return new ValueTask(existingOriginal);
                });
            List<Task> tasks = [core];
            if (reminderOriginal is not null) tasks.Add(reminderOriginal);
            if (existingOriginal is not null) tasks.Add(existingOriginal);
            var errors = await scope.ObserveManyAsync(tasks.ToArray());
            CollectionAssert.AreEqual(new[] { "reminder", "existing" }, events);
            var index = 1;
            if (reminderOriginal is not null)
                ReminderTaskScope.AssertOriginal(reminderOriginal, errors[index++], reminderOutcome, reminderError);
            if (existingOriginal is not null)
                ReminderTaskScope.AssertOriginal(existingOriginal, errors[index], existingOutcome, existingError);
            if (reminderOutcome != "success" && existingOutcome != "success")
                ReminderTaskScope.AssertAggregate(errors[0], reminderError!, existingError!);
            else if (reminderOutcome == "canceled" || existingOutcome == "canceled")
                ReminderTaskScope.AssertCancellation(errors[0]);
            else Assert.AreSame(reminderError ?? existingError, errors[0]);
            Assert.AreEqual((reminderOutcome == "success" || existingOutcome == "success") &&
                (reminderOutcome is "canceled" or "faulted-oce" or "sync-oce" ||
                 existingOutcome is "canceled" or "faulted-oce" or "sync-oce"), core.IsCanceled);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    public async Task PendingReminderCleanupBlocksExistingTraversal()
    {
        var scope = new ReminderTaskScope();
        try
        {
            var reminder = scope.Gate();
            var existing = scope.Track(Task.CompletedTask);
            var calls = 0;
            var core = Route(scope, () => new ValueTask(reminder.Task),
                () => { Interlocked.Increment(ref calls); return new ValueTask(existing); });
            Assert.IsFalse(core.IsCompleted);
            Assert.AreEqual(0, Volatile.Read(ref calls));
            reminder.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, reminder.Task, existing);
            foreach (var error in errors) Assert.IsNull(error);
            Assert.AreEqual(1, Volatile.Read(ref calls));
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CleanupPreservesFrontendContext(bool reminderFails)
    {
        var scope = new ReminderTaskScope();
        ManualContext? context = null;
        Task? core = null;
        Task? frontend = null;
        Task? dispatch = null;
        Task? completion = null;
        var previous = SynchronizationContext.Current;
        try
        {
            var reminder = scope.Gate();
            var editors = scope.Gate();
            var enteredEditors = scope.Gate();
            var reminderError = new InvalidOperationException("reminder");
            var stages = new ConcurrentQueue<(string Name, SynchronizationContext? Context)>();
            void Record(string name) => stages.Enqueue((name, SynchronizationContext.Current));
            context = new ManualContext(scope);
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                core = Route(scope,
                    () => { Record("reminder"); return new ValueTask(reminder.Task); },
                    () =>
                    {
                        Record("existing");
                        frontend = scope.Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
                            () => Record("projection"),
                            () => Record("reminder-ui"),
                            () => { Record("persistence"); return scope.Track(Task.CompletedTask); },
                            () =>
                            {
                                Record("editors");
                                return new ValueTask(editors.Task);
                            },
                            () => { Record("pump"); return ValueTask.CompletedTask; },
                            () => { Record("controller"); return ValueTask.CompletedTask; },
                            () => { Record("drafts"); return ValueTask.CompletedTask; }));
                        // Signal only after the actual frontend original is retained/assigned.
                        enteredEditors.TrySetResult(true);
                        return new ValueTask(frontend);
                    });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            // Retain arranged dispatch BEFORE any completion observer can dispatch frontend work.
            dispatch = scope.Track(context.DispatchAsync());
            completion = scope.Track(context.CompleteAfterAsync(core));
            context.StartDispatch();
            Assert.IsFalse(core.IsCompleted);
            Assert.IsNull(frontend);
            if (reminderFails) reminder.TrySetException(reminderError);
            else reminder.TrySetResult(true);
            var entered = await scope.ObserveManyAsync(enteredEditors.Task, reminder.Task);
            Assert.IsNull(entered[0]);
            Assert.AreSame(reminderFails ? reminderError : null, entered[1]);
            var retainedFrontend = frontend ?? throw new AssertFailedException("Frontend not entered.");
            Assert.IsFalse(retainedFrontend.IsCompleted);
            Assert.IsFalse(core.IsCompleted);
            editors.TrySetResult(true);
            // Every independent bound starts before this aggregate, never serial dispatch-first.
            var errors = await scope.ObserveManyAsync(dispatch, core, retainedFrontend, completion,
                reminder.Task, editors.Task, enteredEditors.Task);
            Assert.IsNull(errors[0]);
            Assert.AreSame(reminderFails ? reminderError : null, errors[1]);
            Assert.IsNull(errors[2]);
            Assert.IsNull(errors[3]);
            Assert.AreSame(reminderFails ? reminderError : null, errors[4]);
            Assert.IsNull(errors[5]);
            Assert.IsNull(errors[6]);
            CollectionAssert.AreEqual(new[] { "reminder", "existing", "projection", "reminder-ui",
                "persistence", "editors", "pump", "controller", "drafts" }, stages.Select(item => item.Name).ToArray());
            foreach (var stage in stages) Assert.AreSame(context, stage.Context, stage.Name);
            Assert.IsTrue(context.PostCount > 0, "An inert captured-context continuation was dispatched.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            try
            {
                if (context is not null)
                {
                    dispatch ??= scope.Track(context.DispatchAsync());
                    if (core is null) context.Complete();
                    else completion ??= scope.Track(context.CompleteAfterAsync(core));
                    context.StartDispatch();
                }
            }
            finally
            {
                try { await scope.FinishAsync(); }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }
        }
    }

    private static Task Route(ReminderTaskScope scope, Func<ValueTask> reminders, Func<ValueTask> existing)
        => scope.Track(ShellFrontendHost.DisposeRemindersThenFrontendAsync(reminders, existing));

    // Signaled, explicitly retained dispatch; no UI loop, polling, sleeps, Task.Run or dropped posts.
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly ReminderTaskScope _scope;
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();
        private readonly TaskCompletionSource<bool> _start;
        private TaskCompletionSource<bool> _signal;
        private bool _complete;
        private int _postCount;

        public ManualContext(ReminderTaskScope scope)
        {
            _scope = scope;
            _signal = scope.Gate();
            _start = scope.Gate();
        }

        public int PostCount => Volatile.Read(ref _postCount);
        public void StartDispatch() => _start.TrySetResult(true);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                _posts.Enqueue((callback, state));
                Interlocked.Increment(ref _postCount);
                if (_complete) _scope.RecordFailure(new AssertFailedException("Post after routing completion."));
                _signal.TrySetResult(true);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _complete = true;
                _signal.TrySetResult(true);
            }
        }

        public async Task CompleteAfterAsync(Task core)
        {
            try { await _scope.ObserveAsync(core).ConfigureAwait(false); }
            finally { Complete(); }
        }

        public async Task DispatchAsync()
        {
            var startError = await _scope.ObserveAsync(_start.Task).ConfigureAwait(false);
            if (startError is not null) ExceptionDispatchInfo.Throw(startError);
            while (true)
            {
                Task signal;
                lock (_gate) signal = _signal.Task;
                var error = await _scope.ObserveAsync(signal).ConfigureAwait(false);
                if (error is not null) ExceptionDispatchInfo.Throw(error);
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) posted;
                    lock (_gate)
                    {
                        if (!_posts.TryDequeue(out posted))
                        {
                            if (_complete) return;
                            _signal = _scope.Gate();
                            break;
                        }
                    }
                    var previous = Current;
                    try
                    {
                        SetSynchronizationContext(this);
                        posted.Callback(posted.State);
                    }
                    catch (Exception failure) { _scope.RecordFailure(failure); }
                    finally { SetSynchronizationContext(previous); }
                }
            }
        }
    }
}
