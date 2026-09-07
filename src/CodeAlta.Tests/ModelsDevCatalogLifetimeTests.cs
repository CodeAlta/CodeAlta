using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Agent.ModelCatalog;

namespace CodeAlta.Tests;

/// <summary>Inert supplied-task tests of the mandatory models.dev selected-refresh cleanup core.</summary>
/// <remarks>
/// Only the separate static core is called; no catalog, CTS, registration, client, timer, owner,
/// provider, registry, UI, State, logger, version or host is constructed or initialized here.
/// No source/filesystem reads or test-side admission, history, Lazy or scheduling model is used.
/// These cases do not qualify service admission/sharing, BCL cancellation traversal, constructor
/// rollback, cache durability, external dependency liveness or complete shutdown. Source wiring
/// is covered separately. The future helper is intentionally absent until authorized implementation.
/// </remarks>
[TestClass]
public sealed class ModelsDevCatalogLifetimeTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(3)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task Dispose_ValidatesBeforeCallbacks(int nullMask)
    {
        var scope = new TaskScope();
        try
        {
            var calls = 0;
            Action cancel = () => calls++;
            Action releaseSource = () => calls++;
            Action releaseClient = () => calls++;
            var error = Assert.Throws<ArgumentNullException>(() =>
            {
                // If validation incorrectly returns a task, retain it before Assert.Throws fails.
                scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                    null,
                    (nullMask & 1) != 0 ? null! : cancel,
                    (nullMask & 2) != 0 ? null! : releaseSource,
                    (nullMask & 4) != 0 ? null! : releaseClient,
                    ownsHttpClient: false));
            });
            Assert.AreEqual((nullMask & 1) != 0 ? "cancel" : (nullMask & 2) != 0 ? "releaseSource" : "releaseHttpClient",
                error.ParamName);
            Assert.AreEqual(0, calls);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("faulted-oce")]
    [DataRow("canceled")]
    [DataRow("nested")]
    public async Task Dispose_PreservesJoinOnlyCancellationSuppression(string outcome)
    {
        var scope = new TaskScope();
        try
        {
            Exception? expected = outcome switch
            {
                "fault" => new InvalidOperationException("original"),
                "faulted-oce" => new OperationCanceledException("faulted original"),
                "nested" => new AggregateException(new InvalidOperationException("outer member"),
                    new AggregateException(new OperationCanceledException("nested cancellation"))),
                _ => null,
            };
            var original = outcome == "absent" ? null : scope.Original(outcome, expected);
            var events = new List<string>();
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                original,
                () => events.Add("cancel"),
                () => events.Add("source"),
                () => events.Add("client"),
                ownsHttpClient: true));
            // Original failure/cancellation is observed independently, not inferred from core success.
            var errors = original is null
                ? await scope.ObserveManyAsync(core)
                : await scope.ObserveManyAsync(core, original);
            if (original is not null) AssertOriginal(original, errors[1], outcome, expected);
            var retained = outcome is "fault" or "nested" ? expected : null;
            AssertCleanup(core, errors[0], retained);
            CollectionAssert.AreEqual(new[] { "cancel", "source", "client" }, events);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("cancel", "fault")]
    [DataRow("cancel", "oce")]
    [DataRow("source", "fault")]
    [DataRow("source", "oce")]
    [DataRow("client", "fault")]
    [DataRow("client", "oce")]
    public async Task Dispose_AttemptsLaterStagesAfterCallbackFailure(string stage, string outcome)
    {
        var scope = new TaskScope();
        try
        {
            var original = scope.Original("success", null);
            Exception expected = outcome == "oce"
                ? new OperationCanceledException(stage)
                : new InvalidOperationException(stage);
            var events = new List<string>();
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                original,
                () => { events.Add("cancel"); if (stage == "cancel") throw expected; },
                () => { events.Add("source"); if (stage == "source") throw expected; },
                () => { events.Add("client"); if (stage == "client") throw expected; },
                ownsHttpClient: true));
            var errors = await scope.ObserveManyAsync(core, original);
            AssertOriginal(original, errors[1], "success", null);
            AssertCleanup(core, errors[0], expected);
            CollectionAssert.AreEqual(new[] { "cancel", "source", "client" }, events);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dispose_PendingOriginalBlocksReleases(bool cancellationFails)
    {
        var scope = new TaskScope();
        try
        {
            // Gate task and its finally release are registered before callbacks or assertions exist.
            var original = scope.Gate();
            var expected = cancellationFails ? new InvalidOperationException("cancel") : null;
            var events = new List<string>();
            var sourceSawTerminalOriginal = false;
            var clientSawTerminalOriginal = false;
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                original.Task,
                () => { events.Add("cancel"); if (expected is not null) throw expected; },
                () => { sourceSawTerminalOriginal = original.Task.IsCompleted; events.Add("source"); },
                () => { clientSawTerminalOriginal = original.Task.IsCompleted; events.Add("client"); },
                ownsHttpClient: true));
            // The accepted helper starts inline: cancel has been attempted before it returns at the pending join.
            CollectionAssert.AreEqual(new[] { "cancel" }, events);
            Assert.IsFalse(original.Task.IsCompleted);
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse(sourceSawTerminalOriginal);
            Assert.IsFalse(clientSawTerminalOriginal);

            original.TrySetResult(true);
            var errors = await scope.ObserveManyAsync(core, original.Task);
            AssertOriginal(original.Task, errors[1], "success", null);
            AssertCleanup(core, errors[0], expected);
            Assert.IsTrue(sourceSawTerminalOriginal);
            Assert.IsTrue(clientSawTerminalOriginal);
            CollectionAssert.AreEqual(new[] { "cancel", "source", "client" }, events);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dispose_BorrowedClientIsNotReleased(bool hasOriginal)
    {
        var scope = new TaskScope();
        try
        {
            var expected = hasOriginal ? new InvalidOperationException("borrowed-client original") : null;
            var original = hasOriginal ? scope.Original("fault", expected) : null;
            var events = new List<string>();
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                original,
                () => events.Add("cancel"),
                () => events.Add("source"),
                () => { events.Add("client"); throw new InvalidOperationException("Borrowed client invoked."); },
                ownsHttpClient: false));
            var errors = original is null
                ? await scope.ObserveManyAsync(core)
                : await scope.ObserveManyAsync(core, original);
            if (original is not null) AssertOriginal(original, errors[1], "fault", expected);
            AssertCleanup(core, errors[0], expected);
            CollectionAssert.AreEqual(new[] { "cancel", "source" }, events);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("cancel-worker-source-client")]
    [DataRow("nested-repeated")]
    [DataRow("suppressed-worker-oce")]
    public async Task Dispose_PreservesDirectFailureOrder(string scenario)
    {
        var scope = new TaskScope();
        try
        {
            Exception cancelError;
            Exception workerError;
            Exception? sourceError;
            Exception? clientError;
            if (scenario == "nested-repeated")
            {
                var nested = new AggregateException(new InvalidOperationException("nested member"),
                    new AggregateException(new OperationCanceledException("nested cancellation")));
                var repeated = new InvalidOperationException("repeated reference");
                cancelError = nested;
                workerError = repeated;
                sourceError = nested;
                clientError = repeated;
            }
            else if (scenario == "suppressed-worker-oce")
            {
                cancelError = new OperationCanceledException("retained callback cancellation");
                workerError = new OperationCanceledException("suppressed faulted original");
                sourceError = null;
                clientError = null;
            }
            else
            {
                cancelError = new InvalidOperationException("cancel");
                workerError = new InvalidOperationException("worker");
                sourceError = new OperationCanceledException("source");
                clientError = new InvalidOperationException("client");
            }

            var originalOutcome = scenario == "suppressed-worker-oce" ? "faulted-oce" : "fault";
            var original = scope.Original(originalOutcome, workerError);
            var events = new List<string>();
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                original,
                () => { events.Add("cancel"); throw cancelError; },
                () => { events.Add("source"); if (sourceError is not null) throw sourceError; },
                () => { events.Add("client"); if (clientError is not null) throw clientError; },
                ownsHttpClient: true));
            var errors = await scope.ObserveManyAsync(core, original);
            AssertOriginal(original, errors[1], originalOutcome, workerError);
            if (scenario == "suppressed-worker-oce")
            {
                AssertCleanup(core, errors[0], cancelError);
            }
            else
            {
                var aggregate = Assert.IsInstanceOfType<AggregateException>(errors[0]);
                Assert.AreEqual(4, aggregate.InnerExceptions.Count);
                Assert.AreSame(cancelError, aggregate.InnerExceptions[0]);
                Assert.AreSame(workerError, aggregate.InnerExceptions[1]);
                Assert.AreSame(sourceError, aggregate.InnerExceptions[2]);
                Assert.AreSame(clientError, aggregate.InnerExceptions[3]);
                Assert.IsTrue(core.IsFaulted);
                Assert.IsFalse(core.IsCanceled);
            }

            CollectionAssert.AreEqual(new[] { "cancel", "source", "client" }, events);
        }
        finally { await scope.FinishAsync(); }
    }

    [TestMethod]
    public async Task Dispose_JoinsSelectedOriginalOnly()
    {
        var scope = new TaskScope();
        try
        {
            var selected = scope.Gate();
            var unselected = scope.Gate();
            var unselectedError = new InvalidOperationException("independent unselected original");
            var events = new List<string>();
            var core = scope.Track(ModelsDevCatalogLifetime.DisposeRefreshAsync(
                selected.Task,
                () => events.Add("cancel"),
                () => events.Add("source"),
                () => events.Add("client"),
                ownsHttpClient: true));
            // Its independent bound starts before the selected/core aggregate, while its gate stays closed.
            var unselectedObservation = scope.ObserveAsync(unselected.Task);
            Assert.IsFalse(core.IsCompleted);
            Assert.IsFalse(selected.Task.IsCompleted);
            Assert.IsFalse(unselected.Task.IsCompleted);
            CollectionAssert.AreEqual(new[] { "cancel" }, events);

            selected.TrySetResult(true);
            // The unselected observer is already retained and running, but is not joined in this phase.
            // Its original also has an unconditional finally release, independently of core.
            var selectedErrors = await scope.ObserveManyAsync(core, selected.Task);
            AssertOriginal(selected.Task, selectedErrors[1], "success", null);
            AssertCleanup(core, selectedErrors[0], null);
            Assert.IsFalse(unselected.Task.IsCompleted, "Core completion does not establish unselected termination.");
            CollectionAssert.AreEqual(new[] { "cancel", "source", "client" }, events);

            unselected.TrySetException(unselectedError);
            var allErrors = await scope.ObserveManyAsync(core, selected.Task, unselected.Task, unselectedObservation);
            AssertCleanup(core, allErrors[0], null);
            AssertOriginal(selected.Task, allErrors[1], "success", null);
            AssertOriginal(unselected.Task, allErrors[2], "fault", unselectedError);
            Assert.IsNull(allErrors[3]);
            Assert.AreSame(unselectedError, unselectedObservation.Result);
        }
        finally { await scope.FinishAsync(); }
    }

    private static void AssertOriginal(Task original, Exception? error, string outcome, Exception? expected)
    {
        if (outcome == "canceled")
        {
            Assert.AreEqual(new CancellationToken(true), Assert.IsInstanceOfType<OperationCanceledException>(error).CancellationToken);
        }
        else
        {
            Assert.AreSame(outcome == "success" ? null : expected, error);
        }

        Assert.AreEqual(outcome == "success", original.IsCompletedSuccessfully);
        Assert.AreEqual(outcome == "canceled", original.IsCanceled);
        Assert.AreEqual(outcome is "fault" or "faulted-oce" or "nested", original.IsFaulted);
        if (original.IsFaulted)
        {
            var aggregate = Assert.IsInstanceOfType<AggregateException>(original.Exception);
            Assert.AreEqual(1, aggregate.InnerExceptions.Count);
            Assert.AreSame(expected, aggregate.InnerExceptions[0]);
        }
    }

    private static void AssertCleanup(Task core, Exception? actual, Exception? expected)
    {
        Assert.AreSame(expected, actual);
        Assert.AreEqual(expected is null, core.IsCompletedSuccessfully);
        Assert.AreEqual(expected is OperationCanceledException, core.IsCanceled);
        Assert.AreEqual(expected is not null and not OperationCanceledException, core.IsFaulted);
    }

    // Per-case task containment only, not a service owner or admission/Lazy/history model.
    // Concurrent queues permit observations and completion callbacks to register owned wrappers.
    private sealed class TaskScope
    {
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<Action> _releases = new();
        private readonly ConcurrentQueue<Exception> _permanentFailures = new();

        public T Track<T>(T task) where T : Task { _tasks.Enqueue(task); return task; }

        public TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Track(gate.Task);
            _releases.Enqueue(() => gate.TrySetResult(true));
            return gate;
        }

        public Task Original(string outcome, Exception? expected) => Track(outcome switch
        {
            "success" => Task.CompletedTask,
            "canceled" => Task.FromCanceled(new CancellationToken(true)),
            "fault" or "faulted-oce" or "nested" => Task.FromException(expected ?? throw new AssertFailedException("Missing original error.")),
            _ => throw new AssertFailedException("Unknown original outcome: " + outcome),
        });

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
                // A timeout permanently fails this case, even if the original completes later.
                _permanentFailures.Enqueue(error);
                if (task.IsCompleted)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception) { }
                }

                throw;
            }
            catch (Exception) when (task.IsCompleted)
            {
                // Observe the original separately from any WaitAsync wrapper and preserve identity.
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
            // Every independent five-second observer is started and retained before this sole aggregate site.
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
            // A second finite snapshot includes observers, bounds and wrappers retained during the first pass.
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
            // The test finally awaits Finish itself; it is not included in the set it must join.
            // No actual resource is released by this inert scope, and no context routing is added.
        }
    }
}
