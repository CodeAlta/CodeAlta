using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Tests;

/// <summary>Direct inert activations only; supplied event references do not qualify runtime publication or persistence.</summary>
[TestClass]
public sealed class RuntimePluginAgentEventObserverTests
{
    /// <summary>Checks immutable scalar capture, event identity, nullable run IDs and event-derived defaults.</summary>
    [TestMethod]
    public void Envelope_CapturesScalarPathAndExactEventOptions()
    {
        var supplied = Event("cOdEx", new AgentRunId("run"));
        var project = "PROJECT";
        var currentPath = "current-path";
        var envelope = RuntimePluginAgentEventEnvelope.Capture(supplied, "session", project, "working-path", "project", currentPath);
        project = "changed";
        currentPath = "changed-path";
        Assert.AreSame(supplied, envelope.Event);
        Assert.AreEqual("PROJECT", envelope.ProjectId);
        Assert.AreEqual("current-path", envelope.ProjectPath);
        Assert.AreEqual("session", envelope.SessionId);
        var options = envelope.CreateOptions();
        Assert.AreEqual("run", options.RunId);
        Assert.AreEqual("cOdEx", options.ProviderId);
        Assert.IsFalse(options.IsCodeAltaManagedProvider);
        Assert.IsFalse(options.HasInteractiveUi);
        Assert.IsFalse(options.IsHeadless);
        Assert.IsNull(options.Model);
        Assert.AreEqual("working-path", RuntimePluginAgentEventEnvelope.Capture(supplied, "session", "other", "working-path", "project", "current-path").ProjectPath);
        Assert.AreEqual("working-path", RuntimePluginAgentEventEnvelope.Capture(supplied, "session", null, "working-path", null, null).ProjectPath);
        Assert.IsNull(RuntimePluginAgentEventEnvelope.Capture(supplied, "session", "PROJECT", "working-path", "project", null).ProjectPath);
        foreach (var provider in new[] { "CoPiLoT", "other-provider" })
        {
            var captured = new RuntimePluginAgentEventEnvelope(Event(provider), "session", null, null);
            Assert.IsNull(captured.CreateOptions().RunId);
            Assert.AreEqual(provider == "other-provider", captured.CreateOptions().IsCodeAltaManagedProvider);
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => new RuntimePluginAgentEventEnvelope(null!, "session", null, null));
        Assert.ThrowsExactly<ArgumentNullException>(() => new RuntimePluginAgentEventEnvelope(supplied, null!, null, null));
    }

