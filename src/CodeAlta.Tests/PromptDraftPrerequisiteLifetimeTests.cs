using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptDraftPrerequisiteLifetimeTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task Core_ValidatesMandatoryArgumentsBeforeCallbacks(int nullMask)
    {
        var recording = new Recording();
        try
        {
            var previous = recording.Track(Task.CompletedTask);
            var callbacks = 0;
            Func<Task> delay = () => { callbacks++; return previous; };
            Func<bool> predicate = () => { callbacks++; return true; };
            var failure = Assert.Throws<ArgumentNullException>(() =>
            {
                recording.Track(SessionPromptDraftPersistenceCoordinator.JoinDraftPrerequisitesAsync(
                    (nullMask & 1) != 0 ? null! : previous,
                    (nullMask & 2) != 0 ? null! : delay,
                    (nullMask & 4) != 0 ? null! : predicate));
            });
            Assert.AreEqual((nullMask & 1) != 0 ? "previous" : (nullMask & 2) != 0 ? "waitDelay" : "isDelayCancellationRequested", failure.ParamName);
            Assert.AreEqual(0, callbacks);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Core_StartsDelayInlineAndJoinsPreviousBeforeCompletion(bool pendingDelay)
    {
        var recording = new Recording();
        try
        {
            var previousGate = recording.Gate();
            var delayGate = recording.Gate();
            var previous = recording.Operation("success", "previous", previousGate.Task);
            var delay = recording.Operation("success", "delay", pendingDelay ? delayGate.Task : null);
            var calls = 0;
            var predicates = 0;
            var core = recording.Core(previous.Original,
                () => { calls++; return delay.Original; },
                () => { predicates++; return true; });
            Assert.AreEqual(1, calls, "Delay acquisition starts inline even while previous is pending.");
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse(previous.Original.IsCompleted);
            Assert.AreEqual(!pendingDelay, delay.Original.IsCompleted);
            delayGate.TrySetResult(true);
            var delayOutcome = await recording.ObserveManyAsync(delay.Original);
            delay.AssertOriginal(delayOutcome[0]);
            Assert.IsFalse(core.IsCompleted, "Delay completion does not terminate previous work.");
            previousGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, previous.Original, delay.Original);
            Assert.IsNull(outcomes[0]);
            previous.AssertOriginal(outcomes[1]);
            delay.AssertOriginal(outcomes[2]);
            Assert.IsTrue(core.IsCompletedSuccessfully);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, predicates);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    [DataRow("nested", false)]
    [DataRow("nested", true)]
    [DataRow("faulted-oce", false)]
    [DataRow("faulted-oce", true)]
    public async Task Core_DelayFailureStillJoinsPendingPrevious(string kind, bool pendingDelay)
    {
        var recording = new Recording();
        try
        {
            var previousGate = recording.Gate();
            var delayGate = recording.Gate();
            var previous = recording.Operation("success", "previous", previousGate.Task);
            var delay = recording.Operation(kind, "delay", pendingDelay ? delayGate.Task : null);
            var predicates = 0;
            var core = recording.Core(previous.Original, () => delay.Original,
                () => { predicates++; return false; });
            delayGate.TrySetResult(true);
            var delayOutcome = await recording.ObserveManyAsync(delay.Original);
            delay.AssertOriginal(delayOutcome[0]);
            Assert.IsFalse(core.IsCompleted, "A terminal delay failure must not sever the predecessor dependency.");
            Assert.IsFalse(previous.Original.IsCompleted);
            previousGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, delay.Original, previous.Original);
            Assert.AreSame(delay.Expected, outcomes[0]);
            delay.AssertOriginal(outcomes[1]);
            previous.AssertOriginal(outcomes[2]);
            Assert.AreEqual(kind == "faulted-oce", core.IsCanceled);
            Assert.AreEqual(kind != "faulted-oce", core.IsFaulted);
            Assert.AreEqual(kind == "faulted-oce" ? 1 : 0, predicates);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("fault")]
    [DataRow("nested")]
    [DataRow("faulted-oce")]
    [DataRow("null")]
    public async Task Core_DelayFactoryFailureStillJoinsPendingPrevious(string kind)
    {
        var recording = new Recording();
        try
        {
            var previousGate = recording.Gate();
            var previous = recording.Operation("success", "previous", previousGate.Task);
            var expected = kind == "null" ? null : CreateFailure(kind, "factory");
            var calls = 0;
            var predicates = 0;
            var core = recording.Core(previous.Original,
                () =>
                {
                    calls++;
                    if (kind == "null") return null!; // Explicit seam violation, not concrete Task.Delay behavior.
                    throw expected!;
                },
                () => { predicates++; return false; });
            Assert.AreEqual(1, calls);
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse(previous.Original.IsCompleted);
            previousGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, previous.Original);
            if (kind == "null")
                Assert.AreEqual("The delay operation returned a null task.", Assert.IsInstanceOfType<InvalidOperationException>(outcomes[0]).Message);
            else Assert.AreSame(expected, outcomes[0]);
            previous.AssertOriginal(outcomes[1]);
            Assert.AreEqual(kind == "faulted-oce", core.IsCanceled);
            Assert.AreEqual(kind != "faulted-oce", core.IsFaulted);
            Assert.AreEqual(kind == "faulted-oce" ? 1 : 0, predicates);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("factory", "true")]
    [DataRow("factory", "false")]
    [DataRow("factory", "throw")]
    [DataRow("faulted", "true")]
    [DataRow("faulted", "false")]
    [DataRow("faulted", "throw")]
    [DataRow("pending-faulted", "true")]
    [DataRow("pending-faulted", "false")]
    [DataRow("pending-faulted", "throw")]
    [DataRow("canceled", "true")]
    [DataRow("canceled", "false")]
    [DataRow("canceled", "throw")]
    [DataRow("pending-canceled", "true")]
    [DataRow("pending-canceled", "false")]
    [DataRow("pending-canceled", "throw")]
    public async Task Core_DelayCancellationUsesExistingFilterOnly(string delivery, string policy)
    {
        var recording = new Recording();
        try
        {
            var delayGate = recording.Gate();
            var previousGate = recording.Gate();
            var previous = recording.Operation("success", "previous", previousGate.Task);
            var genuinelyCanceled = delivery is "canceled" or "pending-canceled";
            var delay = delivery == "factory" ? null : recording.Operation(
                genuinelyCanceled ? "canceled" : "faulted-oce", "delay",
                delivery.StartsWith("pending-", StringComparison.Ordinal) ? delayGate.Task : null);
            var factoryFailure = CreateFailure("faulted-oce", "factory");
            var predicateFailure = new InvalidOperationException("Throwing filter must not replace original OCE.");
            var calls = 0;
            var predicates = 0;
            var core = recording.Core(previous.Original,
                () =>
                {
                    calls++;
                    if (delay is null) throw factoryFailure;
                    return delay.Original;
                },
                () =>
                {
                    predicates++;
                    if (policy == "throw") throw predicateFailure;
                    return policy == "true";
                });
            delayGate.TrySetResult(true);
            if (delay is not null)
            {
                var delayObservation = await recording.ObserveManyAsync(delay.Original);
                delay.AssertOriginal(delayObservation[0]);
            }
            Assert.IsFalse(core.IsCompleted, "Every filter outcome still joins the pending predecessor.");
            Assert.IsFalse(previous.Original.IsCompleted);
            previousGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, previous.Original, delay?.Original ?? previous.Original);
            previous.AssertOriginal(outcomes[1]);
            delay?.AssertOriginal(outcomes[2]);
            if (policy == "true") Assert.IsNull(outcomes[0]);
            else if (genuinelyCanceled)
                Assert.AreEqual(new CancellationToken(canceled: true), Assert.IsInstanceOfType<OperationCanceledException>(outcomes[0]).CancellationToken);
            else Assert.AreSame(delay?.Expected ?? factoryFailure, outcomes[0]);
            Assert.AreEqual(policy != "true", core.IsCanceled);
            Assert.IsFalse(core.IsFaulted);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, predicates);
            // A throwing filter is false under C# semantics. It contributes no new aggregate entry.
            Assert.AreNotSame(predicateFailure, outcomes[0]);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    [DataRow("nested", false)]
    [DataRow("nested", true)]
    [DataRow("faulted-oce", false)]
    [DataRow("faulted-oce", true)]
    [DataRow("canceled", false)]
    [DataRow("canceled", true)]
    public async Task Core_PriorFailurePreservesOriginalIdentity(string kind, bool pendingPrevious)
    {
        var recording = new Recording();
        try
        {
            var previousGate = recording.Gate();
            var previous = recording.Operation(kind, "previous", pendingPrevious ? previousGate.Task : null);
            var delay = recording.Operation("success", "delay");
            var predicates = 0;
            var core = recording.Core(previous.Original, () => delay.Original,
                () => { predicates++; throw new AssertFailedException("A predecessor OCE must not evaluate the delay filter."); });
            if (pendingPrevious) Assert.IsFalse(core.IsCompleted);
            previousGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, previous.Original, delay.Original);
            if (kind == "canceled")
                Assert.AreEqual(new CancellationToken(canceled: true), Assert.IsInstanceOfType<OperationCanceledException>(outcomes[0]).CancellationToken);
            else Assert.AreSame(previous.Expected, outcomes[0]);
            previous.AssertOriginal(outcomes[1]);
            delay.AssertOriginal(outcomes[2]);
            Assert.AreEqual(kind is "faulted-oce" or "canceled", core.IsCanceled);
            Assert.AreEqual(kind is "fault" or "nested", core.IsFaulted);
            Assert.AreEqual(0, predicates);
            // Genuine canceled tasks need not yield the same newly materialized TaskCanceledException
            // on independent awaits; supplied faulted-OCE exception identity is checked above.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    [DataRow("nested", false)]
    [DataRow("nested", true)]
    [DataRow("repeated", false)]
    [DataRow("repeated", true)]
    [DataRow("faulted-oce", false)]
    [DataRow("faulted-oce", true)]
    public async Task Core_MultipleFailuresRemainDirectAndOrdered(string kind, bool previousFirst)
    {
        var recording = new Recording();
        try
        {
            var previousGate = recording.Gate();
            var delayGate = recording.Gate();
            var outcome = kind == "repeated" ? "fault" : kind;
            var delayFailure = CreateFailure(outcome, "delay");
            var previousFailure = kind == "repeated" ? delayFailure : CreateFailure(outcome, "previous");
            var delay = recording.Operation(outcome, "delay", delayGate.Task, delayFailure);
            var previous = recording.Operation(outcome, "previous", previousGate.Task, previousFailure);
            var core = recording.Core(previous.Original, () => delay.Original, static () => false);
            if (previousFirst)
            {
                previousGate.TrySetResult(true);
                var first = await recording.ObserveManyAsync(previous.Original);
                previous.AssertOriginal(first[0]);
                Assert.IsFalse(core.IsCompleted, "Previous termination cannot bypass a pending delay.");
                delayGate.TrySetResult(true);
            }
            else
            {
                delayGate.TrySetResult(true);
                var first = await recording.ObserveManyAsync(delay.Original);
                delay.AssertOriginal(first[0]);
                Assert.IsFalse(core.IsCompleted, "Delay failure cannot bypass a pending predecessor.");
                previousGate.TrySetResult(true);
            }
            var outcomes = await recording.ObserveManyAsync(core, delay.Original, previous.Original);
            var aggregate = Assert.IsInstanceOfType<AggregateException>(outcomes[0]);
            Assert.AreEqual(2, aggregate.InnerExceptions.Count);
            Assert.AreSame(delayFailure, aggregate.InnerExceptions[0]);
            Assert.AreSame(previousFailure, aggregate.InnerExceptions[1]);
            delay.AssertOriginal(outcomes[1]);
            previous.AssertOriginal(outcomes[2]);
            Assert.IsTrue(core.IsFaulted);
            Assert.IsFalse(core.IsCanceled);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("delay")]
    [DataRow("previous")]
    public async Task Core_PendingPrerequisitePreventsCompletion(string pending)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var delay = recording.Operation("success", "delay", pending == "delay" ? gate.Task : null);
            var previous = recording.Operation("fault", "previous", pending == "previous" ? gate.Task : null);
            var core = recording.Core(previous.Original, () => delay.Original, static () => false);
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse((pending == "delay" ? delay.Original : previous.Original).IsCompleted);
            // No elapsed-time probe. Finally releases the manual gate and independently observes
            // the original fault and eventual core failure; it does not cancel or time out production.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("nested")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    public async Task Core_OnlyJoinsSuppliedOriginalTasks(string hiddenOutcome)
    {
        var recording = new Recording();
        try
        {
            var hiddenGate = recording.Gate();
            var unselectedGate = recording.Gate();
            var unselected = recording.Operation("fault", "unselected", unselectedGate.Task);
            var previous = recording.Operation("success", "previous");
            var delay = recording.Operation("success", "selected delay");
            Operation? hidden = null;
            var core = recording.Core(previous.Original,
                () =>
                {
                    // Operation retains both original and producer before any later callback/assertion.
                    hidden = recording.Operation(hiddenOutcome, "withheld callback work", hiddenGate.Task);
                    return delay.Original;
                }, static () => false);
            Assert.IsNotNull(hidden);
            Assert.IsTrue(core.IsCompletedSuccessfully);
            Assert.IsFalse(hidden.Original.IsCompleted);
            Assert.IsFalse(unselected.Original.IsCompleted);
            hiddenGate.TrySetResult(true);
            unselectedGate.TrySetResult(true);
            var outcomes = await recording.ObserveManyAsync(core, previous.Original, delay.Original, hidden.Original, unselected.Original);
            Assert.IsNull(outcomes[0]);
            previous.AssertOriginal(outcomes[1]);
            delay.AssertOriginal(outcomes[2]);
            hidden.AssertOriginal(outcomes[3]);
            unselected.AssertOriginal(outcomes[4]);
            // This is an inert supplied-task contract limit, not real application descendant ownership.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("nested")]
    [DataRow("faulted-oce")]
    public async Task Core_BackgroundJoinPreservesFrontendStageSevenContext(string delayOutcome)
    {
        var recording = new Recording();
        ManualContext? context = null;
        Task? caller = null;
        Task? dispatch = null;
        Task? completion = null;
        var previousContext = SynchronizationContext.Current;
        try
        {
            var previousGate = recording.Gate();
            var delayGate = recording.Gate();
            var previous = recording.Operation("success", "previous", previousGate.Task);
            var delay = recording.Operation(delayOutcome, "delay", delayGate.Task);
            var contexts = new ConcurrentQueue<(string Stage, SynchronizationContext? Context)>();
            Exception? callerFailure = null;
            context = new ManualContext(recording);
            // Retain the dispatch producer and its start gate before any frontend callback runs.
            dispatch = recording.Track(context.DispatchAsync());
            async Task CallFrontendAsync()
            {
                var frontend = recording.Frontend(previous.Original,
                    () => { contexts.Enqueue(("delay", SynchronizationContext.Current)); return delay.Original; });
                try { await frontend; }
                catch (Exception ex) { callerFailure = ex; }
                finally { contexts.Enqueue(("caller-after-frontend", SynchronizationContext.Current)); }
            }
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                caller = recording.Track(CallFrontendAsync());
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            completion = recording.Track(context.CompleteAfterAsync(caller));
            context.StartDispatch();
            var frontendTask = recording.FrontendTask ?? throw new AssertFailedException("Frontend not retained.");
            var core = recording.CoreTask ?? throw new AssertFailedException("Stage-seven core not retained.");
            Assert.IsFalse(caller.IsCompleted);
            Assert.IsFalse(frontendTask.IsCompleted);
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse(previous.Original.IsCompleted);
            Assert.IsFalse(delay.Original.IsCompleted);
            Assert.AreEqual(0, context.PostCount);
            recording.AssertFrontendStages();
            delayGate.TrySetResult(true);
            previousGate.TrySetResult(true);
            // All independent five-second observers start before this aggregate. Never dispatch-first.
            var outcomes = await recording.ObserveManyAsync(dispatch, caller, frontendTask, core, completion, delay.Original, previous.Original);
            Assert.IsNull(outcomes[0]);
            Assert.IsNull(outcomes[1]);
            Assert.AreSame(delay.Expected, outcomes[2]);
            Assert.AreSame(delay.Expected, outcomes[3]);
            Assert.IsNull(outcomes[4]);
            delay.AssertOriginal(outcomes[5]);
            previous.AssertOriginal(outcomes[6]);
            Assert.AreSame(delay.Expected, callerFailure);
            Assert.IsTrue(context.PostCount >= 1, "Pending infrastructure work must resume the frontend through its captured context.");
            var observed = contexts.ToArray();
            CollectionAssert.AreEqual(new[] { "delay", "caller-after-frontend" }, observed.Select(item => item.Stage).ToArray());
            foreach (var item in observed) Assert.AreSame(context, item.Context, item.Stage);
            recording.AssertFrontendStages();
            // Stage seven is last. The context assertion is the caller after plain-await frontend
            // completion (also after failure), not an invented later stage or a forced thread switch.
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            recording.ReleaseGates();
            try
            {
                if (context is not null)
                {
                    dispatch ??= recording.Track(context.DispatchAsync());
                    if (caller is null) context.Complete();
                    else completion ??= recording.Track(context.CompleteAfterAsync(caller));
                    context.StartDispatch();
                }
            }
            finally
            {
                try { await recording.FinishAsync(); }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            }
        }
    }

    private static Exception CreateFailure(string kind, string name) => kind switch
    {
        "fault" => new InvalidOperationException(name),
        "faulted-oce" => new OperationCanceledException(name, new CancellationToken(canceled: true)),
        "nested" => new AggregateException(new InvalidOperationException(name), new AggregateException(new OperationCanceledException("nested"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task CompleteOriginalAsync(Task gate, TaskCompletionSource<bool> completion, string outcome, Exception? expected)
    {
        await gate.ConfigureAwait(false);
        switch (outcome)
        {
            case "success": completion.TrySetResult(true); break;
            case "canceled": completion.TrySetCanceled(new CancellationToken(canceled: true)); break;
            case "fault" or "faulted-oce" or "nested":
                completion.TrySetException(expected ?? new AssertFailedException("Missing synthetic failure."));
                break;
            default: completion.TrySetException(new AssertFailedException("Unknown synthetic outcome.")); break;
        }
    }

    private sealed class Operation(Task original, Exception? expected, string outcome)
    {
        public Task Original { get; } = original;
        public Exception? Expected { get; } = expected;

        public void AssertOriginal(Exception? actual)
        {
            if (outcome == "canceled")
                Assert.AreEqual(new CancellationToken(canceled: true), Assert.IsInstanceOfType<OperationCanceledException>(actual).CancellationToken);
            else Assert.AreSame(Expected, actual);
            Assert.AreEqual(outcome == "canceled", Original.IsCanceled);
            Assert.AreEqual(outcome is "fault" or "faulted-oce" or "nested", Original.IsFaulted);
        }
    }

    // Instance-owned inert state only. No actual CTS/registration, source/file reads, concrete
    // persistence/logger/store/UI/version/monitor initialization, auth records or HOME shortcuts.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();
        private readonly ConcurrentQueue<Exception> _permanentFailures = new();
        private readonly ConcurrentQueue<string> _stages = new();

        public Task? CoreTask { get; private set; }
        public Task? FrontendTask { get; private set; }

        public T Track<T>(T task) where T : Task { _tasks.Enqueue(task); return task; }
        public void RecordFailure(Exception failure) => _permanentFailures.Enqueue(failure);
        public void ReleaseGates() { foreach (var gate in _gates) gate.TrySetResult(true); }
        public void AssertFrontendStages()
            => CollectionAssert.AreEqual(new[] { "projection", "reminder-ui", "persistence", "editors", "pump", "controller", "drafts" }, _stages.ToArray());

        public TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Track(signal.Task);
            return signal;
        }

        public TaskCompletionSource<bool> Gate()
        {
            var gate = Signal();
            _gates.Enqueue(gate);
            return gate;
        }

        public Operation Operation(string outcome, string name, Task? gate = null, Exception? failure = null)
        {
            var expected = outcome is "success" or "canceled" ? null : failure ?? CreateFailure(outcome, name);
            Task original;
            if (gate is not null)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                original = Track(completion.Task);
                Track(CompleteOriginalAsync(gate, completion, outcome, expected));
            }
            else
            {
                original = Track(outcome switch
                {
                    "success" => Task.CompletedTask,
                    "canceled" => Task.FromCanceled(new CancellationToken(canceled: true)),
                    _ => Task.FromException(expected ?? new AssertFailedException("Missing synthetic failure.")),
                });
            }
            // Retain original and producer before exposing either to fallible callback code.
            return new Operation(original, expected, outcome);
        }

        public Task Core(Task previous, Func<Task> waitDelay, Func<bool> predicate)
        {
            var task = Track(SessionPromptDraftPersistenceCoordinator.JoinDraftPrerequisitesAsync(previous, waitDelay, predicate));
            CoreTask = task;
            return task;
        }

        public Task Frontend(Task previous, Func<Task> waitDelay)
        {
            var task = Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
                () => _stages.Enqueue("projection"),
                () => _stages.Enqueue("reminder-ui"),
                () => { _stages.Enqueue("persistence"); return Track(Task.CompletedTask); },
                () => { _stages.Enqueue("editors"); return ValueTask.CompletedTask; },
                () => { _stages.Enqueue("pump"); return ValueTask.CompletedTask; },
                () => { _stages.Enqueue("controller"); return ValueTask.CompletedTask; },
                () => { _stages.Enqueue("drafts"); return new ValueTask(Core(previous, waitDelay, static () => false)); }));
            FrontendTask = task;
            return task;
        }

        public Task<Exception?> ObserveAsync(Task task) => Track(ObserveCoreAsync(task));

        private async Task<Exception?> ObserveCoreAsync(Task task)
        {
            try
            {
                var bounded = Track(task.WaitAsync(TimeSpan.FromSeconds(5)));
                await bounded.ConfigureAwait(false);
                return null;
            }
            catch (TimeoutException ex)
            {
                // All body/nested/dispatch/teardown timeout observations remain permanent failures,
                // even when another observation later sees the original task complete successfully.
                RecordFailure(ex);
                if (task.IsCompleted)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception) { /* Observe original without rehabilitating timeout. */ }
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

        public Task<Exception?[]> ObserveManyAsync(params Task[] tasks) => Track(ObserveManyCoreAsync(tasks));

        private async Task<Exception?[]> ObserveManyCoreAsync(Task[] tasks)
        {
            List<Task<Exception?>> observations = [];
            foreach (var task in tasks) observations.Add(ObserveAsync(task));
            var aggregate = Track(Task.WhenAll(observations));
            return await aggregate.ConfigureAwait(false);
        }

        public async Task FinishAsync()
        {
            ReleaseGates();
            var firstTasks = _tasks.ToArray();
            Exception? firstFailure = null;
            try { await ObserveManyAsync(firstTasks).ConfigureAwait(false); }
            catch (Exception ex) { firstFailure = ex; }
            // Finite second snapshot includes late callback work and retained observers/bounds/
            // aggregates. The teardown task is awaited directly, never queued to join itself.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try { await ObserveManyAsync(finalTasks).ConfigureAwait(false); }
            catch (Exception ex) { finalFailure = ex; }
            List<Exception> failures = [.. _permanentFailures];
            if (firstFailure is not null and not TimeoutException) failures.Add(firstFailure);
            if (finalFailure is not null and not TimeoutException) failures.Add(finalFailure);
            if (!firstTasks.All(static task => task.IsCompleted) || !finalTasks.All(static task => task.IsCompleted))
                failures.Add(new AssertFailedException("Retained work did not complete; this is not termination evidence."));
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    // Manually dispatched inert continuations, not a UI dispatcher. Queue every post, including
    // unexpected additional work; permanent failures report late posts rather than silently dropping them.
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Recording _recording;
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();
        private readonly TaskCompletionSource<bool> _dispatchStart;
        private TaskCompletionSource<bool> _signal;
        private bool _complete;
        private int _postCount;

        public ManualContext(Recording recording)
        {
            _recording = recording;
            _signal = recording.Signal();
            _dispatchStart = recording.Gate();
        }

        public int PostCount => Volatile.Read(ref _postCount);
        public void StartDispatch() => _dispatchStart.TrySetResult(true);

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                _posts.Enqueue((d, state));
                Interlocked.Increment(ref _postCount);
                if (_complete) _recording.RecordFailure(new AssertFailedException("Post after frontend caller completion."));
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

        public async Task CompleteAfterAsync(Task caller)
        {
            try { await _recording.ObserveAsync(caller).ConfigureAwait(false); }
            finally { Complete(); }
        }

        public async Task DispatchAsync()
        {
            var startFailure = await _recording.ObserveAsync(_dispatchStart.Task).ConfigureAwait(false);
            if (startFailure is not null) ExceptionDispatchInfo.Throw(startFailure);
            while (true)
            {
                Task signal;
                lock (_gate) signal = _signal.Task;
                var failure = await _recording.ObserveAsync(signal).ConfigureAwait(false);
                if (failure is not null) ExceptionDispatchInfo.Throw(failure);
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) posted;
                    lock (_gate)
                    {
                        if (!_posts.TryDequeue(out posted))
                        {
                            if (_complete) return;
                            // Retain the next signal before another callback can publish. Each
                            // outer iteration awaits that signal; this is not sleeping or polling.
                            _signal = _recording.Signal();
                            break;
                        }
                    }
                    var previous = Current;
                    try
                    {
                        SetSynchronizationContext(this);
                        posted.Callback(posted.State);
                    }
                    catch (Exception ex) { _recording.RecordFailure(ex); }
                    finally { SetSynchronizationContext(previous); }
                }
            }
        }
    }
}
