using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateServiceLifetimeTests
{
    [TestMethod]
    [DataRow("publish", "fresh", "publishChecking")]
    [DataRow("start", "fresh", "startCheck")]
    [DataRow("both", "fresh", "publishChecking")]
    [DataRow("publish", "started", "publishChecking")]
    [DataRow("start", "started", "startCheck")]
    [DataRow("both", "started", "publishChecking")]
    [DataRow("publish", "stopped", "publishChecking")]
    [DataRow("start", "stopped", "startCheck")]
    [DataRow("both", "stopped", "publishChecking")]
    public async Task Start_ValidatesCallbacks(string missing, string state, string parameter)
    {
        var recording = new Recording();
        try
        {
            if (state == "started") recording.Start();
            recording.StopRequested = state == "stopped";
            var events = recording.Events;
            var source = recording.Source;
            var original = recording.CheckTask;
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
                CodeAltaUpdateService.StartUpdateCheck(
                    ref recording.StartRequested, recording.StopRequested,
                    ref recording.Source, ref recording.CheckTask,
                    missing is "publish" or "both" ? null! : recording.PublishChecking,
                    missing is "start" or "both" ? null! : recording.StartCheck));
            Assert.AreEqual(parameter, failure.ParamName);
            Assert.AreSame(source, recording.Source);
            Assert.AreSame(original, recording.CheckTask);
            recording.AssertEvents(events);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancel")]
    public async Task Start_PublishesThenRetainsOriginalTask(string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var expected = OutcomeFailure(outcome == "pending" ? "success" : outcome);
            var original = recording.Operation(outcome == "pending" ? gate.Task : Task.CompletedTask, expected);
            var calls = 0;
            CancellationToken forwarded = default;
            recording.StartCheck = token =>
            {
                recording.Record("schedule");
                calls++;
                forwarded = token;
                return original;
            };
            recording.Start();
            var source = recording.Source ?? throw new AssertFailedException("Missing retained source.");
            Assert.IsTrue(recording.StartRequested);
            Assert.AreEqual(source.Token, forwarded);
            Assert.IsTrue(forwarded.CanBeCanceled);
            Assert.IsFalse(forwarded.IsCancellationRequested);
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreEqual(outcome != "pending", original.IsCompleted);
            recording.Start();
            Assert.AreSame(source, recording.Source);
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreEqual(1, calls);
            recording.AssertEvents("checking", "schedule");
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(original));
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_RejectsStoppedAdmission(bool alreadyStarted)
    {
        var recording = new Recording();
        try
        {
            if (alreadyStarted) recording.Start();
            recording.StopRequested = true;
            var original = recording.CheckTask;
            var source = recording.Source;
            var events = recording.Events;
            var failure = Assert.ThrowsExactly<ObjectDisposedException>(recording.Start);
            Assert.AreEqual(nameof(CodeAltaUpdateService), failure.ObjectName);
            Assert.AreEqual(alreadyStarted, recording.StartRequested);
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreSame(source, recording.Source);
            recording.AssertEvents(events);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Start_CapturesTokenBeforeDeferredInvocation()
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            CancellationToken publicationToken = default;
            CancellationToken admittedToken = default;
            CancellationToken workerToken = default;
            var requested = false;
            recording.PublishChecking = () =>
            {
                var retained = recording.Source ?? throw new AssertFailedException("Source must precede publication.");
                publicationToken = retained.Token;
                recording.Record("checking");
            };
            recording.StartCheck = token =>
            {
                admittedToken = token;
                return recording.Track(WorkerAsync(token));
            };
            async Task WorkerAsync(CancellationToken token)
            {
                await gate.Task.ConfigureAwait(false);
                workerToken = token;
                requested = token.IsCancellationRequested;
                recording.Record("worker");
            }

            recording.Start();
            var source = recording.Source ?? throw new AssertFailedException("Missing retained source.");
            var original = recording.CheckTask;
            var disposal = recording.Dispose();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.AreEqual(source.Token, admittedToken);
            Assert.AreEqual(publicationToken, admittedToken);
            Assert.IsTrue(admittedToken.IsCancellationRequested);
            recording.AssertEvents("checking", "cancel");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreEqual(admittedToken, workerToken);
            Assert.IsTrue(requested);
            recording.AssertEvents("checking", "cancel", "worker", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("publish")]
    [DataRow("schedule")]
    public async Task Start_FailureRemainsLatched(string stage)
    {
        var recording = new Recording();
        try
        {
            var expected = new InvalidOperationException(stage);
            if (stage == "publish") recording.PublishChecking = () => { recording.Record("checking"); throw expected; };
            else recording.StartCheck = _ => { recording.Record("schedule"); throw expected; };
            Assert.AreSame(expected, Assert.ThrowsExactly<InvalidOperationException>(recording.Start));
            var source = recording.Source ?? throw new AssertFailedException("Failed start lost its source.");
            Assert.IsTrue(recording.StartRequested);
            Assert.IsNull(recording.CheckTask);
            var events = recording.Events;
            recording.Start();
            Assert.AreSame(source, recording.Source);
            recording.AssertEvents(events);
            var disposal = recording.Dispose();
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.IsTrue(source.IsCancellationRequested);
            recording.AssertEvents(events.Concat(["cancel", "release"]).ToArray());
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("stop", "stopCheck")]
    [DataRow("core", "disposeCore")]
    [DataRow("both", "stopCheck")]
    public async Task Factory_ValidatesCallbacks(string missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
                CodeAltaUpdateService.CreateUpdateDisposal(
                    missing is "stop" or "both" ? null! : recording.Stop,
                    missing is "core" or "both" ? null! : recording.Dispose));
            Assert.AreEqual(parameter, failure.ParamName);
            Assert.IsFalse(recording.StopRequested);
            recording.AssertEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Factory_IsColdAndStopsBeforeCoreInline()
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            recording.CheckTask = recording.Operation(gate.Task);
            var thread = -1;
            var stoppedAtCancel = false;
            recording.CancelCheck = () =>
            {
                thread = Environment.CurrentManagedThreadId;
                stoppedAtCancel = recording.StopRequested;
                recording.Record("cancel");
            };
            var lazy = recording.CreateDisposal();
            recording.AssertEvents();
            Assert.IsFalse(recording.StopRequested);
            var callerThread = Environment.CurrentManagedThreadId;
            var disposal = recording.Track(lazy.Value);
            Assert.IsTrue(stoppedAtCancel);
            Assert.AreEqual(callerThread, thread);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertEvents("stop", "cancel");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("stop", "cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false, "success")]
    [DataRow(false, "fault")]
    [DataRow(false, "cancel")]
    [DataRow(true, "success")]
    [DataRow(true, "fault")]
    [DataRow(true, "cancel")]
    public async Task Factory_CachesPendingAndTerminalOutcomes(bool pending, string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var expected = OutcomeFailure(outcome);
            recording.CheckTask = recording.Operation(pending ? gate.Task : Task.CompletedTask, expected);
            var lazy = recording.CreateDisposal();
            var first = recording.Track(lazy.Value);
            var second = recording.Track(lazy.Value);
            Assert.AreSame(first, second);
            Assert.AreEqual(!pending, first.IsCompleted);
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(first));
            var terminal = recording.Track(lazy.Value);
            Assert.AreSame(first, terminal);
            Assert.AreSame(expected, await ObserveAsync(terminal));
            Assert.AreEqual(outcome == "cancel", first.IsCanceled);
            Assert.AreEqual(outcome == "fault", first.IsFaulted);
            recording.AssertEvents("stop", "cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Factory_ConcurrentCallersSharePendingTask()
    {
        var recording = new Recording();
        try
        {
            var barrier = recording.Gate();
            var gate = recording.Gate();
            recording.CheckTask = recording.Operation(gate.Task);
            var lazy = recording.CreateDisposal();
            async Task<Task> CallAsync()
            {
                await barrier.Task.ConfigureAwait(false);
                return recording.Track(lazy.Value);
            }
            var callers = Enumerable.Range(0, 4).Select(_ => recording.Track(CallAsync())).ToArray();
            var callersJoined = recording.Track(Task.WhenAll(callers));
            barrier.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(callersJoined));
            var disposals = await callersJoined;
            foreach (var disposal in disposals)
            {
                Assert.AreSame(disposals[0], disposal);
                Assert.IsFalse(disposal.IsCompleted);
            }
            recording.AssertEvents("stop", "cancel");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposals[0]));
            recording.AssertEvents("stop", "cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Disposal_StopRejectsCallbackAdmission()
    {
        var recording = new Recording();
        try
        {
            recording.Start();
            var source = recording.Source ?? throw new AssertFailedException("Missing source.");
            var original = recording.CheckTask;
            var calls = 0;
            recording.Register(source.Token, () =>
            {
                calls++;
                recording.Record("callback");
                Assert.ThrowsExactly<ObjectDisposedException>(recording.Start);
            });
            var lazy = recording.CreateDisposal();
            var disposal = recording.Track(lazy.Value);
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.AreEqual(1, calls);
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreSame(source, recording.Source);
            recording.AssertEvents("checking", "stop", "cancel", "callback", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("cancel", "cancelCheck")]
    [DataRow("release", "disposeCancellation")]
    [DataRow("both", "cancelCheck")]
    public async Task Core_ValidatesCallbacks(string missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                recording.Track(CodeAltaUpdateService.DisposeUpdateCheckAsync(
                    null,
                    missing is "cancel" or "both" ? null! : recording.CancelCheck,
                    missing is "release" or "both" ? null! : recording.ReleaseSource));
            });
            Assert.AreEqual(parameter, failure.ParamName);
            recording.AssertEvents();
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("never-started")]
    [DataRow("failed-start")]
    [DataRow("cancel-failure")]
    public async Task Core_NoTaskStillAttemptsFinalStages(string state)
    {
        var recording = new Recording();
        try
        {
            Exception? expected = null;
            if (state == "failed-start")
            {
                recording.PublishChecking = () => throw new InvalidOperationException("publish");
                Assert.ThrowsExactly<InvalidOperationException>(recording.Start);
                Assert.IsNotNull(recording.Source);
            }
            if (state == "cancel-failure")
            {
                var cancelFailure = new InvalidOperationException("cancel");
                expected = cancelFailure;
                recording.CancelCheck = () => { recording.Record("cancel"); throw cancelFailure; };
            }
            Assert.IsNull(recording.CheckTask);
            var disposal = recording.Dispose();
            Assert.AreSame(expected, await ObserveAsync(disposal));
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false, "success")]
    [DataRow(false, "fault")]
    [DataRow(false, "cancel")]
    [DataRow(true, "success")]
    [DataRow(true, "fault")]
    [DataRow(true, "cancel")]
    public async Task Core_JoinsOriginalBeforeRelease(bool pending, string outcome)
    {
        var recording = new Recording();
        try
        {
            recording.CreateSource();
            var gate = recording.Gate();
            var expected = OutcomeFailure(outcome);
            var original = recording.Operation(pending ? gate.Task : Task.CompletedTask, expected);
            recording.CheckTask = original;
            var disposal = recording.Dispose();
            if (pending)
            {
                Assert.IsFalse(disposal.IsCompleted);
                recording.AssertEvents("cancel");
            }
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreSame(original, recording.CheckTask);
            Assert.AreSame(expected, await ObserveAsync(original));
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancel")]
    public async Task Core_CancelFailureStillJoins(string outcome)
    {
        var recording = new Recording();
        try
        {
            var source = recording.CreateSource();
            var callbackFailure = new InvalidOperationException("callback");
            recording.Register(source.Token, () => throw callbackFailure);
            Exception? cancelFailure = null;
            recording.CancelCheck = () =>
            {
                recording.Record("cancel");
                try { source.Cancel(); }
                catch (Exception ex) { cancelFailure = ex; throw; }
            };
            var gate = recording.Gate();
            var checkFailure = OutcomeFailure(outcome);
            recording.CheckTask = recording.Operation(gate.Task, checkFailure);
            var disposal = recording.Dispose();
            Assert.IsFalse(disposal.IsCompleted);
            AssertAggregate(cancelFailure, callbackFailure);
            recording.AssertEvents("cancel");
            gate.TrySetResult(true);
            var failure = await ObserveAsync(disposal);
            if (checkFailure is null) Assert.AreSame(cancelFailure, failure);
            else AssertAggregate(failure, cancelFailure ?? throw new AssertFailedException("Missing cancellation failure."), checkFailure);
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("cancel")]
    [DataRow("nested")]
    public async Task Core_ReportsIndividualReleaseFailures(string kind)
    {
        var recording = new Recording();
        try
        {
            recording.CreateSource();
            recording.CheckTask = recording.Track(Task.CompletedTask);
            Exception expected = kind switch
            {
                "cancel" => new OperationCanceledException("release"),
                "nested" => new AggregateException(new InvalidOperationException("release"), new AggregateException(new Exception("nested"))),
                _ => new InvalidOperationException("release"),
            };
            recording.ReleaseSource = () => { recording.Record("release"); throw expected; };
            var disposal = recording.Dispose();
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreEqual(kind == "cancel", disposal.IsCanceled);
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("fault")]
    [DataRow("cancel")]
    public async Task Core_OrdersAllDirectFailures(string outcome)
    {
        var recording = new Recording();
        try
        {
            var cancel = new AggregateException(new InvalidOperationException("cancel"), new AggregateException(new Exception("nested cancel")));
            var check = OutcomeFailure(outcome) ?? throw new AssertFailedException("Expected a check failure.");
            var release = new AggregateException(new InvalidOperationException("release"), new AggregateException(new Exception("nested release")));
            recording.CancelCheck = () => { recording.Record("cancel"); throw cancel; };
            recording.CheckTask = recording.Operation(Task.CompletedTask, check);
            recording.ReleaseSource = () => { recording.Record("release"); throw release; };
            var disposal = recording.Dispose();
            AssertAggregate(await ObserveAsync(disposal), cancel, check, release);
            Assert.IsTrue(disposal.IsFaulted);
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_RequestsBeforeJoining()
    {
        var recording = new Recording();
        try
        {
            var source = recording.CreateSource();
            var gate = recording.Gate();
            var callbackGate = recording.Gate();
            recording.CheckTask = recording.Operation(gate.Task);
            Task? callbackTask = null;
            async Task CompleteFromCallbackAsync()
            {
                await callbackGate.Task.ConfigureAwait(false);
                gate.TrySetResult(true);
            }
            recording.Register(source.Token, () =>
            {
                recording.Record("callback");
                var started = recording.Track(CompleteFromCallbackAsync());
                Assert.IsNull(callbackTask, "Unexpected duplicate callback.");
                callbackTask = started;
            });
            var disposal = recording.Dispose();
            Assert.IsNotNull(callbackTask);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertEvents("cancel", "callback");
            callbackGate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("cancel", "callback", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("requested-exact")]
    [DataRow("unrelated")]
    [DataRow("default")]
    [DataRow("faulted-exact")]
    public async Task Core_DoesNotSuppressCanceledTaskByRequestState(string kind)
    {
        var recording = new Recording();
        try
        {
            var source = recording.CreateSource();
            source.Cancel();
            var unrelated = recording.NewSource();
            unrelated.Cancel();
            var token = kind switch
            {
                "unrelated" => unrelated.Token,
                "default" => CancellationToken.None,
                _ => source.Token,
            };
            var expected = new OperationCanceledException("check", token);
            var original = kind == "faulted-exact"
                ? recording.Track(Task.FromException(expected))
                : recording.Operation(Task.CompletedTask, expected);
            recording.CheckTask = original;
            Assert.AreEqual(kind == "faulted-exact", original.IsFaulted);
            var disposal = recording.Dispose();
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.IsTrue(disposal.IsCanceled);
            Assert.AreSame(expected, await ObserveAsync(original));
            recording.AssertEvents("cancel", "release");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("none", "success")]
    [DataRow("none", "fault")]
    [DataRow("none", "cancel")]
    [DataRow("app", "success")]
    [DataRow("app", "fault")]
    [DataRow("app", "cancel")]
    [DataRow("services", "success")]
    [DataRow("services", "fault")]
    [DataRow("services", "cancel")]
    [DataRow("startup-fault", "success")]
    [DataRow("startup-fault", "fault")]
    [DataRow("startup-fault", "cancel")]
    public async Task Deferred_AwaitsUpdateBeforeFinalStages(string branch, string outcome)
    {
        var recording = new Recording();
        try
        {
            recording.CreateSource();
            var gate = recording.Gate();
            var updateFailure = OutcomeFailure(outcome);
            recording.CheckTask = recording.Operation(gate.Task, updateFailure);
            var lazy = recording.CreateDisposal();
            var services = recording.Resource("services");
            var app = branch == "app" ? recording.Resource("app") : null;
            var startupFailure = new InvalidOperationException("startup");
            Task<RecordingDisposable>? startup = branch switch
            {
                "none" => null,
                "startup-fault" => recording.Track(Task.FromException<RecordingDisposable>(startupFailure)),
                _ => recording.Track(Task.FromResult(services)),
            };
            var disposal = recording.Deferred(app, startup, () => new ValueTask(recording.Track(lazy.Value)));
            Assert.IsFalse(disposal.IsCompleted);
            string[] prefix = branch switch
            {
                "app" => ["startup-cancel", "app", "stop", "cancel"],
                "services" => ["startup-cancel", "services", "stop", "cancel"],
                _ => ["startup-cancel", "stop", "cancel"],
            };
            recording.AssertEvents(prefix);
            gate.TrySetResult(true);
            var failure = await ObserveAsync(disposal);
            if (branch == "startup-fault" && updateFailure is not null) AssertAggregate(failure, startupFailure, updateFailure);
            else Assert.AreSame(branch == "startup-fault" ? startupFailure : updateFailure, failure);
            Assert.AreEqual(outcome == "cancel" && branch != "startup-fault", disposal.IsCanceled);
            Assert.AreEqual(branch == "services" ? 1 : 0, services.DisposeCalls);
            if (app is not null) Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertEvents(prefix.Concat(["release", "presenter", "startup-cts"]).ToArray());
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Deferred_PreservesNestedUpdateFailures()
    {
        var recording = new Recording();
        try
        {
            var cancel = new AggregateException(new InvalidOperationException("cancel"));
            var check = new AggregateException(new InvalidOperationException("check"), new AggregateException(new Exception("nested")));
            var release = new OperationCanceledException("release");
            recording.CancelCheck = () => { recording.Record("cancel"); throw cancel; };
            recording.CheckTask = recording.Track(Task.FromException(check));
            recording.ReleaseSource = () => { recording.Record("release"); throw release; };
            var owner = new InvalidOperationException("startup");
            var startup = recording.Track(Task.FromException<RecordingDisposable>(owner));
            var presenter = new InvalidOperationException("presenter");
            var cts = new InvalidOperationException("startup-cts");
            recording.DisposePresenter = () => { recording.Record("presenter"); throw presenter; };
            recording.DisposeStartupCancellation = () => { recording.Record("startup-cts"); throw cts; };
            Task? updateTask = null;
            var disposal = recording.Deferred(null, startup, () =>
            {
                var task = recording.Dispose();
                Assert.IsNull(updateTask, "Unexpected duplicate update cleanup.");
                updateTask = task;
                return new ValueTask(task);
            });
            var failure = await ObserveAsync(disposal);
            var updateFailure = await ObserveAsync(updateTask ?? throw new AssertFailedException("Update stage not reached."));
            AssertAggregate(updateFailure, cancel, check, release);
            AssertAggregate(failure, owner, updateFailure ?? throw new AssertFailedException("Missing updater aggregate."), presenter, cts);
            recording.AssertEvents("startup-cancel", "cancel", "release", "presenter", "startup-cts");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Deferred_UpdateCleanupStartsInline()
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var operation = recording.Operation(gate.Task);
            var updateThread = -1;
            var callingThread = Environment.CurrentManagedThreadId;
            var disposal = recording.Deferred(null, null, () =>
            {
                updateThread = Environment.CurrentManagedThreadId;
                recording.Record("update");
                return new ValueTask(operation);
            });
            Assert.AreEqual(callingThread, updateThread);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertEvents("startup-cancel", "update");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("startup-cancel", "update", "presenter", "startup-cts");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Deferred_UpdateAwaitPreservesFrontendContext()
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
            var operation = recording.Operation(gate.Task);
            SynchronizationContext? updateContext = null;
            SynchronizationContext? presenterContext = null;
            SynchronizationContext? ctsContext = null;
            recording.DisposePresenter = () => { presenterContext = SynchronizationContext.Current; recording.Record("presenter"); };
            recording.DisposeStartupCancellation = () => { ctsContext = SynchronizationContext.Current; recording.Record("startup-cts"); };
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                disposal = recording.Deferred(null, null, () =>
                {
                    updateContext = SynchronizationContext.Current;
                    recording.Record("update");
                    return new ValueTask(operation);
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.AreSame(context, updateContext);
            Assert.IsFalse(disposal.IsCompleted);
            gate.TrySetResult(true);
            dispatch = recording.Track(context.InvokePostedAsync());
            Assert.IsNull(await ObserveAsync(dispatch));
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.AreEqual(1, context.PostCount);
            Assert.AreSame(context, presenterContext);
            Assert.AreSame(context, ctsContext);
            recording.AssertEvents("startup-cancel", "update", "presenter", "startup-cts");
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

    private static Exception? OutcomeFailure(string outcome) => outcome switch
    {
        "success" => null,
        "fault" => new InvalidOperationException("check"),
        "cancel" => new OperationCanceledException("check"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
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
            // Observe a terminal original independently, but never rehabilitate the timeout.
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

    // Instance-owned inert operations only. Source-release callbacks record/fail; actual fixture
    // sources are released only after the retained producers, operations and disposal callers join.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();
        private readonly ConcurrentQueue<CancellationTokenSource> _sources = new();
        private readonly ConcurrentQueue<CancellationTokenRegistration> _registrations = new();

        public bool StartRequested;
        public bool StopRequested;
        public CancellationTokenSource? Source;
        public Task? CheckTask;

        public Recording()
        {
            PublishChecking = () => Record("checking");
            StartCheck = _ => Track(Task.CompletedTask);
            CancelCheck = () => { Record("cancel"); Source?.Cancel(); };
            ReleaseSource = () => Record("release");
            DisposePresenter = () => Record("presenter");
            DisposeStartupCancellation = () => Record("startup-cts");
        }

        public Action PublishChecking { get; set; }
        public Func<CancellationToken, Task> StartCheck { get; set; }
        public Action CancelCheck { get; set; }
        public Action ReleaseSource { get; set; }
        public Action DisposePresenter { get; set; }
        public Action DisposeStartupCancellation { get; set; }
        public string[] Events => _events.ToArray();

        public void Record(string stage) => _events.Enqueue(stage);
        public void AssertEvents(params string[] expected) => CollectionAssert.AreEqual(expected, Events);
        public Task Track(Task task) { _tasks.Enqueue(task); return task; }
        public Task<T> Track<T>(Task<T> task) { _tasks.Enqueue(task); return task; }

        public CancellationTokenSource NewSource()
        {
            var source = new CancellationTokenSource();
            _sources.Enqueue(source);
            return source;
        }

        public CancellationTokenSource CreateSource() => Source = NewSource();

        public void Register(CancellationToken token, Action callback) => _registrations.Enqueue(token.Register(callback));

        public TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(gate);
            Track(gate.Task);
            return gate;
        }

        public Task Operation(Task gate, Exception? failure = null) => Track(CompleteAsync(gate, failure));
        public RecordingDisposable Resource(string name) => new(this, name);

        public void Start()
        {
            try
            {
                // The production seam stores the source in this ref field before fallible callbacks.
                CodeAltaUpdateService.StartUpdateCheck(
                    ref StartRequested, StopRequested, ref Source, ref CheckTask, PublishChecking, StartCheck);
            }
            finally
            {
                if (Source is not null) _sources.Enqueue(Source);
            }
        }

        public void Stop() { StopRequested = true; Record("stop"); }
        public Lazy<Task> CreateDisposal() => CodeAltaUpdateService.CreateUpdateDisposal(Stop, Dispose);
        public Task Dispose() => Track(CodeAltaUpdateService.DisposeUpdateCheckAsync(CheckTask, CancelCheck, ReleaseSource));

        public Task Deferred(IAsyncDisposable? app, Task<RecordingDisposable>? startup, Func<ValueTask> update)
            => Track(DeferredCodeAltaApp.DisposeDeferredStartupAsync(
                app, startup, null, CancellationToken.None,
                () => Record("startup-cancel"), update, DisposePresenter, DisposeStartupCancellation));

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

            // Producers/callers can retain more tasks. No fixture creates gates in callbacks.
            // Start ALL independent observations in each finite pass before awaiting the aggregate.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try
            {
                var observations = finalTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex) { finalFailure = ex; }

            // A late terminal state does not undo a timeout or prove callback-produced work joined.
            if (firstFailure is null && finalFailure is null &&
                firstTasks.All(static task => task.IsCompleted) && finalTasks.All(static task => task.IsCompleted))
            {
                foreach (var registration in _registrations) registration.Dispose();
                if (Source is not null) _sources.Enqueue(Source);
                foreach (var source in _sources.Distinct()) source.Dispose();
            }
            if (firstFailure is not null && finalFailure is not null) throw new AggregateException(firstFailure, finalFailure);
            if (firstFailure is not null) ExceptionDispatchInfo.Throw(firstFailure);
            if (finalFailure is not null) ExceptionDispatchInfo.Throw(finalFailure);
        }
    }

    private sealed class RecordingDisposable(Recording recording, string name) : IAsyncDisposable
    {
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            recording.Record(name);
            return ValueTask.CompletedTask;
        }
    }

    // One retained inert signal and one explicitly awaited callback; not a dispatcher pump.
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
