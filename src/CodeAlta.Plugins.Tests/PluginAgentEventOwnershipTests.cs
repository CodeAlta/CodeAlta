using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Tests;

/// <summary>Root-free direct construction only; no activator, runtime startup or configured logging.</summary>
[TestClass]
public sealed class PluginAgentEventOwnershipTests
{
    [TestMethod]
    public async Task Fixture_UnawaitedWrapperFailureCannotPass()
    {
        var failure = new InvalidOperationException("unawaited-wrapper-original");
        var retained = await Assert.ThrowsExactlyAsync<Fixture.RetainedFixtureFailure>(() => Fixture.Run(f =>
        {
            _ = f.Keep(() => Task.FromException(failure));
            return Task.CompletedTask;
        }));
        Assert.IsNull(retained.Primary);
        Assert.AreSame(failure, retained.Cleanup);
    }

    [TestMethod]
    public async Task Fixture_DeadlineRetainsBodyAndLateInventoryUntilActualJoin()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedFinished = false;
        var retained = await Assert.ThrowsExactlyAsync<Fixture.RetainedFixtureFailure>(() => Fixture.Run(async f =>
        {
            entered.TrySetResult();
            await acquire.Task;
            f.Add((_, _) => Task.CompletedTask);
            _ = f.Keep(async () => { await finish.Task; nestedFinished = true; });
            acquired.TrySetResult();
            await finish.Task;
        }, TimeSpan.Zero, TimeSpan.Zero));
        Exception? primary = null;
        try
        {
            Assert.IsInstanceOfType<TimeoutException>(retained.Primary);
            Assert.IsInstanceOfType<TimeoutException>(retained.Cleanup);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            acquire.TrySetResult();
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsFalse(retained.Fixture.CleanupOutcome.IsCompleted);
            Assert.AreEqual(0, retained.Fixture.Plugins[0].Disposals);
        }
        catch (Exception ex) { primary = ex; }
        finally { acquire.TrySetResult(); finish.TrySetResult(); }
        Exception? cleanup;
        try { cleanup = await retained.Fixture.CleanupOutcome.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception ex) { cleanup = ex; }
        if (primary is not null || cleanup is not null)
            throw new Fixture.RetainedFixtureFailure(retained.Fixture, primary, cleanup);
        Assert.IsTrue(nestedFinished);
        Assert.AreEqual(1, retained.Fixture.Plugins[0].Disposals);
    }

    [TestMethod]
    public Task Admission_64Originals_NoWaiters_ExplicitCapacityAndClosing()
        => Fixture.Run(async f =>
        {
            var entered = f.Gate();
            var release = f.Gate();
            var count = 0;
            var active = f.Add(async (_, _) =>
            {
                if (Interlocked.Increment(ref count) == 64) entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            });
            for (var i = 0; i < 64; i++) _ = f.Keep(() => f.Observe(active));
            await entered.Task;
            Assert.AreEqual(64, active.AgentEventAdmission.Outstanding);
            var rejected = await f.Observe(active);
            StringAssert.Contains(rejected.Single().Message, "Capacity");
            Assert.AreEqual(64, count);
            Assert.AreEqual(1L, active.AgentEventAdmission.CapacityRejected);
            var drain = active.QuiesceAgentEventsAsync();
            f.Track(drain);
            Assert.IsTrue(active.AgentEventAdmission.Closed);
            rejected = await f.Observe(active); // Stale snapshots must not disappear in a validity prefilter.
            StringAssert.Contains(rejected.Single().Message, "Closing");
            Assert.IsFalse(drain.IsCompleted);
            release.SetResult();
            await drain;
            Assert.AreEqual(0, active.AgentEventAdmission.Outstanding);
        });

    [TestMethod]
    public Task Outcomes_PreserveReferencesNullRunIdScopeAndFailurePolicy()
        => Fixture.Run(async f =>
        {
            var fault = new InvalidOperationException("callback-original");
            var contexts = new List<PluginAgentEventContext>();
            var first = f.Add((context, _) => { contexts.Add(context); return Task.FromException(fault); });
            var last = f.Add((context, _) => { contexts.Add(context); return Task.CompletedTask; });
            var diagnostics = await f.Keep(() => f.Adapter.ObserveAgentEventAsync([first, last], f.Template(first),
                new PluginAdapterOperationOptions { ProjectId = "project", SessionId = "session", ProviderId = "event-provider", RunId = null }).AsTask());
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual(2, contexts.Count);
            foreach (var context in contexts)
            {
                Assert.AreSame(f.Event, context.Event);
                Assert.IsNull(context.RunId);
                Assert.AreEqual("event-provider", context.ProviderId);
                Assert.AreEqual("project", context.ProjectId);
            }
            Assert.AreSame(first.RuntimeContext.Services, contexts[0].Services);
            Assert.IsTrue(contexts[0].IsValid); // Existing ordinary-failure behavior, not a finally-invalidation fix.
            Assert.IsFalse(contexts[1].IsValid);
            var cancelled = f.Add((_, _) => Task.FromException(new OperationCanceledException("callback-oce")));
            var after = 0;
            var skipped = f.Add((_, _) => { after++; return Task.CompletedTask; });
            var cancelledDelivery = f.Keep(() => f.Adapter.ObserveAgentEventAsync([cancelled, skipped], f.Template(cancelled)).AsTask());
            var cancellation = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cancelledDelivery);
            await f.AcceptFailureAsync(cancelledDelivery, cancellation);
            Assert.AreEqual(0, after);
        });

    [TestMethod]
    public Task SelfJoin_NestedScopesRejectBeforeClosing_ExpiredFlowDoesNotReject()
        => Fixture.Run(async f =>
        {
            var delayed = f.Gate();
            Task? flowed = null;
            ActivePluginInstance? outer = null;
            var inner = f.Add(async (_, _) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => outer!.ThrowIfAgentEventSelfJoin());
                await Task.Yield();
                Assert.ThrowsExactly<InvalidOperationException>(() => outer!.ThrowIfAgentEventSelfJoin());
            });
            outer = f.Add(async (_, _) =>
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => outer!.DisposeAsync().AsTask());
                Assert.IsFalse(outer!.AgentEventAdmission.Closed);
                await f.Observe(inner);
                flowed = f.Keep(async () => { await delayed.Task; outer.ThrowIfAgentEventSelfJoin(); });
            });
            await f.Keep(() => f.Observe(outer));
            delayed.SetResult();
            await flowed!;
        });

    [TestMethod]
    public Task Deactivation_WaitCancellationDoesNotReleaseOrRestartOriginal()
        => Fixture.Run(async f =>
        {
            var entered = f.Gate();
            var release = f.Gate();
            var active = f.Add(async (_, _) => { entered.SetResult(); await release.Task; });
            var signalled = f.Gate();
            using var registration = active.LifetimeToken.Register(() => signalled.TrySetResult());
            var original = f.Keep(() => f.Observe(active));
            await entered.Task;
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var cancelledWait = f.Keep(() => active.DeactivateAsync(Timeout.InfiniteTimeSpan, cancelled.Token).AsTask());
            var waitFailure = await Fixture.ObserveOutcome(cancelledWait);
            Assert.IsInstanceOfType<OperationCanceledException>(waitFailure);
            Assert.IsNotNull(waitFailure);
            await f.AcceptFailureAsync(cancelledWait, waitFailure);
            Assert.IsNotNull(active.Instance);
            await signalled.Task;
            Assert.IsFalse(active.RuntimeContext.IsValid); // Retention does not mean validity after cancellation.
            var closing = f.Keep(() => active.DisposeAsync().AsTask());
            Assert.IsFalse(closing.IsCompleted);
            release.SetResult();
            await original;
            await closing;
            Assert.IsNull(active.Instance);
            Assert.AreEqual(1, f.Plugins[0].Deactivations);
            Assert.AreEqual(1, f.Plugins[0].Disposals);
        });

    [TestMethod]
    public Task Manager_CloseJoinsAdmittedStartAndLateActivation_NoSecondStart()
        => Fixture.Run(async f =>
        {
            var manager = new PluginRuntimeManager();
            f.AddManager(manager);
            var entered = f.Gate();
            var acquire = f.Gate();
            var finish = f.Gate();
            ActivePluginInstance? late = null;
            var start = f.Keep(() => manager.RunOwnedStartAsync(async () =>
            {
                entered.SetResult();
                await acquire.Task;
                late = f.Add((_, _) => Task.CompletedTask);
                manager.OwnActivation(late);
                Assert.IsTrue(late.AgentEventAdmission.Closed);
                Assert.ThrowsExactly<InvalidOperationException>(manager.ThrowIfAgentEventSelfJoin);
                await finish.Task;
            }));
            await entered.Task;
            var close = manager.QuiesceAgentEventsAsync();
            f.Track(close);
            Assert.AreSame(close, manager.QuiesceAgentEventsAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RunOwnedStartAsync(() => Task.CompletedTask));
            acquire.SetResult();
            Assert.IsFalse(close.IsCompleted);
            finish.SetResult();
            await start;
            await close;
            Assert.IsNotNull(late);
            Assert.IsNotNull(late.Instance); // Manager-wide borrowed-service barrier is not plugin disposal transfer.
            Assert.AreEqual(0, f.Plugins[0].Deactivations);
            Assert.AreEqual(0, f.Plugins[0].Disposals);
            StringAssert.Contains((await f.Observe(late)).Single().Message, "Closing");
        });

    [TestMethod]
    public async Task CancellationFailures_IndependentSignals_RetainBothOriginalFailuresAndDependencies()
    {
        var lifetimeFailure = new OperationCanceledException("lifetime-control-original");
        var taskFailure = new InvalidOperationException("task-control-original");
        var retained = await Assert.ThrowsExactlyAsync<Fixture.RetainedFixtureFailure>(() => Fixture.Run(async f =>
        {
            var active = f.Add((_, _) => Task.CompletedTask);
            var entered = f.Gate();
            var signalled = f.Gate();
            var release = f.Gate();
            using var registration = active.LifetimeToken.Register(() => throw lifetimeFailure);
            var handle = f.Tasks[0].Run("faulting-control", async token =>
            {
                using var taskRegistration = token.Register(() => { signalled.TrySetResult(); throw taskFailure; });
                entered.TrySetResult();
                await release.Task;
            });
            f.Track(handle.Completion);
            await entered.Task;
            var close = f.Keep(active.QuiesceAgentEventsAsync);
            await signalled.Task; // The other signal was not skipped after lifetime cancellation failed.
            Assert.IsFalse(close.IsCompleted);
            release.TrySetResult();
            var failure = await Fixture.ObserveOutcome(close);
            Assert.IsTrue(Contains(failure, lifetimeFailure));
            Assert.IsTrue(Contains(failure, taskFailure));
            Assert.IsNotNull(failure);
            await f.AcceptFailureAsync(close, failure);
            Assert.IsNotNull(active.Instance);
            Assert.AreEqual(0, f.Plugins[0].Disposals);
        }));
        Assert.IsNull(retained.Primary, "The body must succeed; only the expected retained cleanup failure is accepted.");
        Assert.IsTrue(Contains(retained.Cleanup, lifetimeFailure));
        Assert.IsTrue(Contains(retained.Cleanup, taskFailure));

        static bool Contains(Exception? root, Exception expected) => ReferenceEquals(root, expected) ||
            root is AggregateException aggregate && aggregate.InnerExceptions.Any(child => Contains(child, expected));
    }

    [TestMethod]
    public Task UpstreamCancellation_OriginalControlStillGatesRelease()
        => Fixture.Run(async f =>
        {
            using var upstream = new CancellationTokenSource();
            var active = f.Add((_, _) => Task.CompletedTask, upstream: upstream.Token);
            var entered = f.Gate();
            var release = f.Gate();
            using var registration = active.LifetimeToken.Register(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); });
            var signal = f.Keep(upstream.CancelAsync);
            await entered.Task;
            var close = f.Keep(() => active.DisposeAsync().AsTask());
            Assert.IsFalse(close.IsCompleted);
            Assert.IsNotNull(active.Instance);
            release.TrySetResult();
            await signal;
            await close;
            Assert.AreEqual(1, f.Plugins[0].Disposals);
        });

    [TestMethod]
    public Task SyncPrefix_IsOwnedBeforeEntry_TimeoutDoesNotRelease()
        => Fixture.Run(async f =>
        {
            var entered = f.Gate();
            var release = f.Gate();
            var active = f.Add((_, _) =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return Task.CompletedTask;
            });
            var delivery = f.Keep(() => f.Observe(active));
            await entered.Task;
            Assert.AreEqual(1, active.AgentEventAdmission.Outstanding);
            var diagnostics = await active.DeactivateAsync(TimeSpan.Zero);
            StringAssert.Contains(diagnostics.Single().Message, "remain retained");
            Assert.IsNotNull(active.Instance);
            Assert.AreEqual(0, f.Plugins[0].Disposals);
            release.TrySetResult();
            await delivery;
            await f.Keep(() => active.DisposeAsync().AsTask());
        });

    [TestMethod]
    public Task TaskService_PreCancelledLifetimeNeverEntersCallback()
        => Fixture.Run(async f =>
        {
            f.Add((_, _) => Task.CompletedTask, cancelledLifetime: true);
            var entries = 0;
            var handle = f.Tasks[0].Run("pre-cancelled", _ => { Interlocked.Increment(ref entries); return ValueTask.CompletedTask; });
            f.Track(handle.Completion);
            var failure = await Fixture.ObserveOutcome(handle.Completion);
            Assert.IsInstanceOfType<OperationCanceledException>(failure);
            Assert.IsNotNull(failure);
            await f.AcceptFailureAsync(handle.Completion, failure);
            await f.Tasks[0].WhenIdleAsync();
            Assert.AreEqual(0, entries);
            Assert.AreEqual(0, f.Tasks[0].RunningTaskCount);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task LandingRead_ReaderTimeoutOrCancellationRetainsCallbackUntilTerminal(bool cancelReader)
        => Fixture.Run(async f =>
        {
            var active = f.Add((_, _) => Task.CompletedTask);
            var entered = f.Gate();
            var release = f.Gate();
            f.Registry.Register(active.Descriptor, PluginScope.Global, null, null, PluginPoint.LandingCard,
            [
                new PluginLandingCardContribution
                {
                    Id = "held", Title = "Held",
                    GetCard = (_, _) => new ValueTask<PluginLandingCard?>(f.Keep(async () =>
                    {
                        entered.TrySetResult();
                        await release.Task;
                        Assert.AreEqual(0, f.Plugins[0].Disposals, "the original callback still uses its activation");
                        return (PluginLandingCard?)PluginLandingCard.Of("<p>late</p>");
                    })),
                },
            ], 0);
            using var cancellation = new CancellationTokenSource();
            var reading = f.Keep(() => f.Adapter.GetLandingCardEntriesAsync([active], null, null,
                cancelReader ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(50), cancellation.Token).AsTask());
            await entered.Task;
            if (cancelReader)
            {
                await cancellation.CancelAsync();
                var failure = await Fixture.ObserveOutcome(reading);
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
                await f.AcceptFailureAsync(reading, failure);
            }
            else
            {
                Assert.IsTrue((await reading).Single().Failed, "the reader ends without joining a noncooperative callback");
            }

            Assert.AreEqual(1, active.AgentEventAdmission.Outstanding, "the original, not the bounded reader, remains activation-owned");
            var diagnostics = await f.Keep(() => active.DeactivateAsync(TimeSpan.Zero).AsTask());
            StringAssert.Contains(diagnostics.Single().Message, "remain retained");
            var close = f.Keep(() => active.DisposeAsync().AsTask());
            Assert.IsFalse(close.IsCompleted);
            Assert.IsNotNull(active.Instance);
            Assert.AreEqual(0, f.Plugins[0].Disposals);
            release.TrySetResult();
            await close;
            Assert.AreEqual(1, f.Plugins[0].Disposals);
            Assert.AreEqual(0, active.AgentEventAdmission.Outstanding);
        });

    [TestMethod]
    public Task LandingRead_ClosedAdmissionRejectsTheCallbackEvenBeforeLifetimeCancellation()
        => Fixture.Run(async f =>
        {
            var active = f.Add((_, _) => Task.CompletedTask);
            var calls = 0;
            f.Registry.Register(active.Descriptor, PluginScope.Global, null, null, PluginPoint.LandingCard,
            [
                new PluginLandingCardContribution
                {
                    Id = "closed", Title = "Closed",
                    GetCard = (_, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult<PluginLandingCard?>(PluginLandingCard.Of("<p>late</p>")); },
                },
            ], 0);
            active.CloseAgentEventAdmission();
            Assert.IsFalse(active.LifetimeToken.IsCancellationRequested);

            var cards = await f.Keep(() => f.Adapter.GetLandingCardEntriesAsync([active], null, null, TimeSpan.FromSeconds(5)).AsTask());

            Assert.AreEqual(0, calls, "a stale snapshot must not start a callback after activation admission closes");
            Assert.AreEqual(0, cards.Count);
            Assert.AreEqual(1L, active.AgentEventAdmission.ClosingRejected);
        });

    [TestMethod]
    public Task LandingRead_SharesBoundedAdmissionWithoutInvokingARejectedCallback()
        => Fixture.Run(async f =>
        {
            var entered = f.Gate();
            var release = f.Gate();
            var eventCalls = 0;
            var active = f.Add(async (_, _) =>
            {
                if (Interlocked.Increment(ref eventCalls) == 64) entered.TrySetResult();
                await release.Task;
            });
            var cardCalls = 0;
            f.Registry.Register(active.Descriptor, PluginScope.Global, null, null, PluginPoint.LandingCard,
            [
                new PluginLandingCardContribution
                {
                    Id = "capacity", Title = "Capacity",
                    GetCard = (_, _) => { Interlocked.Increment(ref cardCalls); return ValueTask.FromResult<PluginLandingCard?>(null); },
                },
            ], 0);
            for (var i = 0; i < 64; i++) _ = f.Keep(() => f.Observe(active));
            await entered.Task;

            var cards = await f.Keep(() => f.Adapter.GetLandingCardEntriesAsync([active], null, null, TimeSpan.FromSeconds(5)).AsTask());

            Assert.IsTrue(cards.Single().Failed);
            Assert.AreEqual(0, cardCalls);
            Assert.AreEqual(64, active.AgentEventAdmission.Outstanding);
            Assert.AreEqual(1L, active.AgentEventAdmission.CapacityRejected);
            release.TrySetResult();
        });

    [TestMethod]
    public Task LandingRead_SelfJoinGuardFlowsIntoTheWorkerBeforeCallbackEntry()
        => Fixture.Run(async f =>
        {
            var active = f.Add((_, _) => Task.CompletedTask);
            f.Registry.Register(active.Descriptor, PluginScope.Global, null, null, PluginPoint.LandingCard,
            [
                new PluginLandingCardContribution
                {
                    Id = "self-join", Title = "Self-join",
                    GetCard = (_, _) =>
                    {
                        Assert.ThrowsExactly<InvalidOperationException>(() => active.QuiesceAgentEventsAsync());
                        Assert.IsFalse(active.AgentEventAdmission.Closed, "reject a self-join before changing admission");
                        return ValueTask.FromResult<PluginLandingCard?>(PluginLandingCard.Of("<p>still active</p>"));
                    },
                },
            ], 0);

            var cards = await f.Keep(() => f.Adapter.GetLandingCardEntriesAsync([active], null, null, TimeSpan.FromSeconds(5)).AsTask());

            Assert.IsFalse(cards.Single().Failed);
            Assert.IsFalse(active.AgentEventAdmission.Closed);
        });

    [TestMethod]
    public Task TaskService_CloseRegistersCancellationBeforeJoinAndRejectsRun()
        => Fixture.Run(async f =>
        {
            var active = f.Add((_, _) => Task.CompletedTask);
            var tasks = f.Tasks[0];
            var entered = f.Gate();
            var release = f.Gate();
            var cancellationEntered = f.Gate();
            var cancellationRelease = f.Gate();
            var handle = tasks.Run("held", async token =>
            {
                using var registration = token.Register(() => { cancellationEntered.TrySetResult(); cancellationRelease.Task.GetAwaiter().GetResult(); });
                entered.SetResult();
                await release.Task;
            });
            f.Track(handle.Completion);
            await entered.Task;
            var close = f.Keep(active.QuiesceAgentEventsAsync);
            await cancellationEntered.Task;
            Assert.ThrowsExactly<ObjectDisposedException>(() => tasks.Run("late", _ => ValueTask.CompletedTask));
            Assert.IsFalse(close.IsCompleted);
            release.SetResult();
            cancellationRelease.SetResult();
            await close;
            Assert.AreEqual(0, tasks.RunningTaskCount);
        });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Task> _originals = [];
        private readonly List<Task<Exception?>> _outcomes = [];
        private readonly Dictionary<Task, Exception> _acceptedFailures = [];
        private readonly HashSet<Task> _controls = [];
        private readonly List<ActivePluginInstance> _active = [];
        private readonly List<CleanupOperation> _disposals = [];
        private bool _draining;
        private bool _sealed;
        private CleanupOperation? _cleanup;
        // Run publishes this retained operation before returning or throwing a RetainedFixtureFailure.
        internal Task<Exception?> CleanupOutcome => _cleanup!.Outcome;
        internal List<InertPlugin> Plugins { get; } = [];
        internal List<PluginRuntimeTaskService> Tasks { get; } = [];
        internal List<PluginRuntimeManager> Managers { get; } = [];
        internal PluginContributionRegistry Registry { get; } = new();
        internal PluginContributionAdapterService Adapter { get; }
        internal AgentEvent Event { get; } = new AgentSessionUpdateEvent(new("event-provider"), "session", DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.DiffUpdated, null);
        private Fixture()
        {
            Assert.IsFalse(LogManager.IsInitialized, "Fixture requires an unconfigured logger; do not initialize process logging.");
            Adapter = new(Registry);
        }
        internal void AddManager(PluginRuntimeManager manager)
        {
            bool drain;
            lock (_gate)
            {
                Assert.IsFalse(_sealed, "Manager registration after the producer inventory was sealed.");
                Managers.Add(manager);
                drain = _draining;
            }
            if (drain) _ = Keep(manager.QuiesceAgentEventsAsync, control: true);
        }
        internal TaskCompletionSource Gate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool release;
            lock (_gate)
            {
                Assert.IsFalse(_sealed, "Gate registration after the producer inventory was sealed.");
                _gates.Add(gate);
                release = _draining;
            }
            // Late gates created by the still-owned body must not miss the release pass.
            if (release) gate.TrySetResult();
            return gate;
        }
        internal ActivePluginInstance Add(Func<PluginAgentEventContext, CancellationToken, Task> callback, bool cancelledLifetime = false, CancellationToken upstream = default)
        {
            var logger = LogManager.GetLogger(nameof(PluginAgentEventOwnershipTests) + "." + Guid.NewGuid().ToString("N"));
            Assert.IsFalse(logger.IsEnabled(LogLevel.Error), "The existing adapter log tail must take its disabled no-I/O path.");
            var services = new NoopPluginServices(logger);
            var plugin = new InertPlugin(callback);
            var lifetime = new PluginActivationLifetime(cancelledLifetime ? new CancellationToken(true) : upstream);
            var tasks = new PluginRuntimeTaskService(lifetime.Token);
            var descriptor = new PluginDescriptor { RuntimeKey = "inert-" + Guid.NewGuid().ToString("N"), TypeName = nameof(InertPlugin), AssemblyName = "inert" };
            var context = new PluginRuntimeContext
            {
                Plugin = descriptor, Host = new PluginHostInfo { ApplicationName = "inert", Version = "0", HostApiVersion = "0", UserDataDirectory = "" }, Logger = services.Logger, Services = services,
                PackageDirectory = "", LifetimeCancellationToken = lifetime.Token,
            };
            var active = new ActivePluginInstance(plugin, descriptor, null, null, context, [], Registry, tasks, lifetime);
            bool drain;
            lock (_gate)
            {
                Assert.IsFalse(_sealed, "Activation registration after the producer inventory was sealed.");
                _active.Add(active); Plugins.Add(plugin); Tasks.Add(tasks);
                drain = _draining;
            }
            if (drain) _ = Keep(active.QuiesceAgentEventsAsync, control: true);
            return active;
        }
        internal PluginAgentEventContext Template(ActivePluginInstance active) => new() { Plugin = active.Descriptor, Services = active.RuntimeContext.Services, Event = Event };
        internal async Task<IReadOnlyList<PluginRuntimeDiagnostic>> Observe(ActivePluginInstance active)
        {
            var diagnostics = await Adapter.ObserveAgentEventAsync([active], Template(active));
            // Tests intentionally exercising ordinary callback faults use Adapter directly instead.
            Assert.IsFalse(diagnostics.Any(diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error), "Unexpected callback failure.");
            return diagnostics;
        }
        internal void Track(Task original, bool control = false)
        {
            var outcome = ObserveOutcome(original);
            lock (_gate)
            {
                Assert.IsFalse(_sealed, "Operation registration after the producer inventory was sealed.");
                _originals.Add(original); _outcomes.Add(outcome);
                if (control) _controls.Add(original);
            }
        }
        internal async Task AcceptFailureAsync(Task original, Exception failure)
        {
            Task<Exception?> observer;
            lock (_gate)
            {
                Assert.IsFalse(_sealed);
                var index = _originals.IndexOf(original);
                Assert.IsTrue(index >= 0, "Only an explicitly tracked operation can have an expected failure.");
                observer = _outcomes[index];
            }
            Assert.AreSame(failure, await observer.ConfigureAwait(false));
            lock (_gate)
            {
                Assert.IsFalse(_sealed);
                _acceptedFailures.Add(original, failure);
            }
        }
        internal Task Keep(Func<Task> operation, bool control = false)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Invoke(); Track(work, control); launch.SetResult(); return work;
            async Task Invoke() { await launch.Task.ConfigureAwait(false); await operation().ConfigureAwait(false); }
        }
        internal Task<T> Keep<T>(Func<Task<T>> operation)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Invoke(); Track(work); launch.SetResult(); return work;
            async Task<T> Invoke() { await launch.Task.ConfigureAwait(false); return await operation().ConfigureAwait(false); }
        }
        internal static async Task<Exception?> ObserveOutcome(Task work) { try { await work.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }
        internal static Task Run(Func<Fixture, Task> body)
            => Run(body, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        internal static async Task Run(Func<Fixture, Task> body, TimeSpan bodyWait, TimeSpan cleanupWait)
        {
            var f = new Fixture();
            Exception? primary = null;
            var original = f.Keep(() => body(f));
            Task<Exception?> bodyOutcome;
            lock (f._gate) bodyOutcome = f._outcomes[f._originals.IndexOf(original)];
            try { primary = await bodyOutcome.WaitAsync(bodyWait); } catch (Exception ex) { primary = ex; }
            f.BeginDrain();
            // Retain one cleanup original and its observer before launch. Wait deadlines never cancel it.
            f._cleanup = new CleanupOperation(() => f.CleanupAsync(original, bodyOutcome, primary));
            f._cleanup.Launch();
            Exception? cleanup;
            try { cleanup = await f._cleanup.Outcome.WaitAsync(cleanupWait); }
            catch (Exception ex) { cleanup = ex; }
            if (primary is not null || cleanup is not null)
                throw new RetainedFixtureFailure(f, primary, cleanup);
        }

        private void BeginDrain()
        {
            TaskCompletionSource[] gates;
            ActivePluginInstance[] activePlugins;
            PluginRuntimeManager[] managers;
            lock (_gate)
            {
                _draining = true;
                gates = [.. _gates]; activePlugins = [.. _active]; managers = [.. Managers];
            }
            foreach (var gate in gates) gate.TrySetResult();
            // Non-disposing controls only. Late acquisitions also register controls while draining.
            // Every independent control is launched before CleanupAsync joins the body or any control.
            foreach (var active in activePlugins) _ = Keep(active.QuiesceAgentEventsAsync, control: true);
            foreach (var manager in managers) _ = Keep(manager.QuiesceAgentEventsAsync, control: true);
        }

        private async Task CleanupAsync(Task body, Task<Exception?> bodyOutcome, Exception? primary)
        {
            // The actual body must terminate first. A failed caller wait cannot seal its acquisition inventory.
            await bodyOutcome.ConfigureAwait(false);
            var failures = new List<Exception>();
            ActivePluginInstance[] activePlugins;
            PluginRuntimeManager[] managers;
            var controlFailed = false;
            while (true)
            {
                Task[] originals;
                Task<Exception?>[] observers;
                lock (_gate) { originals = [.. _originals]; observers = [.. _outcomes]; }
                var outcomes = await Task.WhenAll(observers).ConfigureAwait(false);
                lock (_gate)
                {
                    // A tracked producer can register a nested operation before becoming terminal.
                    // Re-snapshot until all originals AND observers in the current inventory have joined.
                    if (observers.Length != _outcomes.Count) continue;
                    _sealed = true;
                    activePlugins = [.. _active]; managers = [.. Managers];
                    for (var index = 0; index < outcomes.Length; index++)
                    {
                        if (outcomes[index] is not { } failure) continue;
                        if (_controls.Contains(originals[index]))
                        {
                            controlFailed = true;
                            failures.Add(failure);
                            continue;
                        }
                        if (ReferenceEquals(originals[index], body) && ReferenceEquals(failure, primary)) continue;
                        if (_acceptedFailures.TryGetValue(originals[index], out var expected) && ReferenceEquals(expected, failure)) continue;
                        failures.Add(failure);
                    }
                    break;
                }
            }

            if (!controlFailed)
            {
                // Disposal inventory is now sealed; retain all cleanup originals/observers before launching any.
                foreach (var active in activePlugins) _disposals.Add(new CleanupOperation(() => active.DisposeAsync().AsTask()));
                foreach (var manager in managers) _disposals.Add(new CleanupOperation(() => manager.DisposeAsync().AsTask()));
                foreach (var disposal in _disposals) disposal.Launch();
                var disposalOutcomes = await Task.WhenAll(_disposals.Select(disposal => disposal.Outcome)).ConfigureAwait(false);
                failures.AddRange(disposalOutcomes.OfType<Exception>());
            }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }

        private sealed class CleanupOperation
        {
            private readonly TaskCompletionSource _launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CleanupOperation(Func<Task> body)
            {
                Original = InvokeAsync(body);
                Outcome = ObserveOutcome(Original);
            }
            internal Task Original { get; }
            internal Task<Exception?> Outcome { get; }
            internal void Launch() => _launch.TrySetResult();
            private async Task InvokeAsync(Func<Task> body)
            {
                await _launch.Task.ConfigureAwait(false);
                await body().ConfigureAwait(false);
            }
        }
        internal sealed class RetainedFixtureFailure(Fixture fixture, Exception? primary, Exception? cleanup)
            : AggregateException("Plugin event fixture failed; uncertain originals/dependencies are retained.", new[] { primary, cleanup }.OfType<Exception>())
        {
            internal Fixture Fixture { get; } = fixture;
            internal Exception? Primary { get; } = primary;
            internal Exception? Cleanup { get; } = cleanup;
        }
    }
    private sealed class InertPlugin(Func<PluginAgentEventContext, CancellationToken, Task> callback) : PluginBase
    {
        internal int Deactivations;
        internal int Disposals;
        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default) => new(callback(context, cancellationToken));
        public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref Deactivations); return ValueTask.CompletedTask; }
        public override ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
