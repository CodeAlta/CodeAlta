using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.LiveTool;
using WorkerJoin = CodeAlta.LiveTool.AltaReminderService.ReminderWorkerJoin;

namespace CodeAlta.Tests;

/// <summary>Inert supplied-task tests of the mandatory reminder static seams only.</summary>
/// <remarks>
/// No service, entry, CTS, registration, owner, provider, registry, UI or host is constructed.
/// Actual admission, pruning/history, instance Lazy sharing and cancellation races remain
/// source-only obligations. These cases do not establish fallback/failed-acquisition ownership,
/// CRUD notification draining, UI/dialog/output or detached-send/provider descendant completion,
/// full service quiescence, termination guarantees or BCL behavior. Production ownership is
/// limited to the frontend-created service of a successfully returned App; M2-M7 remain open.
/// Missing production members are intentional until separately authorized implementation.
/// </remarks>
[TestClass]
public sealed class AltaReminderLifetimeTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Start_ValidatesBeforeCallbacks(int nullMask)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var calls = 0;
            Action start = () => calls++;
            Action<Exception?> complete = _ => calls++;
            var error = Assert.Throws<ArgumentNullException>(() =>
                AltaReminderService.StartAndPublishReminderWorker(
                    (nullMask & 1) != 0 ? null! : start,
                    (nullMask & 2) != 0 ? null! : complete));
            Assert.AreEqual((nullMask & 1) != 0 ? "startAndRetainOriginal" : "completePublication", error.ParamName);
            Assert.AreEqual(0, calls);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    public async Task Start_RetainsOriginalBeforeCompletion(string outcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var gate = scope.Gate();
            var expected = ReminderTaskScope.Failure(outcome, "original");
            var original = outcome == "pending" ? gate.Task : scope.Original(outcome, expected);
            var publication = scope.Publication(original, pending: true);
            Task? assigned = null;
            Task? seen = null;
            Exception? reported = null;
            var events = new List<string>();
            AltaReminderService.StartAndPublishReminderWorker(
                () => { assigned = original; events.Add("assigned"); },
                error =>
                {
                    seen = assigned;
                    reported = error;
                    events.Add("completion");
                    publication.TrySetResult(assigned!);
                });
            Assert.AreSame(original, assigned);
            Assert.AreSame(original, seen);
            Assert.IsNull(reported);
            CollectionAssert.AreEqual(new[] { "assigned", "completion" }, events);
            Assert.IsTrue(publication.Task.IsCompletedSuccessfully);
            Assert.AreSame(original, publication.Task.GetAwaiter().GetResult());
            Assert.AreEqual(outcome != "pending", original.IsCompleted);
            gate.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(original, publication.Task);
            ReminderTaskScope.AssertOriginal(original, errors[0], outcome == "pending" ? "success" : outcome, expected);
            Assert.IsNull(errors[1]);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("completion")]
    [DataRow("both")]
    public async Task Start_CompletionFailureHasExplicitPrecedence(string failing)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var original = scope.Gate();
            var startError = new InvalidOperationException("start");
            var completionError = new AggregateException(new InvalidOperationException("completion"));
            Task? assigned = null;
            Exception? reported = null;
            var completionCalls = 0;
            Action invoke = () => AltaReminderService.StartAndPublishReminderWorker(
                () =>
                {
                    if (failing != "completion") throw startError;
                    assigned = original.Task;
                },
                failure =>
                {
                    completionCalls++;
                    reported = failure;
                    if (failing != "start") throw completionError;
                });
            Exception error = failing == "start"
                ? Assert.Throws<InvalidOperationException>(invoke)
                : Assert.Throws<AggregateException>(invoke);
            Assert.AreSame(failing == "start" ? startError : completionError, error);
            Assert.AreSame(failing == "completion" ? null : startError, reported);
            Assert.AreSame(failing == "completion" ? original.Task : null, assigned);
            Assert.AreEqual(1, completionCalls);
            Assert.IsFalse(original.Task.IsCompleted, "Completion failure is not original termination.");
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("pending")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    public async Task Observe_RequiresActualCompletion(string outcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            if (outcome == "null")
            {
                var error = Assert.Throws<ArgumentNullException>(() =>
                    AltaReminderService.TryObserveCompletedReminder(null!, out _));
                Assert.AreEqual("original", error.ParamName);
                return;
            }

            var gate = scope.Gate();
            var expected = ReminderTaskScope.Failure(outcome, "observed");
            var original = outcome == "pending" ? gate.Task : scope.Original(outcome, expected);
            var completed = AltaReminderService.TryObserveCompletedReminder(original, out var failure);
            Assert.AreEqual(outcome != "pending", completed);
            if (outcome == "pending")
            {
                Assert.IsNull(failure);
                Assert.IsFalse(original.IsCompleted);
            }
            else ReminderTaskScope.AssertOriginal(original, failure, outcome, expected);
            gate.TrySetResult(true);
            var observed = await scope.ObserveManyAsync(original);
            ReminderTaskScope.AssertOriginal(original, observed[0], outcome == "pending" ? "success" : outcome, expected);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("null-both")]
    [DataRow("null-cancel")]
    [DataRow("null-complete")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("oce")]
    public async Task Cancel_ValidatesAndCompletesAfterTraversal(string outcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var events = new List<string>();
            var expected = ReminderTaskScope.Failure(outcome, "cancel");
            Exception? reported = null;
            Action cancel = () =>
            {
                events.Add("cancel");
                if (expected is not null) throw expected;
            };
            Action<Exception?> complete = failure => { events.Add("complete"); reported = failure; };
            if (outcome.StartsWith("null-", StringComparison.Ordinal))
            {
                var error = Assert.Throws<ArgumentNullException>(() => AltaReminderService.CancelAndComplete(
                    outcome is "null-both" or "null-cancel" ? null! : cancel,
                    outcome is "null-both" or "null-complete" ? null! : complete));
                Assert.AreEqual(outcome == "null-complete" ? "complete" : "cancel", error.ParamName);
                Assert.AreEqual(0, events.Count);
                return;
            }

            var actual = AltaReminderService.CancelAndComplete(cancel, complete);
            CollectionAssert.AreEqual(new[] { "cancel", "complete" }, events);
            Assert.AreSame(expected, actual);
            Assert.AreSame(expected, reported);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_CompletionFailureHasExplicitPrecedence(bool cancelFails)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var cancelError = new OperationCanceledException("private synthetic action");
            var completionError = new InvalidOperationException("completion");
            Exception? reported = null;
            var error = Assert.Throws<InvalidOperationException>(() => AltaReminderService.CancelAndComplete(
                () => { if (cancelFails) throw cancelError; },
                failure => { reported = failure; throw completionError; }));
            Assert.AreSame(completionError, error);
            Assert.AreSame(cancelFails ? cancelError : null, reported);
            // This is callback-error precedence only, not a private return-fence state test.
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("null-both")]
    [DataRow("null-fence")]
    [DataRow("null-release")]
    [DataRow("pending")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    [DataRow("release-fault")]
    public async Task SourceRelease_RequiresSuccessfulReturnFence(string outcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var gate = scope.Gate();
            var expected = ReminderTaskScope.Failure(outcome, "fence/release");
            var releases = 0;
            Action release = () => { releases++; if (outcome == "release-fault") throw expected!; };
            if (outcome.StartsWith("null-", StringComparison.Ordinal))
            {
                var error = Assert.Throws<ArgumentNullException>(() => scope.Track(
                    AltaReminderService.FinishReminderSourceAsync(
                        outcome is "null-both" or "null-fence" ? null! : gate.Task,
                        outcome is "null-both" or "null-release" ? null! : release)));
                Assert.AreEqual(outcome == "null-release" ? "releaseSource" : "cancellationReturned", error.ParamName);
                Assert.AreEqual(0, releases);
                return;
            }

            var fenceKind = outcome == "release-fault" ? "success" : outcome;
            var fence = fenceKind == "pending" ? gate.Task : scope.Original(fenceKind, expected);
            var core = scope.Track(AltaReminderService.FinishReminderSourceAsync(fence, release));
            if (outcome == "pending")
            {
                Assert.IsFalse(core.IsCompleted);
                Assert.AreEqual(0, releases);
            }
            gate.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, fence);
            ReminderTaskScope.AssertOriginal(fence, errors[1], fenceKind == "pending" ? "success" : fenceKind, expected);
            if (outcome == "canceled") ReminderTaskScope.AssertCancellation(errors[0]);
            else Assert.AreSame(outcome is "success" or "pending" ? null : expected, errors[0]);
            Assert.AreEqual(outcome is "pending" or "success" or "release-fault" ? 1 : 0, releases);
            Assert.AreEqual(outcome is "canceled" or "faulted-oce", core.IsCanceled);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, 1)]
    [DataRow(0, 2)]
    [DataRow(0, 4)]
    [DataRow(0, 6)]
    [DataRow(0, 7)]
    [DataRow(1, 1)]
    [DataRow(1, 2)]
    [DataRow(1, 4)]
    public async Task Dispose_ValidatesSnapshotBeforeCallbacks(int badIndex, int nullMask)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var original = scope.Track(Task.CompletedTask);
            var publication = scope.Publication(original);
            var calls = 0;
            Func<Exception?> callback = () => { calls++; return null; };
            WorkerJoin[] workers = [new(callback, publication.Task, callback), new(callback, publication.Task, callback)];
            if (badIndex >= 0)
            {
                workers[badIndex] = new(
                    (nullMask & 1) != 0 ? null! : callback,
                    (nullMask & 2) != 0 ? null! : publication.Task,
                    (nullMask & 4) != 0 ? null! : callback);
                // Even index-zero reader validation must precede index-one request validation.
                if (badIndex == 0) workers[1] = default;
            }
            var error = badIndex < 0
                ? Assert.Throws<ArgumentNullException>(() => Join(scope, null!))
                : Assert.Throws<ArgumentException>(() => Join(scope, workers));
            Assert.AreEqual("workers", error.ParamName);
            if (badIndex < 0) Assert.IsInstanceOfType<ArgumentNullException>(error);
            else
            {
                var member = (nullMask & 1) != 0 ? "cancellation operation" :
                    (nullMask & 2) != 0 ? "publication task" : "cancellation-failure reader";
                Assert.IsTrue(error.Message.StartsWith($"Worker {badIndex} has no {member}.", StringComparison.Ordinal));
            }
            Assert.AreEqual(0, calls, "Validation is synchronous and complete before any callback.");
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    public async Task Dispose_PreservesInputsAgainstCallerArrayMutation()
    {
        var scope = new ReminderTaskScope();
        try
        {
            var first = scope.Track(Task.CompletedTask);
            var selected = scope.Gate();
            var unselected = scope.Gate();
            var firstPublication = scope.Publication(first);
            var selectedPublication = scope.Publication(selected.Task);
            var replacementPublication = scope.Publication(unselected.Task);
            var events = new ConcurrentQueue<string>();
            WorkerJoin[] workers = new WorkerJoin[2];
            workers[0] = new(() =>
            {
                events.Enqueue("cancel-0");
                workers[1] = new(() => { events.Enqueue("replacement-cancel"); return null; },
                    replacementPublication.Task, () => { events.Enqueue("replacement-read"); return null; });
                return null;
            }, firstPublication.Task, () => { events.Enqueue("read-0"); return null; });
            workers[1] = new(() => { events.Enqueue("cancel-1"); return null; },
                selectedPublication.Task, () => { events.Enqueue("read-1"); return null; });
            var core = Join(scope, workers);
            CollectionAssert.AreEqual(new[] { "cancel-0", "cancel-1", "read-0" }, events.ToArray());
            Assert.IsFalse(core.IsCompleted);
            selected.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, first, selected.Task,
                firstPublication.Task, selectedPublication.Task, replacementPublication.Task);
            foreach (var error in errors) Assert.IsNull(error);
            Assert.IsFalse(unselected.Task.IsCompleted, "An unselected replacement is not joined by the supplied snapshot.");
            CollectionAssert.AreEqual(new[] { "cancel-0", "cancel-1", "read-0", "read-1" }, events.ToArray());
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("return")]
    [DataRow("throw")]
    public async Task Dispose_AttemptsAllCancellationCallbacksBeforeJoining(string firstFailure)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var first = scope.Gate();
            var later = scope.Track(Task.CompletedTask);
            var firstPublication = scope.Publication(first.Task);
            var laterPublication = scope.Publication(later);
            var expected = new InvalidOperationException("first cancellation");
            var events = new ConcurrentQueue<string>();
            WorkerJoin[] workers =
            [
                new(() =>
                {
                    events.Enqueue("cancel-0");
                    if (firstFailure == "throw") throw expected;
                    return firstFailure == "return" ? expected : null;
                }, firstPublication.Task, () => { events.Enqueue("read-0"); return null; }),
                new(() => { events.Enqueue("cancel-1"); return null; }, laterPublication.Task,
                    () => { events.Enqueue("read-1"); return null; }),
                new(() => { events.Enqueue("cancel-2"); return null; }, laterPublication.Task,
                    () => { events.Enqueue("read-2"); return null; }),
            ];
            var core = Join(scope, workers);
            CollectionAssert.AreEqual(new[] { "cancel-0", "cancel-1", "cancel-2" }, events.ToArray());
            Assert.IsFalse(core.IsCompleted);
            first.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, first.Task, later, firstPublication.Task, laterPublication.Task);
            Assert.AreSame(firstFailure == "none" ? null : expected, errors[0]);
            foreach (var error in errors.Skip(1)) Assert.IsNull(error);
            CollectionAssert.AreEqual(firstFailure == "none"
                ? new[] { "cancel-0", "cancel-1", "cancel-2", "read-0", "read-1", "read-2" }
                : new[] { "cancel-0", "cancel-1", "cancel-2", "read-1", "read-2" }, events.ToArray());
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("pending-publication")]
    [DataRow("pending-original")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    [DataRow("publication-fault")]
    [DataRow("publication-canceled")]
    [DataRow("null-original")]
    public async Task Dispose_JoinsPublicationThenOriginal(string outcome)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var gate = scope.Gate();
            var unselected = scope.Gate();
            var expected = ReminderTaskScope.Failure(outcome, "join");
            var originalKind = outcome is "fault" or "faulted-oce" or "canceled" ? outcome : "success";
            var original = outcome.StartsWith("pending-", StringComparison.Ordinal)
                ? gate.Task : scope.Original(originalKind, expected);
            var publication = scope.Publication(original, pending: true);
            if (outcome != "pending-publication")
            {
                if (outcome == "publication-fault") publication.TrySetException(expected!);
                else if (outcome == "publication-canceled") publication.TrySetCanceled(new CancellationToken(true));
                else publication.TrySetResult(outcome == "null-original" ? null! : original);
            }
            var later = scope.Track(Task.CompletedTask);
            var laterPublication = scope.Publication(later);
            var reads = new ConcurrentQueue<string>();
            var core = Join(scope,
            [
                new(static () => null, publication.Task, () => { reads.Enqueue("first"); return null; }),
                new(static () => null, laterPublication.Task, () => { reads.Enqueue("later"); return null; }),
            ]);
            if (outcome.StartsWith("pending-", StringComparison.Ordinal))
            {
                Assert.IsFalse(core.IsCompleted);
                Assert.AreEqual(0, reads.Count);
                publication.TrySetResult(original);
                var published = await scope.ObserveManyAsync(publication.Task);
                Assert.IsNull(published[0]);
                Assert.IsFalse(core.IsCompleted, "Successful publication alone is not an original join.");
            }
            gate.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, original, publication.Task, later, laterPublication.Task);
            if (outcome == "null-original")
                Assert.AreEqual("Worker 0 published a null original task.", Assert.IsInstanceOfType<InvalidOperationException>(errors[0]).Message);
            else if (outcome is "canceled" or "publication-canceled") ReminderTaskScope.AssertCancellation(errors[0]);
            else Assert.AreSame(outcome is "fault" or "faulted-oce" or "publication-fault" ? expected : null, errors[0]);
            ReminderTaskScope.AssertOriginal(original, errors[1], originalKind, expected);
            if (outcome == "publication-canceled") ReminderTaskScope.AssertCancellation(errors[2]);
            else Assert.AreSame(outcome == "publication-fault" ? expected : null, errors[2]);
            Assert.IsNull(errors[3]);
            Assert.IsNull(errors[4]);
            Assert.AreEqual(outcome is "faulted-oce" or "canceled" or "publication-canceled", core.IsCanceled);
            CollectionAssert.AreEqual(new[] { "first", "later" }, reads.ToArray());
            Assert.IsFalse(unselected.Task.IsCompleted);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("return", "worker")]
    [DataRow("throw", "worker")]
    [DataRow("return", "publication")]
    [DataRow("throw", "publication")]
    [DataRow("throw", "null")]
    [DataRow("null", "worker")]
    [DataRow("skip-return", "worker")]
    [DataRow("skip-throw", "worker")]
    [DataRow("return", "pending")]
    public async Task Dispose_PreservesReaderAndWorkerFailures(string readerPolicy, string workerPolicy)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var gate = scope.Gate();
            var workerError = new InvalidOperationException("worker/publication");
            var readerError = new AggregateException(new OperationCanceledException("reader"));
            var callerError = new InvalidOperationException("request caller");
            var original = workerPolicy == "pending" ? gate.Task :
                scope.Original(workerPolicy == "worker" ? "fault" : "success", workerError);
            var publication = scope.Publication(original, pending: true);
            if (workerPolicy == "publication") publication.TrySetException(workerError);
            else publication.TrySetResult(workerPolicy == "null" ? null! : original);
            var reads = 0;
            var skip = readerPolicy.StartsWith("skip-", StringComparison.Ordinal);
            var core = Join(scope,
            [
                new(() =>
                {
                    if (readerPolicy == "skip-throw") throw callerError;
                    return readerPolicy == "skip-return" ? callerError : null;
                }, publication.Task, () =>
                {
                    Interlocked.Increment(ref reads);
                    if (readerPolicy == "throw") throw readerError;
                    return readerPolicy == "null" ? null : readerError;
                }),
            ]);
            if (workerPolicy == "pending")
            {
                Assert.AreEqual(0, Volatile.Read(ref reads));
                Assert.IsFalse(core.IsCompleted);
            }
            gate.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, original, publication.Task);
            Exception? cancellation = skip ? callerError : readerPolicy == "null" ? null : readerError;
            if (workerPolicy == "pending") Assert.AreSame(readerError, errors[0]);
            else if (cancellation is null) Assert.AreSame(workerError, errors[0]);
            else
            {
                var aggregate = Assert.IsInstanceOfType<AggregateException>(errors[0]);
                Assert.AreEqual(2, aggregate.InnerExceptions.Count);
                Assert.AreSame(cancellation, aggregate.InnerExceptions[0]);
                if (workerPolicy == "null")
                    Assert.AreEqual("Worker 0 published a null original task.", aggregate.InnerExceptions[1].Message);
                else Assert.AreSame(workerError, aggregate.InnerExceptions[1]);
            }
            Assert.AreSame(workerPolicy == "worker" ? workerError : null, errors[1]);
            Assert.AreSame(workerPolicy == "publication" ? workerError : null, errors[2]);
            Assert.AreEqual(skip ? 0 : 1, Volatile.Read(ref reads));
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("history")]
    [DataRow("worker")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    [DataRow("ordered")]
    public async Task Dispose_PreservesDirectErrorOrder(string scenario)
    {
        var scope = new ReminderTaskScope();
        try
        {
            var history = new AggregateException(new InvalidOperationException("history"),
                new AggregateException(new OperationCanceledException("nested history")));
            if (scenario is "empty" or "history")
            {
                var core = Join(scope, [], scenario == "history" ? history : null);
                var errors = await scope.ObserveManyAsync(core);
                Assert.AreSame(scenario == "history" ? history : null, errors[0]);
                return;
            }
            if (scenario != "ordered")
            {
                var kind = scenario == "worker" ? "fault" : scenario;
                var expected = ReminderTaskScope.Failure(kind, "single");
                var original = scope.Original(kind, expected);
                var publication = scope.Publication(original);
                var core = Join(scope, [new(static () => null, publication.Task, static () => null)]);
                var errors = await scope.ObserveManyAsync(core, original, publication.Task);
                if (scenario == "canceled") ReminderTaskScope.AssertCancellation(errors[0]);
                else Assert.AreSame(expected, errors[0]);
                ReminderTaskScope.AssertOriginal(original, errors[1], kind, expected);
                Assert.IsNull(errors[2]);
                Assert.AreEqual(scenario is "faulted-oce" or "canceled", core.IsCanceled);
                return;
            }

            var repeated = new InvalidOperationException("same reference, distinct slots");
            var reader = new AggregateException(new AggregateException(new InvalidOperationException("reader")));
            var publicationError = new AggregateException(new OperationCanceledException("publication"));
            var first = scope.Original("fault", repeated);
            var second = scope.Track(Task.CompletedTask);
            var firstPublication = scope.Publication(first);
            var secondPublication = scope.Publication(second, pending: true);
            secondPublication.TrySetException(publicationError);
            var ordered = Join(scope,
            [
                new(() => repeated, firstPublication.Task, () => throw new AssertFailedException("Reader must be skipped.")),
                new(static () => null, secondPublication.Task, () => reader),
            ], history);
            var observed = await scope.ObserveManyAsync(ordered, first, second, firstPublication.Task, secondPublication.Task);
            ReminderTaskScope.AssertAggregate(observed[0], history, repeated, reader, repeated, publicationError);
            Assert.AreSame(repeated, observed[1]);
            Assert.IsNull(observed[2]);
            Assert.IsNull(observed[3]);
            Assert.AreSame(publicationError, observed[4]);
            Assert.IsTrue(ordered.IsFaulted);
        }
        finally { await scope.FinishAsync(); }
    }

    private static Task Join(ReminderTaskScope scope, WorkerJoin[] workers, Exception? history = null)
        => scope.Track(AltaReminderService.DisposeReminderWorkersAsync(workers, history));
}

