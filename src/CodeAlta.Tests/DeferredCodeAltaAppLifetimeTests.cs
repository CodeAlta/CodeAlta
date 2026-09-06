using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class DeferredCodeAltaAppLifetimeTests
{
    [TestMethod]
    [DataRow("first-none")]
    [DataRow("first-active")]
    [DataRow("repeat")]
    [DataRow("stopped")]
    [DataRow("stopped-repeat")]
    public async Task BeginRun_EnforcesAdmission(string state)
    {
        var recording = new Recording();
        try
        {
            var runStarted = false;
            var parent = recording.NewSource();
            var token = state == "first-none" ? CancellationToken.None : parent.Token;
            if (state is "repeat" or "stopped-repeat")
            {
                recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, false, token));
            }

            if (state is "stopped" or "stopped-repeat")
            {
                var failure = Assert.ThrowsExactly<ObjectDisposedException>(() =>
                    recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, true, token)));
                Assert.AreEqual("DeferredCodeAltaApp", failure.ObjectName);
                Assert.AreEqual(state == "stopped-repeat", runStarted);
            }
            else if (state == "repeat")
            {
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, false, token)));
                Assert.IsTrue(runStarted);
            }
            else
            {
                var linked = recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, false, token));
                Assert.IsTrue(runStarted);
                Assert.IsTrue(linked.Token.CanBeCanceled);
                Assert.AreNotEqual(token, linked.Token);
                Assert.IsFalse(linked.IsCancellationRequested);
            }
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("later-parent")]
    [DataRow("already-canceled-parent")]
    [DataRow("owner-with-parent-none")]
    public async Task BeginRun_ForwardsLinkedCancellation(string state)
    {
        var recording = new Recording();
        try
        {
            var parent = recording.NewSource();
            if (state == "already-canceled-parent") parent.Cancel();
            var parentToken = state == "owner-with-parent-none" ? CancellationToken.None : parent.Token;
            var runStarted = false;
            var linked = recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, false, parentToken));
            recording.UseStartupSource(linked);
            var resource = recording.Resource("services");
            var original = recording.Track(Task.FromResult(resource));
            Task<RecordingDisposable>? retained = null;
            var receivedToken = CancellationToken.None;
            var calls = 0;
            var admitted = DeferredCodeAltaApp.TryStartDeferredServices(
                false, ref retained, linked.Token, token =>
                {
                    receivedToken = token;
                    calls++;
                    return original;
                });

            Assert.IsTrue(admitted);
            Assert.AreSame(original, retained);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(linked.Token, receivedToken);
            Assert.AreNotEqual(parentToken, receivedToken);
            Assert.AreEqual(state == "already-canceled-parent", receivedToken.IsCancellationRequested);
            if (state == "owner-with-parent-none") linked.Cancel();
            else parent.Cancel();
            Assert.IsTrue(receivedToken.IsCancellationRequested);
            if (state == "owner-with-parent-none") Assert.IsFalse(parent.IsCancellationRequested);
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TryStart_ValidatesFactory(bool stopped)
    {
        var recording = new Recording();
        try
        {
            Task<RecordingDisposable>? retained = null;
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
                DeferredCodeAltaApp.TryStartDeferredServices(stopped, ref retained, CancellationToken.None, null!));
            Assert.AreEqual("startServices", failure.ParamName);
            Assert.IsNull(retained);
            recording.AssertEvents();
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    public async Task TryStart_RetainsOriginalTask(string outcome)
    {
        var recording = new Recording();
        try
        {
            var source = recording.NewStartupSource();
            if (outcome == "canceled") source.Cancel();
            Exception? expected = outcome switch
            {
                "fault" => new InvalidOperationException("startup"),
                "canceled" => new OperationCanceledException("startup", source.Token),
                _ => null,
            };
            var gate = recording.Gate();
            var resource = recording.Resource("services");
            var original = recording.Startup(outcome == "pending" ? gate.Task : Task.CompletedTask, resource, expected);
            Task<RecordingDisposable>? retained = null;
            var calls = 0;
            Task<RecordingDisposable> Start(CancellationToken token)
            {
                calls++;
                return original;
            }

            Assert.IsTrue(DeferredCodeAltaApp.TryStartDeferredServices(false, ref retained, source.Token, Start));
            Assert.IsTrue(DeferredCodeAltaApp.TryStartDeferredServices(false, ref retained, source.Token, Start));
            Assert.AreSame(original, retained);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(outcome != "pending", original.IsCompleted);
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(original));
            if (expected is null) Assert.AreSame(resource, original.GetAwaiter().GetResult());
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TryStart_RejectsStoppedAdmission(bool hasTask)
    {
        var recording = new Recording();
        try
        {
            var original = recording.Track(Task.FromResult(recording.Resource("services")));
            Task<RecordingDisposable>? retained = hasTask ? original : null;
            var calls = 0;
            var admitted = DeferredCodeAltaApp.TryStartDeferredServices(
                true, ref retained, CancellationToken.None, _ =>
                {
                    calls++;
                    return original;
                });
            Assert.IsFalse(admitted);
            Assert.AreEqual(0, calls);
            Assert.AreSame(hasTask ? original : null, retained);
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    public async Task DisposalStop_PrecedesCancellationCallbackAdmission()
    {
        var recording = new Recording();
        try
        {
            var source = recording.NewStartupSource();
            var unexpectedTask = recording.Track(Task.FromResult(recording.Resource("services")));
            var stopped = false;
            var runStarted = false;
            var observedStop = false;
            var observedPublishedLazy = true;
            var admitted = true;
            var calls = 0;
            Exception? runFailure = null;
            Task<RecordingDisposable>? retained = null;
            Lazy<Task>? lazy = null;
            // Cancellation runs only on first Value access, after the lazy has been assigned.
            recording.Register(source.Token, () =>
            {
                observedStop = stopped;
                observedPublishedLazy = lazy!.IsValueCreated;
                try
                {
                    recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, stopped, source.Token));
                }
                catch (Exception ex)
                {
                    runFailure = ex;
                }

                admitted = DeferredCodeAltaApp.TryStartDeferredServices(stopped, ref retained, source.Token, _ =>
                {
                    calls++;
                    return unexpectedTask;
                });
            });
            lazy = DeferredCodeAltaApp.CreateDeferredDisposal(
                () => { stopped = true; recording.Record("stop"); },
                () => recording.Dispose(null, null));
            var disposal = recording.Track(lazy.Value);
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.IsTrue(observedStop);
            Assert.IsFalse(observedPublishedLazy);
            Assert.IsInstanceOfType<ObjectDisposedException>(runFailure);
            Assert.IsFalse(runStarted);
            Assert.IsFalse(admitted);
            Assert.IsNull(retained);
            Assert.AreEqual(0, calls);
            recording.AssertEvents("stop", "cancel", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("stop", "stopStartup")]
    [DataRow("core", "disposeCore")]
    [DataRow("all", "stopStartup")]
    public async Task DisposalFactory_ValidatesCallbacks(string missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() => DeferredCodeAltaApp.CreateDeferredDisposal(
                missing is "stop" or "all" ? null! : () => recording.Record("stop"),
                missing is "core" or "all" ? null! : () => recording.Dispose(null, null)));
            Assert.AreEqual(parameter, failure.ParamName);
            recording.AssertEvents();
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    public async Task DisposalFactory_IsColdAndStartsInline()
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var app = recording.Resource("app", () => new ValueTask(recording.Operation(gate.Task)));
            var lazy = DeferredCodeAltaApp.CreateDeferredDisposal(
                () => recording.Record("stop"), () => recording.Dispose(app, null));
            Assert.IsFalse(lazy.IsValueCreated);
            recording.AssertEvents();
            var disposal = recording.Track(lazy.Value);
            Assert.IsFalse(disposal.IsCompleted);
            Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertEvents("stop", "cancel", "app");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("stop", "cancel", "app", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("success", false)]
    [DataRow("success", true)]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    [DataRow("canceled", false)]
    [DataRow("canceled", true)]
    public async Task DisposalFactory_CachesPendingAndTerminalOutcomes(string outcome, bool pending)
    {
        var recording = new Recording();
        try
        {
            var canceled = recording.NewSource();
            canceled.Cancel();
            Exception? expected = outcome switch
            {
                "fault" => new InvalidOperationException("app"),
                "canceled" => new OperationCanceledException("app", canceled.Token),
                _ => null,
            };
            var gate = recording.Gate();
            var operation = recording.Operation(pending ? gate.Task : Task.CompletedTask, expected);
            var app = recording.Resource("app", () => new ValueTask(operation));
            var stops = 0;
            var lazy = DeferredCodeAltaApp.CreateDeferredDisposal(
                () => { stops++; recording.Record("stop"); }, () => recording.Dispose(app, null));
            var first = recording.Track(lazy.Value);
            var second = recording.Track(lazy.Value);
            Assert.AreSame(first, second);
            Assert.AreEqual(!pending, first.IsCompleted);
            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(first));
            var terminal = recording.Track(lazy.Value);
            Assert.AreSame(first, terminal);
            Assert.AreSame(expected, await ObserveAsync(terminal));
            Assert.AreEqual(outcome == "success", first.IsCompletedSuccessfully);
            Assert.AreEqual(outcome == "fault", first.IsFaulted);
            Assert.AreEqual(outcome == "canceled", first.IsCanceled);
            Assert.AreEqual(1, stops);
            Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertEvents("stop", "cancel", "app", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    public async Task DisposalFactory_ConcurrentCallersSharePendingTask()
    {
        var recording = new Recording();
        try
        {
            var launch = recording.Gate();
            var acquired = recording.Gate();
            var release = recording.Gate();
            var operation = recording.Operation(release.Task);
            var app = recording.Resource("app", () => new ValueTask(operation));
            var stops = 0;
            var arrivals = 0;
            var lazy = DeferredCodeAltaApp.CreateDeferredDisposal(
                () => { Interlocked.Increment(ref stops); recording.Record("stop"); },
                () => recording.Dispose(app, null));
            async Task<Task> AccessAsync()
            {
                await launch.Task.ConfigureAwait(false);
                // Retain the returned operation before bookkeeping or any assertion can fail.
                var disposal = recording.Track(lazy.Value);
                if (Interlocked.Increment(ref arrivals) == 4) acquired.TrySetResult(true);
                return disposal;
            }

            var callers = new Task<Task>[4];
            for (var i = 0; i < callers.Length; i++) callers[i] = recording.Track(AccessAsync());
            launch.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(acquired.Task));
            var observations = callers.Select(ObserveAsync).ToArray();
            await Task.WhenAll(observations);
            var first = callers[0].GetAwaiter().GetResult();
            foreach (var caller in callers) Assert.AreSame(first, caller.GetAwaiter().GetResult());
            Assert.IsFalse(first.IsCompleted);
            Assert.AreEqual(1, stops);
            Assert.AreEqual(1, app.DisposeCalls);
            release.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(first));
            recording.AssertEvents("stop", "cancel", "app", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("cancel", "cancelStartup")]
    [DataRow("update", "disposeUpdate")]
    [DataRow("presenter", "disposePresenter")]
    [DataRow("cts", "disposeStartupCancellation")]
    [DataRow("all", "cancelStartup")]
    public async Task DisposalCore_ValidatesFinalOperations(string missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                recording.Track(DeferredCodeAltaApp.DisposeDeferredStartupAsync<RecordingDisposable>(
                    null, null, null, CancellationToken.None,
                    missing is "cancel" or "all" ? null! : recording.CancelStartup,
                    missing is "update" or "all" ? null! : () => { recording.DisposeUpdate(); return ValueTask.CompletedTask; },
                    missing is "presenter" or "all" ? null! : recording.DisposePresenter,
                    missing is "cts" or "all" ? null! : recording.DisposeStartupCancellation));
            });
            Assert.AreEqual(parameter, failure.ParamName);
            recording.AssertEvents();
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("never-run")]
    [DataRow("admitted")]
    [DataRow("canceled-admitted")]
    public async Task Disposal_NoStartup_AttemptsFinalStages(string state)
    {
        var recording = new Recording();
        try
        {
            if (state != "never-run")
            {
                var parent = recording.NewSource();
                if (state == "canceled-admitted") parent.Cancel();
                var runStarted = false;
                var linked = recording.Own(DeferredCodeAltaApp.BeginDeferredRun(ref runStarted, false, parent.Token));
                recording.UseStartupSource(linked);
            }

            var disposal = recording.Dispose(null, null);
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.IsTrue(disposal.IsCompletedSuccessfully);
            Assert.AreEqual(state != "never-run", recording.StartupToken.IsCancellationRequested);
            recording.AssertEvents("cancel", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposal_UntransferredSuccess_DisposesExactResource(bool pending)
    {
        var recording = new Recording();
        try
        {
            recording.NewStartupSource();
            var startupGate = recording.Gate();
            var resourceGate = recording.Gate();
            var entered = recording.Gate();
            var resource = recording.Resource("services", () =>
            {
                var operation = recording.Operation(resourceGate.Task);
                entered.TrySetResult(true);
                return new ValueTask(operation);
            });
            var startup = recording.Startup(pending ? startupGate.Task : Task.CompletedTask, resource);
            var disposal = recording.Dispose(null, startup);
            if (pending)
            {
                Assert.IsFalse(disposal.IsCompleted);
                Assert.IsTrue(recording.StartupToken.IsCancellationRequested);
                recording.AssertEvents("cancel");
            }

            startupGate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(entered.Task));
            Assert.AreSame(resource, startup.GetAwaiter().GetResult());
            Assert.AreEqual(1, resource.DisposeCalls);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertEvents("cancel", "services");
            resourceGate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("cancel", "services", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Disposal_ObservesStartupFaults(bool pending, bool nested)
    {
        var recording = new Recording();
        try
        {
            var original = new InvalidOperationException("creation");
            var cleanup = new AggregateException(new ArgumentException("rollback"));
            Exception expected = nested ? new AggregateException(original, cleanup) : original;
            var gate = recording.Gate();
            var resource = recording.Resource("services");
            var startup = recording.Startup(pending ? gate.Task : Task.CompletedTask, resource, expected);
            var disposal = recording.Dispose(null, startup);
            if (pending)
            {
                Assert.IsFalse(disposal.IsCompleted);
                recording.AssertEvents("cancel");
            }

            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreSame(expected, await ObserveAsync(startup));
            Assert.IsTrue(disposal.IsFaulted);
            Assert.AreEqual(0, resource.DisposeCalls);
            if (nested) AssertAggregate(expected, original, cleanup);
            recording.AssertEvents("cancel", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("task", "startupTask")]
    [DataRow("exception", "exception")]
    [DataRow("all", "startupTask")]
    public async Task CancellationPredicate_ValidatesRequiredArguments(string missing, string parameter)
    {
        var recording = new Recording();
        try
        {
            var task = recording.Track(Task.CompletedTask);
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() => DeferredCodeAltaApp.IsExpectedDeferredStartupCancellation(
                missing is "task" or "all" ? null! : task,
                missing is "exception" or "all" ? null! : new InvalidOperationException("startup"),
                CancellationToken.None, false));
            Assert.AreEqual(parameter, failure.ParamName);
            recording.AssertEvents();
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("terminal-requested", false, true)]
    [DataRow("pending-requested", true, true)]
    [DataRow("terminal-before-request", false, false)]
    [DataRow("pending-no-request", true, false)]
    [DataRow("terminal-unrelated", false, false)]
    [DataRow("pending-unrelated", true, false)]
    [DataRow("terminal-default-exception", false, false)]
    [DataRow("pending-default-exception", true, false)]
    [DataRow("faulted-oce", false, false)]
    [DataRow("default-startup-token", false, false)]
    public async Task Disposal_ClassifiesStartupCancellationPrecisely(string state, bool pending, bool suppressed)
    {
        var recording = new Recording();
        try
        {
            var unrelated = recording.NewSource();
            unrelated.Cancel();
            CancellationTokenSource? source = null;
            if (state != "default-startup-token") source = recording.NewStartupSource();
            if (!pending && state is not "terminal-before-request" and not "default-startup-token")
            {
                Assert.IsNotNull(source);
                source.Cancel();
            }
            if (state == "pending-no-request") recording.CancelStartup = () => recording.Record("cancel");
            var exceptionToken = state switch
            {
                "terminal-unrelated" or "pending-unrelated" => unrelated.Token,
                "terminal-default-exception" or "pending-default-exception" or "default-startup-token" => CancellationToken.None,
                _ => recording.StartupToken,
            };
            var expected = new OperationCanceledException("startup", exceptionToken);
            var gate = recording.Gate();
            var resource = recording.Resource("services");
            var startup = state == "faulted-oce"
                ? recording.Track(Task.FromException<RecordingDisposable>(expected))
                : recording.Startup(pending ? gate.Task : Task.CompletedTask, resource, expected);
            var disposal = recording.Dispose(null, startup);
            if (pending) Assert.IsFalse(disposal.IsCompleted);
            gate.TrySetResult(true);
            Assert.AreSame(suppressed ? null : expected, await ObserveAsync(disposal));
            Assert.AreSame(expected, await ObserveAsync(startup));
            Assert.AreEqual(state == "faulted-oce", startup.IsFaulted);
            Assert.AreEqual(state != "faulted-oce", startup.IsCanceled);
            Assert.AreEqual(suppressed, disposal.IsCompletedSuccessfully);
            Assert.AreEqual(!suppressed, disposal.IsCanceled);
            Assert.AreEqual(0, resource.DisposeCalls);
            recording.AssertEvents("cancel", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("same")]
    [DataRow("same-nested")]
    [DataRow("distinct-message-match")]
    [DataRow("new-aggregate")]
    public async Task Disposal_DistinguishesReportedFailureIdentity(string state)
    {
        var recording = new Recording();
        try
        {
            Exception reported = state == "same-nested"
                ? new AggregateException(new InvalidOperationException("creation"), new AggregateException(new Exception("rollback")))
                : new InvalidOperationException("startup");
            Exception startupFailure = state switch
            {
                "distinct-message-match" => new InvalidOperationException(reported.Message),
                "new-aggregate" => new AggregateException(reported, new ArgumentException("new cleanup")),
                _ => reported,
            };
            var startup = recording.Track(Task.FromException<RecordingDisposable>(startupFailure));
            Assert.AreSame(startupFailure, await ObserveAsync(startup));
            var disposal = recording.Dispose(null, startup, reported);
            Assert.AreSame(ReferenceEquals(startupFailure, reported) ? null : startupFailure, await ObserveAsync(disposal));
            Assert.AreEqual(ReferenceEquals(startupFailure, reported), disposal.IsCompletedSuccessfully);
            if (state == "new-aggregate") Assert.AreSame(reported, ((AggregateException)startupFailure).InnerExceptions[0]);
            recording.AssertEvents("cancel", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposal_AppIsExclusiveOwner(bool fails)
    {
        var recording = new Recording();
        try
        {
            var services = recording.Resource("services");
            var startup = recording.Track(Task.FromResult(services));
            Exception? expected = fails ? new AggregateException(new InvalidOperationException("frontend"), new Exception("owned")) : null;
            var app = recording.Resource("app", () => expected is null ? ValueTask.CompletedTask : throw expected);
            var disposal = recording.Dispose(app, startup);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreEqual(1, app.DisposeCalls);
            Assert.AreEqual(0, services.DisposeCalls);
            recording.AssertEvents("cancel", "app", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("expected-cancellation")]
    public async Task Disposal_CancelCallbackFailureStillJoins(string outcome)
    {
        var recording = new Recording();
        try
        {
            var source = recording.NewStartupSource();
            var callbackFailure = new InvalidOperationException("cancel callback");
            recording.Register(source.Token, () => throw callbackFailure);
            Exception? cancelFailure = null;
            recording.CancelStartup = () =>
            {
                recording.Record("cancel");
                try { source.Cancel(); }
                catch (Exception ex) { cancelFailure = ex; throw; }
            };
            Exception? startupFailure = outcome switch
            {
                "fault" => new ArgumentException("startup"),
                "expected-cancellation" => new OperationCanceledException("startup", source.Token),
                _ => null,
            };
            var gate = recording.Gate();
            var services = recording.Resource("services");
            var startup = recording.Startup(gate.Task, services, startupFailure);
            var disposal = recording.Dispose(null, startup);
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsTrue(source.IsCancellationRequested);
            AssertAggregate(cancelFailure, callbackFailure);
            recording.AssertEvents("cancel");
            gate.TrySetResult(true);
            var failure = await ObserveAsync(disposal);
            if (outcome == "fault")
            {
                Assert.IsNotNull(cancelFailure);
                Assert.IsNotNull(startupFailure);
                AssertAggregate(failure, cancelFailure, startupFailure);
            }
            else Assert.AreSame(cancelFailure, failure);
            Assert.AreSame(startupFailure, await ObserveAsync(startup));
            Assert.AreEqual(outcome == "success" ? 1 : 0, services.DisposeCalls);
            recording.AssertEvents(outcome == "success"
                ? ["cancel", "services", "update", "presenter", "cts"]
                : ["cancel", "update", "presenter", "cts"]);
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow("app", "sync")]
    [DataRow("app", "fault")]
    [DataRow("app", "canceled")]
    [DataRow("services", "sync")]
    [DataRow("services", "fault")]
    [DataRow("services", "canceled")]
    [DataRow("update", "ordinary")]
    [DataRow("update", "canceled")]
    [DataRow("update", "nested")]
    [DataRow("presenter", "ordinary")]
    [DataRow("presenter", "canceled")]
    [DataRow("presenter", "nested")]
    [DataRow("cts", "ordinary")]
    [DataRow("cts", "canceled")]
    [DataRow("cts", "nested")]
    public async Task Disposal_ContinuesAfterIndividualCleanupFailures(string stage, string kind)
    {
        var recording = new Recording();
        try
        {
            var canceled = recording.NewSource();
            canceled.Cancel();
            Exception expected = kind switch
            {
                "canceled" => new OperationCanceledException(stage, canceled.Token),
                "nested" => new AggregateException(new InvalidOperationException(stage), new AggregateException(new Exception("nested"))),
                _ => new InvalidOperationException(stage),
            };
            var gate = recording.Gate();
            var asynchronous = (stage is "app" or "services") && (kind is "fault" or "canceled");
            Task? operation = asynchronous ? recording.Operation(gate.Task, expected) : null;
            Func<ValueTask> failResource = operation is { } operationTask ? () => new ValueTask(operationTask) : () => throw expected;
            var services = recording.Resource("services", stage == "services" ? failResource : () => ValueTask.CompletedTask);
            var startup = recording.Track(Task.FromResult(services));
            RecordingDisposable? app = stage == "app" ? recording.Resource("app", failResource) : null;
            Action failFinal = () => { recording.Record(stage); throw expected; };
            if (stage == "update") recording.DisposeUpdate = failFinal;
            if (stage == "presenter") recording.DisposePresenter = failFinal;
            if (stage == "cts") recording.DisposeStartupCancellation = failFinal;
            var disposal = recording.Dispose(app, startup);
            if (asynchronous)
            {
                Assert.IsFalse(disposal.IsCompleted);
                recording.AssertEvents("cancel", stage);
            }

            gate.TrySetResult(true);
            Assert.AreSame(expected, await ObserveAsync(disposal));
            Assert.AreEqual(kind == "canceled", disposal.IsCanceled);
            Assert.AreEqual(kind != "canceled", disposal.IsFaulted);
            Assert.AreEqual(stage == "app" ? 0 : 1, services.DisposeCalls);
            if (app is not null) Assert.AreEqual(1, app.DisposeCalls);
            recording.AssertEvents("cancel", stage == "app" ? "app" : "services", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposal_OrdersAllDirectFailures(bool hasApp)
    {
        var recording = new Recording();
        try
        {
            var canceled = recording.NewSource();
            canceled.Cancel();
            var cancel = new AggregateException(new InvalidOperationException("cancel"));
            var owner = new AggregateException(new InvalidOperationException("creation or frontend"), new AggregateException(new Exception("rollback or owned")));
            var update = new OperationCanceledException("update", canceled.Token);
            var presenter = new ArgumentException("presenter");
            var cts = new AggregateException(new InvalidOperationException("cts"));
            recording.CancelStartup = () => { recording.Record("cancel"); throw cancel; };
            recording.DisposeUpdate = () => { recording.Record("update"); throw update; };
            recording.DisposePresenter = () => { recording.Record("presenter"); throw presenter; };
            recording.DisposeStartupCancellation = () => { recording.Record("cts"); throw cts; };
            var services = recording.Resource("services");
            var startup = hasApp
                ? recording.Track(Task.FromResult(services))
                : recording.Track(Task.FromException<RecordingDisposable>(owner));
            var app = hasApp ? recording.Resource("app", () => throw owner) : null;
            var disposal = recording.Dispose(app, startup);
            AssertAggregate(await ObserveAsync(disposal), cancel, owner, update, presenter, cts);
            Assert.IsTrue(disposal.IsFaulted);
            Assert.AreEqual(0, services.DisposeCalls);
            recording.AssertEvents(hasApp
                ? ["cancel", "app", "update", "presenter", "cts"]
                : ["cancel", "update", "presenter", "cts"]);
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposal_DoesNotSuppressCleanupUsingReportedReference(bool hasApp)
    {
        var recording = new Recording();
        try
        {
            var reported = new InvalidOperationException("already recorded by live startup reporting");
            var services = recording.Resource("services", hasApp ? () => ValueTask.CompletedTask : () => throw reported);
            var startup = recording.Track(Task.FromResult(services));
            var app = hasApp ? recording.Resource("app", () => throw reported) : null;
            var disposal = recording.Dispose(app, startup, reported);
            Assert.AreSame(reported, await ObserveAsync(disposal));
            Assert.IsTrue(disposal.IsFaulted);
            Assert.AreEqual(hasApp ? 0 : 1, services.DisposeCalls);
            recording.AssertEvents("cancel", hasApp ? "app" : "services", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    public async Task Disposal_AppCleanupStartsInline()
    {
        var recording = new Recording();
        try
        {
            var source = recording.NewStartupSource();
            var cancellationThread = -1;
            var appThread = -1;
            recording.Register(source.Token, () =>
            {
                cancellationThread = Environment.CurrentManagedThreadId;
                recording.Record("callback");
            });
            var gate = recording.Gate();
            var operation = recording.Operation(gate.Task);
            var app = recording.Resource("app", () =>
            {
                appThread = Environment.CurrentManagedThreadId;
                return new ValueTask(operation);
            });
            var callingThread = Environment.CurrentManagedThreadId;
            var disposal = recording.Dispose(app, null);
            Assert.AreEqual(callingThread, cancellationThread);
            Assert.AreEqual(callingThread, appThread);
            Assert.IsFalse(disposal.IsCompleted);
            recording.AssertEvents("cancel", "callback", "app");
            gate.TrySetResult(true);
            Assert.IsNull(await ObserveAsync(disposal));
            recording.AssertEvents("cancel", "callback", "app", "update", "presenter", "cts");
        }
        finally
        {
            await recording.FinishAsync();
        }
    }

    [TestMethod]
    public async Task Disposal_PreservesCleanupSynchronizationContext()
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
            SynchronizationContext? appContext = null;
            SynchronizationContext? updateContext = null;
            SynchronizationContext? presenterContext = null;
            SynchronizationContext? ctsContext = null;
            var app = recording.Resource("app", () =>
            {
                appContext = SynchronizationContext.Current;
                return new ValueTask(operation);
            });
            recording.DisposeUpdate = () => { updateContext = SynchronizationContext.Current; recording.Record("update"); };
            recording.DisposePresenter = () => { presenterContext = SynchronizationContext.Current; recording.Record("presenter"); };
            recording.DisposeStartupCancellation = () => { ctsContext = SynchronizationContext.Current; recording.Record("cts"); };
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                disposal = recording.Dispose(app, null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Assert.AreSame(context, appContext);
            Assert.IsFalse(disposal.IsCompleted);
            gate.TrySetResult(true);
            dispatch = recording.Track(context.InvokePostedAsync());
            Assert.IsNull(await ObserveAsync(dispatch));
            Assert.IsNull(await ObserveAsync(disposal));
            Assert.AreEqual(1, context.PostCount);
            Assert.AreSame(context, updateContext);
            Assert.AreSame(context, presenterContext);
            Assert.AreSame(context, ctsContext);
            recording.AssertEvents("cancel", "app", "update", "presenter", "cts");
        }
        finally
        {
            gate?.TrySetResult(true);
            try
            {
                if (context is not null)
                {
                    // If no continuation is required, settle the independently retained inert signal.
                    if (disposal is null || disposal.IsCompleted) context.CompleteWithoutPost();
                    dispatch ??= recording.Track(context.InvokePostedAsync());
                }
            }
            finally
            {
                await recording.FinishAsync();
            }
        }
    }

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
            // A bounded-wait timeout remains a failure even if the original just became terminal.
            if (task.IsCompleted)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception) { /* Observe the original without replacing the timeout. */ }
            }
            throw;
        }
        catch (Exception) when (task.IsCompleted)
        {
            // Preserve the actual original fault/cancellation identity for normal completion.
            try { await task.ConfigureAwait(false); }
            catch (Exception original) { return original; }
            return null;
        }
    }

    private static async Task CompleteOperationAsync(Task gate, Exception? failure)
    {
        await gate.ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    private static async Task<RecordingDisposable> CompleteStartupAsync(Task gate, RecordingDisposable resource, Exception? failure)
    {
        await gate.ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        return resource;
    }

    // Instance-owned synthetic operations only. These defaults never construct production services.
    // CTS disposal below belongs to fixture containment; the core's CTS stage only records/fails.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();
        private readonly ConcurrentQueue<CancellationTokenSource> _sources = new();
        private readonly ConcurrentQueue<CancellationTokenRegistration> _registrations = new();
        private CancellationTokenSource? _startupSource;

        public Recording()
        {
            CancelStartup = () => { Record("cancel"); _startupSource?.Cancel(); };
            DisposeUpdate = () => Record("update");
            DisposePresenter = () => Record("presenter");
            DisposeStartupCancellation = () => Record("cts");
        }

        public CancellationToken StartupToken { get; private set; }
        public Action CancelStartup { get; set; }
        public Action DisposeUpdate { get; set; }
        public Action DisposePresenter { get; set; }
        public Action DisposeStartupCancellation { get; set; }

        public void Record(string stage) => _events.Enqueue(stage);

        public void AssertEvents(params string[] expected) => CollectionAssert.AreEqual(expected, _events.ToArray());

        public Task Track(Task task)
        {
            _tasks.Enqueue(task);
            return task;
        }

        public Task<T> Track<T>(Task<T> task)
        {
            _tasks.Enqueue(task);
            return task;
        }

        public CancellationTokenSource Own(CancellationTokenSource source)
        {
            _sources.Enqueue(source);
            return source;
        }

        public CancellationTokenSource NewSource() => Own(new CancellationTokenSource());

        public CancellationTokenSource NewStartupSource()
        {
            var source = NewSource();
            UseStartupSource(source);
            return source;
        }

        public void UseStartupSource(CancellationTokenSource source)
        {
            _startupSource = source;
            StartupToken = source.Token;
        }

        public void Register(CancellationToken token, Action callback) => _registrations.Enqueue(token.Register(callback));

        public TaskCompletionSource<bool> Gate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Enqueue(gate);
            Track(gate.Task);
            return gate;
        }

        public RecordingDisposable Resource(string name) => Resource(name, () => ValueTask.CompletedTask);

        public RecordingDisposable Resource(string name, Func<ValueTask> operation) => new(this, name, operation);

        public Task Operation(Task gate, Exception? failure = null) => Track(CompleteOperationAsync(gate, failure));

        public Task<RecordingDisposable> Startup(Task gate, RecordingDisposable resource, Exception? failure = null)
            => Track(CompleteStartupAsync(gate, resource, failure));

        public Task Dispose(IAsyncDisposable? app, Task<RecordingDisposable>? startup, Exception? reported = null)
            => Track(DeferredCodeAltaApp.DisposeDeferredStartupAsync(
                app, startup, reported, StartupToken,
                CancelStartup, () => { DisposeUpdate(); return ValueTask.CompletedTask; }, DisposePresenter, DisposeStartupCancellation));

        public async Task FinishAsync()
        {
            foreach (var gate in _gates) gate.TrySetResult(true);

            // First join includes every retained producer/caller. Their callbacks can register
            // additional operation tasks; none of these fixtures create gates inside callbacks.
            var firstTasks = _tasks.ToArray();
            Exception? firstFailure = null;
            try
            {
                var observations = firstTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }

            // A second finite snapshot observes callback-produced work even if the first join
            // failed. Within each pass, start ALL independent five-second observations before
            // awaiting their aggregate; one timeout must not skip an independent operation.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try
            {
                var observations = finalTasks.Select(ObserveAsync).ToArray();
                await Task.WhenAll(observations).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                finalFailure = ex;
            }

            // After actual producer and operation completion no more task registration can occur.
            // A timeout with work still running is a failed fixture, not permission to release CTS.
            // A producer becoming terminal after a timeout does not prove its later tasks were joined.
            if (firstFailure is null && finalFailure is null &&
                firstTasks.All(static task => task.IsCompleted) && finalTasks.All(static task => task.IsCompleted))
            {
                foreach (var registration in _registrations) registration.Dispose();
                foreach (var source in _sources) source.Dispose();
            }

            if (firstFailure is not null && finalFailure is not null) throw new AggregateException(firstFailure, finalFailure);
            if (firstFailure is not null) ExceptionDispatchInfo.Throw(firstFailure);
            if (finalFailure is not null) ExceptionDispatchInfo.Throw(finalFailure);
        }
    }

    private sealed class RecordingDisposable : IAsyncDisposable
    {
        private readonly Recording _recording;
        private readonly string _name;
        private readonly Func<ValueTask> _operation;
        private int _disposeCalls;

        public RecordingDisposable(Recording recording, string name, Func<ValueTask> operation)
        {
            _recording = recording;
            _name = name;
            _operation = operation;
        }

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            _recording.Record(_name);
            return _operation();
        }
    }

    // Exactly one explicitly awaited post, not a terminal dispatcher or a polling pump.
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
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