    /// <summary>Checks required dependencies and an empty observation-time activation snapshot.</summary>
    [TestMethod]
    public Task Observer_RequiresPolicyAndHandlesEmptySnapshot()
        => Fixture.Run(async f =>
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new RuntimePluginAgentEventObserver(f.Manager, null!));
            Assert.ThrowsExactly<ArgumentNullException>(() => new RuntimePluginAgentEventObserver(null!, static (_, _) => ValueTask.CompletedTask));
            var policyCalls = 0;
            var observer = new RuntimePluginAgentEventObserver(f.Manager, (_, _) => { policyCalls++; return ValueTask.CompletedTask; });
            var invalid = f.Observe(observer, null!);
            var invalidOutcome = await f.Outcome(invalid.Operation);
            Assert.IsInstanceOfType<ArgumentNullException>(invalidOutcome);
            var invalidFailure = (ArgumentNullException)invalidOutcome;
            Assert.AreEqual("envelope", invalidFailure.ParamName);
            f.Accept(invalid.Operation, invalidFailure);
            var call = f.Observe(observer, new RuntimePluginAgentEventEnvelope(Event(), "session", null, null));
            await f.Join(call.Operation);
            Assert.AreEqual(0, call.Diagnostics!.Count);
            Assert.AreEqual(0, policyCalls);
        });

    /// <summary>Checks late-added inert activation services, scope and the supplied published reference.</summary>
    [TestMethod]
    public Task Observer_UsesObservationSnapshotAndActualServicesScopeAndPublishedReference()
        => Fixture.Run(async f =>
        {
            var observer = new RuntimePluginAgentEventObserver(f.Manager, static (_, failure) => ValueTask.FromException(failure));
            PluginAgentEventContext? received = null;
            var active = f.Add(context => { received = context; return Task.CompletedTask; }, projectScoped: true);
            var supplied = Event();
            var envelope = new RuntimePluginAgentEventEnvelope(supplied, "session", "project", "project-path");
            var call = f.Observe(observer, envelope);
            await f.Join(call.Operation);
            Assert.AreEqual(0, call.Diagnostics!.Count);
            Assert.IsNotNull(received);
            Assert.AreSame(supplied, received.Event);
            Assert.AreSame(active.Descriptor, received.Plugin);
            Assert.AreSame(active.RuntimeContext.Services, received.Services);
            Assert.AreEqual(PluginScope.Project, received.Scope);
            Assert.AreEqual("project", received.ScopeProjectId);
            Assert.AreEqual("project-path", received.ScopeProjectPath);
            Assert.AreEqual("project", received.ProjectId);
            Assert.AreEqual("project-path", received.ProjectPath);
            Assert.AreEqual("session", received.SessionId);
            Assert.AreEqual("event-provider", received.ProviderId);
            Assert.IsNull(received.RunId);
            Assert.IsNull(received.Model);
            Assert.IsNull(received.Session);
            Assert.IsFalse(received.IsValid);

            var withRun = Event("CoPiLoT", new AgentRunId("event-run"));
            var next = f.Observe(observer, new RuntimePluginAgentEventEnvelope(withRun, "session", "project", "project-path"));
            await f.Join(next.Operation);
            Assert.IsNotNull(received);
            Assert.AreSame(withRun, received.Event);
            Assert.AreEqual("event-run", received.RunId);
            Assert.AreEqual("CoPiLoT", received.ProviderId);
        });

    /// <summary>Checks unchanged ordinary callback diagnostics, continuation and success-only invalidation.</summary>
    [TestMethod]
    public Task Observer_OrdinaryFailureContinuesAndOnlySuccessInvalidates()
        => Fixture.Run(async f =>
        {
            var failure = new InvalidOperationException("ordinary-callback");
            var contexts = new List<PluginAgentEventContext>();
            var failing = f.Add(context => { contexts.Add(context); return Task.FromException(failure); });
            var succeeding = f.Add(context => { contexts.Add(context); return Task.CompletedTask; });
            var policies = 0;
            var observer = new RuntimePluginAgentEventObserver(f.Manager, (_, _) => { policies++; return ValueTask.CompletedTask; });
            var call = f.Observe(observer, new RuntimePluginAgentEventEnvelope(Event(), "session", null, null));
            await f.Join(call.Operation);
            f.AcceptCallback(failing, call.Operation, failure);
            Assert.AreEqual(1, call.Diagnostics!.Count);
            Assert.AreEqual(2, contexts.Count);
            Assert.AreSame(failing.RuntimeContext.Services, contexts[0].Services);
            Assert.AreSame(succeeding.RuntimeContext.Services, contexts[1].Services);
            Assert.AreNotSame(contexts[0].Services, contexts[1].Services);
            Assert.IsTrue(contexts[0].IsValid);
            Assert.IsFalse(contexts[1].IsValid);
            Assert.AreEqual(0, policies);
        });

    /// <summary>Checks exact cancellation escape, handled failure and an awaited, controlled policy tail.</summary>
    [TestMethod]
    public Task Observer_OceReachesExactPolicyAndReturnedOriginalIncludesPolicyTail()
        => Fixture.Run(async f =>
        {
            var failure = new OperationCanceledException("callback-oce");
            PluginAgentEventContext? context = null;
            var failing = f.Add(value => { context = value; return Task.FromException(failure); });
            var later = 0;
            f.Add(_ => { later++; return Task.CompletedTask; });
            var entered = f.Gate();
            var release = f.Gate();
            RuntimePluginAgentEventEnvelope? policyEnvelope = null;
            Exception? policyFailure = null;
            var observer = new RuntimePluginAgentEventObserver(f.Manager, async (value, error) =>
            {
                policyEnvelope = value;
                policyFailure = error;
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            });
            var envelope = new RuntimePluginAgentEventEnvelope(Event(), "session", null, null);
            var call = f.Observe(observer, envelope);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(call.Operation.Original.Work.IsCompleted);
            Assert.AreSame(envelope, policyEnvelope);
            Assert.AreSame(failure, policyFailure);
            Assert.IsNotNull(context);
            Assert.IsTrue(context.IsValid);
            Assert.AreEqual(0, later);
            release.TrySetResult();
            await f.Join(call.Operation);
            f.AcceptCallback(failing, call.Operation, failure);
            Assert.AreEqual(0, call.Diagnostics!.Count);
        });

    /// <summary>Checks ordered exception identity for faulted, cancellation-shaped, shared and synchronously throwing policies.</summary>
    [TestMethod]
    public async Task Observer_PolicyFailurePreservesBothOrderedIdentitiesIncludingCancellationAndSharedIdentity()
    {
        foreach (var shape in new[] { "ordinary", "cancellation", "shared", "synchronous" })
        {
            await Fixture.Run(async f =>
            {
                var original = new OperationCanceledException("original");
                Exception policyFailure = shape == "shared" ? original : shape == "cancellation"
                    ? new OperationCanceledException("policy") : new InvalidOperationException("policy");
                var failing = f.Add(_ => Task.FromException(original));
                Exception? received = null;
                var observer = new RuntimePluginAgentEventObserver(f.Manager, (_, error) =>
                {
                    received = error;
                    if (shape == "synchronous") throw policyFailure;
                    return ValueTask.FromException(policyFailure);
                });
                var call = f.Observe(observer, new RuntimePluginAgentEventEnvelope(Event(), "session", null, null));
                var outcome = await f.Outcome(call.Operation);
                Assert.IsInstanceOfType<AggregateException>(outcome);
                var aggregate = (AggregateException)outcome;
                Assert.AreEqual(2, aggregate.InnerExceptions.Count);
                Assert.AreSame(original, received);
                Assert.AreSame(original, aggregate.InnerExceptions[0]);
                Assert.AreSame(policyFailure, aggregate.InnerExceptions[1]);
                f.Accept(call.Operation, aggregate);
                f.AcceptCallback(failing, call.Operation, original);
            });
        }
    }

    /// <summary>Checks activation ownership while the inert callback's synchronous prefix is blocked.</summary>
    [TestMethod]
    public Task Observer_SynchronousPrefixIsOwnedBeforeEntry()
        => Fixture.Run(async f =>
        {
            var entered = f.Gate();
            var release = f.BlockingGate();
            var active = f.Add(_ =>
            {
                entered.TrySetResult();
                release.Wait();
                return Task.CompletedTask;
            });
            var observer = new RuntimePluginAgentEventObserver(f.Manager, static (_, error) => ValueTask.FromException(error));
            var call = f.Observe(observer, new RuntimePluginAgentEventEnvelope(Event(), "session", null, null));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, active.AgentEventAdmission.Outstanding);
            Assert.IsFalse(call.Operation.Original.Work.IsCompleted);
            release.Set();
            await f.Join(call.Operation);
            Assert.AreEqual(0, active.AgentEventAdmission.Outstanding);
        });

    /// <summary>Checks explicit Closing diagnostics without invoking either a callback or the failure policy.</summary>
    [TestMethod]
    public Task Observer_ClosingIsExplicitWithoutCallbackOrPolicy()
        => Fixture.Run(async f =>
        {
            var callbacks = 0;
            f.Add(_ => { callbacks++; return Task.CompletedTask; });
            var close = f.Start(f.Manager.QuiesceAgentEventsAsync, control: true);
            await f.Join(close);
            var policies = 0;
            var observer = new RuntimePluginAgentEventObserver(f.Manager, (_, _) => { policies++; return ValueTask.CompletedTask; });
            var call = f.Observe(observer, new RuntimePluginAgentEventEnvelope(Event(), "session", null, null));
            await f.Join(call.Operation);
            Assert.AreEqual(1, call.Diagnostics!.Count);
            StringAssert.Contains(call.Diagnostics[0].Message, "Closing");
            Assert.AreEqual(0, callbacks);
            Assert.AreEqual(0, policies);
        });

    private static AgentEvent Event(string provider = "event-provider", AgentRunId? runId = null)
        => new AgentSessionUpdateEvent(new ModelProviderId(provider), "session", DateTimeOffset.UnixEpoch,
            runId, AgentSessionUpdateKind.DiffUpdated, null);

    // The fixture owns every body/call/control original before launch. No shared fixture or logging hooks are changed.
    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Operation> _operations = [];
        private readonly List<Acquisition> _acquisitions = [];
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<ManualResetEventSlim> _blockingGates = [];
        private readonly List<PluginOwnedOperation> _disposals = [];
        private readonly List<Exception> _controlFailures = [];
        private readonly List<Exception> _operationFailures = [];
        private readonly List<Exception> _callbackFailures = [];
        private readonly AsyncLocal<Operation?> _observation = new();
        private bool _draining;
        private bool _sealed;
        private bool _deadlineFailed;
        private TimeoutException? _deadlineFailure;
        private PluginOwnedOperation? _cleanup;
        internal PluginRuntimeManager Manager { get; } = new();

        internal TaskCompletionSource Gate()
        {
            var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool release;
            lock (_gate) { Assert.IsFalse(_sealed); _gates.Add(value); release = _draining; }
            if (release) value.TrySetResult();
            return value;
        }

        internal ManualResetEventSlim BlockingGate()
        {
            var value = new ManualResetEventSlim();
            bool release;
            lock (_gate) { Assert.IsFalse(_sealed); _blockingGates.Add(value); release = _draining; }
            if (release) value.Set();
            return value;
        }

        internal ActivePluginInstance Add(Func<PluginAgentEventContext, Task> callback, bool projectScoped = false)
        {
            var acquisition = new Acquisition();
            lock (_gate) { Assert.IsFalse(_sealed); _acquisitions.Add(acquisition); }
            // Retain partial construction too. Constructors are inert; no StartAsync, real activator, roots or upstream callbacks.
            var logger = LogManager.GetLogger(nameof(RuntimePluginAgentEventObserverTests));
            var services = new NoopPluginServices(logger);
            acquisition.Plugin = new InertPlugin(callback, () => _observation.Value);
            acquisition.Lifetime = new PluginActivationLifetime(default);
            acquisition.Tasks = new PluginRuntimeTaskService(acquisition.Lifetime.Token);
            var descriptor = new PluginDescriptor { RuntimeKey = "inert-" + Guid.NewGuid().ToString("N"), TypeName = nameof(InertPlugin), AssemblyName = "inert" };
            var context = new PluginRuntimeContext
            {
                Plugin = descriptor, Services = services, Logger = logger, PackageDirectory = "",
                Host = new PluginHostInfo { ApplicationName = "inert", Version = "0", HostApiVersion = "0", UserDataDirectory = "" },
                LifetimeCancellationToken = acquisition.Lifetime.Token,
                Scope = projectScoped ? PluginScope.Project : PluginScope.Global,
                ScopeProjectId = projectScoped ? "project" : null,
                ScopeProjectPath = projectScoped ? "project-path" : null,
            };
            acquisition.Active = new ActivePluginInstance(acquisition.Plugin, descriptor, null, null, context, [], Manager.Registry, acquisition.Tasks, acquisition.Lifetime);
            Manager.OwnActivation(acquisition.Active);
            acquisition.ManagerOwned = true;
            bool drain;
            lock (_gate) drain = _draining;
            if (drain) _ = Start(acquisition.Active.QuiesceAgentEventsAsync, control: true);
            return acquisition.Active;
        }

        internal Operation Start(Func<Task> body, bool control = false)
        {
            var operation = Prepare(body, control);
            operation.Original.Launch();
            return operation;
        }

        private Operation Prepare(Func<Task> body, bool control = false)
        {
            Operation operation;
            lock (_gate)
            {
                Assert.IsFalse(_sealed);
                operation = new Operation(new PluginOwnedOperation(body), control);
                _operations.Add(operation);
            }
            return operation;
        }

        internal Call Observe(RuntimePluginAgentEventObserver observer, RuntimePluginAgentEventEnvelope envelope)
            => new(this, observer, envelope);

        internal async Task<Exception?> Outcome(Operation operation)
        {
            try { return await operation.Original.Outcome.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException failure) { RecordDeadline(failure); throw; }
        }

        private void RecordDeadline(TimeoutException failure)
        {
            lock (_gate) { _deadlineFailed = true; _deadlineFailure ??= failure; }
        }

        internal async Task Join(Operation operation)
        {
            var failure = await Outcome(operation).ConfigureAwait(false);
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        internal void Accept(Operation operation, Exception failure)
        {
            lock (_gate)
            {
                Assert.IsFalse(operation.Control);
                Assert.IsTrue(operation.Original.Outcome.IsCompletedSuccessfully);
                Assert.AreSame(failure, operation.Original.Outcome.Result);
                operation.Expected = failure;
            }
        }

        internal void AcceptCallback(ActivePluginInstance active, Operation original, Exception failure)
        {
            Assert.IsTrue(original.Original.Outcome.IsCompletedSuccessfully);
            var plugin = (InertPlugin)active.Instance!;
            var record = plugin.Failures().Single(item => ReferenceEquals(item.Original, original) && ReferenceEquals(item.Failure, failure));
            record.Accepted = true;
        }

        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            var original = f.Start(() => body(f));
            Exception? primary;
            try { primary = await f.Outcome(original).ConfigureAwait(false); }
            catch (Exception failure) { primary = failure; }
            if (primary is TimeoutException primaryDeadline) f.RecordDeadline(primaryDeadline);
            f._cleanup = new PluginOwnedOperation(() => f.CleanupAsync(original, primary));
            f._cleanup.Launch();
            Exception? cleanup;
            try { cleanup = await f._cleanup.Outcome.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception failure)
            {
                cleanup = failure;
            }
            if (cleanup is TimeoutException cleanupDeadline) f.RecordDeadline(cleanupDeadline);
            // A deadline remains a failure even if these retained originals subsequently finish successfully.
            if (primary is not null || cleanup is not null || f._deadlineFailure is not null) throw new FixtureFailure(f, primary, cleanup);
        }

        private void BeginDrain()
        {
            TaskCompletionSource[] gates;
            ManualResetEventSlim[] blocking;
            Acquisition[] acquisitions;
            lock (_gate)
            {
                _draining = true;
                gates = [.. _gates]; blocking = [.. _blockingGates]; acquisitions = [.. _acquisitions];
            }
            foreach (var gate in gates) gate.TrySetResult();
            foreach (var gate in blocking) gate.Set();
            _ = Start(Manager.QuiesceAgentEventsAsync, control: true);
            foreach (var acquisition in acquisitions) StartControls(acquisition);
        }

        private void StartControls(Acquisition acquisition)
        {
            if (acquisition.Active is { } active) _ = Start(active.QuiesceAgentEventsAsync, control: true);
            else
            {
                if (acquisition.Lifetime is { } lifetime) _ = Start(lifetime.CancelAsync, control: true);
                if (acquisition.Tasks is { } tasks) _ = Start(tasks.CloseForReleaseAsync, control: true);
            }
        }

        private async Task CleanupAsync(Operation body, Exception? primary)
        {
            BeginDrain();
            await body.Original.Outcome.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Acquisition[] acquisitions;
            var failures = new List<Exception>();
            var controlFailed = false;
            while (true)
            {
                Operation[] operations;
                lock (_gate) operations = [.. _operations];
                var outcomes = await Task.WhenAll(operations.Select(operation => operation.Original.Outcome))
                    .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Acquisition[] finalControls;
                lock (_gate)
                {
                    if (operations.Length != _operations.Count) continue;
                    acquisitions = [.. _acquisitions];
                    // Every tracked producer has terminated. Cover partial/late acquisition inventory before sealing.
                    finalControls = acquisitions.Where(item => !item.FinalControlsStarted).ToArray();
                    foreach (var acquisition in finalControls) acquisition.FinalControlsStarted = true;
                    if (finalControls.Length == 0)
                    {
                        _sealed = true;
                        for (var i = 0; i < outcomes.Length; i++)
                        {
                            if (outcomes[i] is not { } failure) continue;
                            var operation = operations[i];
                            if (operation.Control)
                            {
                                controlFailed = true;
                                _controlFailures.Add(failure);
                                failures.Add(failure);
                                continue;
                            }
                            if (ReferenceEquals(operation, body) && ReferenceEquals(failure, primary)) continue;
                            if (ReferenceEquals(operation.Expected, failure)) continue;
                            _operationFailures.Add(failure);
                            failures.Add(failure);
                        }
                    }
                }
                if (finalControls.Length == 0) break;
                foreach (var acquisition in finalControls) StartControls(acquisition);
            }
            foreach (var acquisition in acquisitions)
            {
                if (acquisition.Plugin is not { } plugin) continue;
                foreach (var record in plugin.Failures())
                {
                    if (record.Accepted) continue;
                    _callbackFailures.Add(record.Failure);
                    failures.Add(record.Failure);
                }
            }
            lock (_gate)
            {
                if (!controlFailed && !_deadlineFailed)
                {
                    _disposals.Add(new PluginOwnedOperation(() => Manager.DisposeAsync().AsTask()));
                    foreach (var acquisition in acquisitions.Where(item => !item.ManagerOwned))
                        _disposals.Add(new PluginOwnedOperation(async () =>
                        {
                            if (acquisition.Active is { } active) await active.DisposeAsync().ConfigureAwait(false);
                            else
                            {
                                if (acquisition.Plugin is { } plugin) await plugin.DisposeAsync().ConfigureAwait(false);
                                acquisition.Lifetime?.Dispose();
                            }
                        }));
                    // Launch only releases asynchronous gates; no plugin or disposal body runs inline under this lock.
                    foreach (var disposal in _disposals) disposal.Launch();
                }
            }
            if (_disposals.Count != 0)
            {
                var outcomes = await Task.WhenAll(_disposals.Select(disposal => disposal.Outcome))
                    .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                failures.AddRange(outcomes.OfType<Exception>());
                lock (_gate)
                    if (failures.Count == 0 && !_deadlineFailed)
                        foreach (var gate in _blockingGates) gate.Dispose();
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        internal sealed class Operation(PluginOwnedOperation original, bool control)
        {
            internal PluginOwnedOperation Original { get; } = original;
            internal bool Control { get; } = control;
            internal Exception? Expected { get; set; }
        }

        internal sealed class Call
        {
            internal Call(Fixture fixture, RuntimePluginAgentEventObserver observer, RuntimePluginAgentEventEnvelope envelope)
            {
                Operation = fixture.Prepare(async () =>
                {
                    var previous = fixture._observation.Value;
                    fixture._observation.Value = Operation;
                    try { Diagnostics = await observer.ObserveAsync(envelope).ConfigureAwait(false); }
                    finally { fixture._observation.Value = previous; }
                });
                Operation.Original.Launch();
            }
            internal Operation Operation { get; }
            internal IReadOnlyList<PluginRuntimeDiagnostic>? Diagnostics { get; private set; }
        }

        private sealed class Acquisition
        {
            internal InertPlugin? Plugin;
            internal PluginActivationLifetime? Lifetime;
            internal PluginRuntimeTaskService? Tasks;
            internal ActivePluginInstance? Active;
            internal bool ManagerOwned;
            internal bool FinalControlsStarted;
        }

        private sealed class FixtureFailure(Fixture fixture, Exception? primary, Exception? cleanup)
            : AggregateException("Inert observer fixture failed; originals, outcomes and dependencies retained.", new[] { primary, cleanup, fixture._deadlineFailure }.OfType<Exception>())
        {
            internal Fixture Fixture { get; } = fixture;
            internal Exception? Primary { get; } = primary;
            internal Exception? Cleanup { get; } = cleanup;
            internal TimeoutException? Deadline => Fixture._deadlineFailure;
            internal IReadOnlyList<Exception> ControlFailures => Fixture._controlFailures;
            internal IReadOnlyList<Exception> OperationFailures => Fixture._operationFailures;
            internal IReadOnlyList<Exception> CallbackFailures => Fixture._callbackFailures;
        }
    }

    private sealed class InertPlugin(Func<PluginAgentEventContext, Task> callback, Func<Fixture.Operation?> observation) : PluginBase
    {
        private readonly object _gate = new();
        private readonly List<CallbackFailure> _failures = [];
        internal CallbackFailure[] Failures() { lock (_gate) return [.. _failures]; }
        /// <inheritdoc />
        public override async ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
        {
            // The integrated adapter owns its original/observer before this synchronous callback prefix can enter.
            try { await callback(context).ConfigureAwait(false); }
            catch (Exception failure)
            {
                lock (_gate) _failures.Add(new CallbackFailure(observation(), context, failure));
                throw;
            }
        }

        internal sealed class CallbackFailure(Fixture.Operation? original, PluginAgentEventContext context, Exception failure)
        {
            internal Fixture.Operation? Original { get; } = original;
            internal PluginAgentEventContext Context { get; } = context;
            internal Exception Failure { get; } = failure;
            internal bool Accepted { get; set; }
        }
    }
}