// Shared only by these two new inert fixtures: task containment/assertions, not a production
// state machine or substitute core. No filesystem, localization, logger or version initialization.
internal sealed class ReminderTaskScope
{
    private readonly ConcurrentQueue<Task> _tasks = new();
    private readonly ConcurrentQueue<Action> _releases = new();
    private readonly ConcurrentQueue<Exception> _permanentFailures = new();

    public T Track<T>(T task) where T : Task { _tasks.Enqueue(task); return task; }
    public void RecordFailure(Exception error) => _permanentFailures.Enqueue(error);

    public TaskCompletionSource<bool> Gate()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Track(gate.Task);
        _releases.Enqueue(() => gate.TrySetResult(true));
        return gate;
    }

    public TaskCompletionSource<Task> Publication(Task original, bool pending = false)
    {
        var publication = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Track(publication.Task);
        _releases.Enqueue(() => publication.TrySetResult(original));
        if (!pending) publication.TrySetResult(original);
        return publication;
    }

    public Task Original(string outcome, Exception? expected) => Track(outcome switch
    {
        "success" => Task.CompletedTask,
        "canceled" => Task.FromCanceled(new CancellationToken(true)),
        "fault" or "faulted-oce" or "nested" => Task.FromException(expected ?? throw new AssertFailedException("Missing synthetic error.")),
        _ => throw new AssertFailedException("Unknown original outcome: " + outcome),
    });

    public static Exception? Failure(string outcome, string name) => outcome switch
    {
        "fault" or "worker" or "release-fault" or "publication-fault" or "sync-fault" => new InvalidOperationException(name),
        "oce" or "faulted-oce" or "sync-oce" => new OperationCanceledException(name),
        "nested" or "sync-nested" => new AggregateException(new InvalidOperationException(name),
            new AggregateException(new OperationCanceledException("nested"))),
        _ => null,
    };

    public static void AssertCancellation(Exception? error)
        => Assert.AreEqual(new CancellationToken(true), Assert.IsInstanceOfType<OperationCanceledException>(error).CancellationToken);

    public static void AssertOriginal(Task original, Exception? error, string outcome, Exception? expected)
    {
        if (outcome == "canceled") AssertCancellation(error);
        else Assert.AreSame(outcome == "success" ? null : expected, error);
        Assert.AreEqual(outcome == "canceled", original.IsCanceled);
        Assert.AreEqual(outcome is "fault" or "faulted-oce" or "nested", original.IsFaulted);
    }

    public static void AssertAggregate(Exception? error, params Exception[] expected)
    {
        var aggregate = Assert.IsInstanceOfType<AggregateException>(error);
        Assert.AreEqual(expected.Length, aggregate.InnerExceptions.Count);
        for (var index = 0; index < expected.Length; index++)
            Assert.AreSame(expected[index], aggregate.InnerExceptions[index]);
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
        catch (TimeoutException error)
        {
            // A timeout in body, context or either teardown pass permanently fails this scope.
            // Later completion can observe an original, but never rehabilitates this evidence.
            RecordFailure(error);
            if (task.IsCompleted)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception) { }
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
        List<Task<Exception?>> observers = [];
        foreach (var task in tasks) observers.Add(ObserveAsync(task));
        // All independent five-second observers exist before this sole aggregate site.
        var aggregate = Track(Task.WhenAll(observers));
        return await aggregate.ConfigureAwait(false);
    }

    public async Task FinishAsync()
    {
        foreach (var release in _releases) release();
        var first = _tasks.ToArray();
        Exception? firstError = null;
        try { await ObserveManyAsync(first).ConfigureAwait(false); }
        catch (Exception error) { firstError = error; }
        // Include any inert signal/publication registered by a callback during the first pass.
        foreach (var release in _releases) release();
        var second = _tasks.ToArray();
        Exception? secondError = null;
        try { await ObserveManyAsync(second).ConfigureAwait(false); }
        catch (Exception error) { secondError = error; }
        List<Exception> failures = [.. _permanentFailures];
        if (firstError is not null and not TimeoutException) failures.Add(firstError);
        if (secondError is not null and not TimeoutException) failures.Add(secondError);
        if (!first.All(static task => task.IsCompleted) || !second.All(static task => task.IsCompleted))
            failures.Add(new AssertFailedException("Retained originals/wrappers did not terminate; no release evidence."));
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
        // No actual resource is released by these fixtures. Finish itself is awaited by the
        // caller's finally, not inserted into the collection it would have to join.
    }
}
