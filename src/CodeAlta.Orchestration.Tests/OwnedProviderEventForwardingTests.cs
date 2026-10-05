using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Controlled tasks only. These methods do not construct a host or discover providers.</summary>
[TestClass]
public sealed class OwnedProviderEventForwardingTests
{
    [TestMethod]
    public Task Callback_RetainsWorkBeforeSynchronousLaunch() => Exercise(async f =>
    {
        var attachment = f.Attach();
        var entered = f.Gate();
        _ = f.Owner.Forward(attachment, async use =>
        {
            Assert.IsTrue(f.Owner.ActiveWorkCount > 0);
            use.Dispose();
            entered.TrySetResult();
            await f.Release.Task;
        });
        await f.Wait(entered.Task);
        Assert.IsTrue(f.Owner.ActiveWorkCount > 0);
    });

    [TestMethod]
    public Task Callbacks_StartInArrivalOrderBeforeForwardReturns() => Exercise(f =>
    {
        // A provider raises events back to back (the last ones of a turn, then Idle). Each must be handed
        // over before the next one arrives: started on the thread pool, a later event could overtake.
        var attachment = f.Attach();
        var started = new List<int>();
        for (var index = 0; index < 64; index++)
        {
            var arrival = index;
            _ = f.Owner.Forward(attachment, use =>
            {
                lock (started) started.Add(arrival);
                use.Dispose();
                return Task.CompletedTask;
            });
            lock (started) Assert.AreEqual(arrival + 1, started.Count, "The callback starts before Forward returns.");
        }

        lock (started) CollectionAssert.AreEqual(Enumerable.Range(0, 64).ToArray(), started);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task SubscriptionCallback_BeforePublicationUsesCapturedAttachment() => Exercise(async f =>
    {
        var original = f.Attach(completeSetup: false);
        var projected = f.Gate();
        _ = f.Owner.Forward(original, use =>
        {
            Assert.AreSame(original, use.Attachment);
            use.Dispose();
            projected.TrySetResult();
            return Task.CompletedTask;
        });
        await f.Wait(projected.Task);
        original.CompleteSetup();
    });

    [TestMethod]
    public Task Retirement_JoinsProjectionUsesButNotWholeTails() => Exercise(async f =>
    {
        var attachment = f.Attach();
        var projected = f.Gate();
        var releaseProjection = f.Gate();
        var abort = f.Gate();
        attachment.Abort = () => { abort.TrySetResult(); return Task.CompletedTask; };
        var tail = f.Gate();
        _ = f.Owner.Forward(attachment, async use =>
        {
            projected.TrySetResult();
            await releaseProjection.Task;
            use.Dispose();
            await f.Owner.RetireAsync(attachment);
            tail.TrySetResult();
            await f.Release.Task;
        });
        await f.Wait(projected.Task);
        var retirement = f.Track(f.Owner.RetireAsync(attachment));
        await f.Wait(abort.Task);
        Assert.IsFalse(retirement.IsCompleted);
        Assert.IsFalse(attachment.Stopped);
        releaseProjection.TrySetResult();
        await f.Wait(tail.Task);
        await f.Wait(retirement);
        Assert.IsTrue(attachment.Stopped);
        Assert.IsTrue(f.Owner.ActiveWorkCount > 0);
    });

    [TestMethod]
    public Task Shutdown_JoinsCompleteTails() => Exercise(async f =>
    {
        var attachment = f.Attach();
        var entered = f.Gate();
        _ = f.Owner.Forward(attachment, async use =>
        {
            use.Dispose();
            entered.TrySetResult();
            await f.Release.Task;
        });
        await f.Wait(entered.Task);
        var close = f.Track(f.Close());
        Assert.IsFalse(close.IsCompleted);
        Assert.IsFalse(f.ActorsDisposed);
        f.Release.TrySetResult();
        await f.Wait(close);
        Assert.IsTrue(f.ActorsDisposed);
    });

    [TestMethod]
    public Task CancelledWait_DoesNotReleaseAdmittedBody() => Exercise(async f =>
    {
        var work = f.Track(f.Owner.RunAsync(async () => { await f.Release.Task; return 7; }));
        var cancelled = f.Track(work.WaitAsync(new CancellationToken(true)));
        await f.Expect<TaskCanceledException>(cancelled);
        Assert.IsFalse(work.IsCompleted);
        Assert.IsTrue(f.Owner.ActiveWorkCount > 0);
    });

    [TestMethod]
    public Task UnexpectedFailures_AreObservedAndReportedInAdmissionOrder() => Exercise(async f =>
    {
        var first = f.Track(f.Owner.RunAsync<int>(async () =>
        {
            await f.Release.Task;
            throw new FormatException("first");
        }, reportFailure: true));
        var second = f.Track(f.Owner.RunAsync<int>(() => Task.FromException<int>(new ArithmeticException("second")), reportFailure: true));
        await f.Expect<ArithmeticException>(second);
        f.Release.TrySetResult();
        await f.Expect<FormatException>(first);
        var close = f.Track(f.Close());
        var failure = await f.Expect<AggregateException>(close);
        CollectionAssert.AreEqual(new[] { "first", "second" }, failure.InnerExceptions.Select(ex => ex.Message).ToArray());
    });

    [TestMethod]
    public Task ConcurrentDispose_SharesCompletion() => Exercise(async f =>
    {
        _ = f.Track(f.Owner.RunAsync(async () => { await f.Release.Task; return true; }));
        var first = f.Track(f.Close());
        var second = f.Track(f.Close());
        Assert.AreSame(first, second);
        Assert.ThrowsExactly<ObjectDisposedException>(() => f.Owner.RunAsync(() => Task.FromResult(false)));
    });

    [TestMethod]
    public Task NoncooperativeUse_KeepsDependenciesRetained() => Exercise(async f =>
    {
        var attachment = f.Attach();
        var use = attachment.TryAcquireHandleUse()!;
        f.RetainUse(use);
        var abort = f.Gate();
        attachment.Abort = () => { abort.TrySetResult(); return Task.CompletedTask; };
        var retirement = f.Track(f.Owner.RetireAsync(attachment));
        await f.Wait(abort.Task);
        Assert.IsFalse(retirement.IsCompleted);
        Assert.IsFalse(attachment.Stopped);
        use.Dispose();
        await f.Wait(retirement);
        Assert.IsTrue(attachment.Stopped);
    });

    [TestMethod]
    public Task FailedUnsubscribe_RetainsDependenciesAndFailureIdentity() => Exercise(async f =>
    {
        var unsubscribeError = new InvalidOperationException("unsubscribe failed");
        var callbackError = new FormatException("callback failed");
        var stopStarted = f.Gate();
        var attachment = f.Attach(unsubscribeFailure: unsubscribeError, onStop: () => stopStarted.TrySetResult());
        var callback = f.Track(f.Owner.Forward(attachment, _ => Task.FromException(callbackError)));
        Assert.AreSame(callbackError, await f.Expect<FormatException>(callback));
        var retirement = f.Track(f.Owner.RetireAsync(attachment));
        var retirementFailure = await f.Expect<AgentDependencyRetentionException>(retirement);
        Assert.AreEqual("attachment prerequisites", retirementFailure.Stage);
        Assert.IsNotNull(retirementFailure.Dependencies);
        var close = f.Track(f.Close());
        var closeFailure = await f.Expect<AgentDependencyRetentionException>(close);
        Assert.IsFalse(stopStarted.Task.IsCompleted);
        Assert.IsFalse(attachment.Stopped);
        Assert.IsFalse(f.ActorsDisposed);
        Assert.AreEqual("runtime drainage", closeFailure.Stage);
        Assert.IsNotNull(closeFailure.Dependencies);
        Assert.IsTrue(f.Owner.IsClosed);
        Assert.IsTrue(closeFailure.InnerExceptions.Contains(retirementFailure));
        var failures = retirementFailure.InnerExceptions.Cast<OwnedProviderEventForwarding.AttachmentFailureException>().ToArray();
        Assert.AreEqual(2, failures.Length);
        foreach (var failure in failures) Assert.IsTrue(closeFailure.InnerExceptions.Contains(failure));
        Assert.AreSame(unsubscribeError, failures[0].InnerException);
        Assert.AreEqual(3, failures[0].Stage);
        Assert.AreSame(callbackError, failures[1].InnerException);
        Assert.AreEqual(0, failures[1].Stage);
        Assert.IsTrue(failures[0].Ordinal < failures[1].Ordinal);
        foreach (var failure in failures)
        {
            Assert.AreSame(attachment.Identity, failure.Identity);
            Assert.AreEqual("fixture-session", failure.Identity.SessionId);
            Assert.AreEqual("fixture-handle", failure.Identity.HandleId);
        }
    });

    private static async Task Exercise(Func<Fixture, Task> body)
    {
        var fixture = new Fixture();
        try { await fixture.Wait(fixture.StartBody(body)); }
        catch (Exception ex) { fixture.AddFailure(ex); }
        finally { await fixture.Cleanup(); }
        fixture.ThrowFailures();
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<Task> _observers = [];
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<OwnedProviderEventForwarding.Attachment> _attachments = [];
        private readonly List<IDisposable> _releases = [];
        private readonly List<Exception> _failures = [];
        private readonly List<(Task Work, Exception Error)> _faults = [];
        private readonly List<(Task Work, Type Kind, Exception Error)> _expected = [];
        private Task? _body;
        private bool _cleaning;
        internal OwnedProviderEventForwarding Owner { get; } = new();
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ActorsDisposed { get; private set; }

        internal Task StartBody(Func<Fixture, Task> body)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _body = Track(Invoke());
            launch.TrySetResult();
            return _body;
            async Task Invoke() { await launch.Task.ConfigureAwait(false); await body(this).ConfigureAwait(false); }
        }

        internal async Task<T> Expect<T>(Task task) where T : Exception
        {
            var error = (Work: task, Kind: typeof(T), Error: await Assert.ThrowsExactlyAsync<T>(() => task));
            lock (_gate) _expected.Add(error);
            return error.Error;
        }

        internal void AddFailure(Exception error) { lock (_gate) _failures.Add(error); }

        internal void ThrowFailures()
        {
            Exception[] errors;
            lock (_gate)
            {
                errors = _failures.Concat(_faults.Where(fault => !_expected.Any(expected =>
                        ReferenceEquals(expected.Work, fault.Work) && fault.Error.GetType() == expected.Kind &&
                        // Canceled tasks can create a fresh exception per await. Faulted tasks,
                        // including those faulted with cancellation exceptions, require identity.
                        (fault.Work.IsCanceled || ReferenceEquals(expected.Error, fault.Error))))
                    .Select(fault => fault.Error)).Distinct().ToArray();
            }
            if (errors.Length == 0) return;
            var failure = new AggregateException(errors);
            failure.Data["RetainedFixture"] = this;
            throw failure;
        }

        internal void RetainUse(IDisposable use)
        {
            bool release;
            lock (_gate) { _releases.Add(use); release = _cleaning; }
            if (release) Attempt(use.Dispose);
        }

        internal TaskCompletionSource Gate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool release;
            lock (_gate) { _gates.Add(gate); release = _cleaning; }
            if (release) gate.TrySetResult();
            return gate;
        }

        internal OwnedProviderEventForwarding.Attachment Attach(bool completeSetup = true, Exception? unsubscribeFailure = null, Action? onStop = null)
        {
            var attachment = Owner.RegisterAttachment("fixture-session", "fixture-handle", () => Task.CompletedTask,
                () => { onStop?.Invoke(); return Task.CompletedTask; });
            attachment.InstallSubscription(new Subscription(unsubscribeFailure));
            bool release;
            lock (_gate) { _attachments.Add(attachment); release = _cleaning; }
            if (completeSetup || release) attachment.CompleteSetup();
            return attachment;
        }

        internal Task Close() => Owner.CloseAsync(() => Task.CompletedTask,
            () => { ActorsDisposed = true; return Task.CompletedTask; }, () => { });

        internal Task Track(Task task)
        {
            lock (_gate)
            {
                _tasks.Add(task);
                _observers.Add(Observe(task));
            }
            return task;
        }

        private async Task Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _faults.Add((task, ex)); }
        }

        internal Task Wait(Task task) => Track(task.WaitAsync(TimeSpan.FromSeconds(5)));

        internal async Task Cleanup()
        {
            TaskCompletionSource[] gates;
            OwnedProviderEventForwarding.Attachment[] attachments;
            IDisposable[] releases;
            lock (_gate)
            {
                _cleaning = true;
                gates = [.. _gates]; attachments = [.. _attachments]; releases = [.. _releases];
            }
            Attempt(() => Release.TrySetResult());
            foreach (var gate in gates) Attempt(() => gate.TrySetResult());
            foreach (var attachment in attachments) Attempt(attachment.CompleteSetup);
            foreach (var release in releases) Attempt(release.Dispose);
            Task? close = null;
            Attempt(() => close = Track(Close()));
            var producers = new List<Task>();
            if (_body is not null) producers.Add(_body);
            if (close is not null) producers.Add(close);
            await Drain(producers).ConfigureAwait(false);
            Task[] available;
            lock (_gate) available = [.. _tasks, .. _observers];
            // This is final only after BOTH producers settle. On timeout it is diagnostic only;
            // the thrown failure retains this fixture, its owner, and any late work records.
            await Drain(available).ConfigureAwait(false);
            if ((_body is null || _body.IsCompleted) && close?.IsCompleted == true)
            {
                Task[] final;
                lock (_gate) final = [.. _tasks, .. _observers];
                await Drain(final).ConfigureAwait(false);
            }
        }

        private void Attempt(Action action) { try { action(); } catch (Exception ex) { AddFailure(ex); } }

        private async Task Drain(IEnumerable<Task> tasks)
        {
            var waits = tasks.Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            var observers = waits.Select(ObserveBounded).ToArray();
            foreach (var observer in observers) await observer.ConfigureAwait(false);
        }

        private async Task ObserveBounded(Task wait)
        {
            try { await wait.ConfigureAwait(false); }
            catch (TimeoutException ex) { AddFailure(ex); }
            catch { /* Original task observers retain every non-timeout failure for final checking. */ }
        }

        private sealed class Subscription(Exception? failure) : IDisposable
        {
            public void Dispose() { if (failure is not null) throw failure; }
        }
    }
}
