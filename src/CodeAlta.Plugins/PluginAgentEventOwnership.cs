using System.Runtime.ExceptionServices;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>The result of finite, nonwaiting admission of one plugin event callback.</summary>
public enum PluginAgentEventAdmission
{
    /// <summary>The original callback and adapter tail were admitted.</summary>
    Admitted,
    /// <summary>All 64 activation-owned slots are occupied; no callback was queued.</summary>
    Capacity,
    /// <summary>Admission or the activation lifetime is closed; no callback was invoked.</summary>
    Closing,
}

/// <summary>A bounded snapshot; rejection counts saturate rather than retaining per-rejection diagnostics.</summary>
/// <param name="Outstanding">Callbacks and adapter tails not yet joined.</param>
/// <param name="CapacityRejected">Saturating count of capacity rejections.</param>
/// <param name="ClosingRejected">Saturating count of closing rejections.</param>
/// <param name="Closed">Whether event admission is closed.</param>
public sealed record PluginAgentEventAdmissionSnapshot(int Outstanding, long CapacityRejected, long ClosingRejected, bool Closed);

// A retained original and its outcome observer exist before the asynchronous launch is released.
// This is plugin ownership plumbing, not a work queue or a replacement for the plugin task service.
internal sealed class PluginOwnedOperation
{
    private readonly TaskCompletionSource _launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal PluginOwnedOperation(Func<Task> body, bool captureContext = false)
    {
        Work = ExecuteAsync(body, captureContext);
        Outcome = ObserveAsync(Work);
    }
    internal Task Work { get; }
    internal Task<Exception?> Outcome { get; }
    internal void Launch() => _launch.TrySetResult();
    private async Task ExecuteAsync(Func<Task> body, bool captureContext)
    {
        await _launch.Task.ConfigureAwait(captureContext);
        await body().ConfigureAwait(false);
    }
    private static async Task<Exception?> ObserveAsync(Task original)
    { try { await original.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }
    internal static async Task JoinAsync(IEnumerable<PluginOwnedOperation> operations)
    {
        var outcomes = await Task.WhenAll(operations.Select(operation => operation.Outcome)).ConfigureAwait(false);
        var failures = outcomes.OfType<Exception>().ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Length > 1) throw new AggregateException(failures);
    }
}

// Upstream cancellation only launches the same retained original that close joins. Calling CTS.CancelAsync
// again is not a join of an already-running cancellation callback batch (including linked CTS propagation).
internal sealed class PluginActivationLifetime : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly PluginOwnedOperation _cancellation;
    private readonly CancellationTokenRegistration _upstream;

    internal PluginActivationLifetime(CancellationToken upstream)
    {
        // No plugin can have registered a callback yet; preserve already-cancelled admission synchronously.
        if (upstream.IsCancellationRequested) _source.Cancel();
        _cancellation = new PluginOwnedOperation(_source.CancelAsync);
        _upstream = upstream.UnsafeRegister(static state => ((PluginOwnedOperation)state!).Launch(), _cancellation);
    }

    internal CancellationToken Token => _source.Token;
    internal Task CancelAsync() { _cancellation.Launch(); return _cancellation.Work; }
    public void Dispose() { _upstream.Dispose(); _source.Dispose(); }
}

public sealed partial class ActivePluginInstance
{
    private readonly object _eventGate = new();
    private readonly HashSet<PluginOwnedOperation> _eventAttempts = [];
    private readonly AsyncLocal<PluginOwnedOperation?> _eventScope = new AsyncLocal<PluginOwnedOperation?>();
    private TaskCompletionSource _eventChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _capacityRejected;
    private long _closingRejected;
    private bool _eventClosed;
    private PluginOwnedOperation? _eventQuiescence;
    private PluginOwnedOperation[] _eventControls = [];
    private PluginOwnedOperation? _deactivation;
    private readonly List<PluginRuntimeDiagnostic> _deactivationDiagnostics = [];
    internal IReadOnlyList<PluginRuntimeDiagnostic> DeactivationDiagnostics => _deactivationDiagnostics;

    /// <summary>Gets bounded event admission statistics for this activation, including stale-snapshot rejection.</summary>
    public PluginAgentEventAdmissionSnapshot AgentEventAdmission
    {
        get { lock (_eventGate) return new(_eventAttempts.Count, _capacityRejected, _closingRejected, _eventClosed); }
    }

    /// <summary>Rejects a close that would await this execution context's still-outstanding event attempt.</summary>
    /// <exception cref="InvalidOperationException">The caller would join its own original event callback.</exception>
    public void ThrowIfAgentEventSelfJoin()
    {
        var current = _eventScope.Value;
        lock (_eventGate)
            if (current is not null && _eventAttempts.Contains(current))
                throw new InvalidOperationException("Plugin event callback cannot await its own dependency release.");
    }

    internal async ValueTask<PluginAgentEventAdmission> ObserveOwnedAgentEventAsync(Func<Task> body)
    {
        PluginOwnedOperation? operation = null;
        lock (_eventGate)
        {
            if (_eventClosed || _instance is null || !RuntimeContext.IsValid)
            {
                _eventClosed = true;
                if (_closingRejected < long.MaxValue) _closingRejected++;
                return PluginAgentEventAdmission.Closing;
            }
            if (_eventAttempts.Count == 64)
            {
                if (_capacityRejected < long.MaxValue) _capacityRejected++;
                return PluginAgentEventAdmission.Capacity;
            }
            operation = new PluginOwnedOperation(async () =>
            {
                var previous = _eventScope.Value;
                _eventScope.Value = operation;
                try { await body().ConfigureAwait(false); }
                finally { _eventScope.Value = previous; }
            }, captureContext: true);
            _eventAttempts.Add(operation);
        }
        operation.Launch();
        try { await operation.Work.ConfigureAwait(false); }
        finally
        {
            // The original callback AND adapter handling are terminal before capacity/lifetime release.
            await operation.Outcome.ConfigureAwait(false);
            lock (_eventGate)
            {
                _eventAttempts.Remove(operation);
                var changed = _eventChanged;
                _eventChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
        }
        return PluginAgentEventAdmission.Admitted;
    }

    internal void CloseAgentEventAdmission()
    { lock (_eventGate) _eventClosed = true; }

    /// <summary>Closes event and background-task admission, signals cancellation, and joins originals without releasing the activation.</summary>
    /// <remarks>Cancellation may invalidate contexts immediately; retained does not mean valid. No timeout proves termination.</remarks>
    /// <returns>The one retained quiescence task; failures retain activation dependencies.</returns>
    /// <exception cref="InvalidOperationException">The caller would await its own event attempt.</exception>
    /// <exception cref="Exception">An original cancellation/control operation failed.</exception>
    public Task QuiesceAgentEventsAsync()
    {
        ThrowIfAgentEventSelfJoin();
        PluginOwnedOperation operation;
        lock (_eventGate)
        {
            _eventClosed = true;
            operation = _eventQuiescence ??= new PluginOwnedOperation(QuiesceAgentEventsCoreAsync);
        }
        operation.Launch();
        return operation.Work;
    }

    private async Task QuiesceAgentEventsCoreAsync()
    {
        // Retain every independent control before launching any; never await one signal before starting the other.
        _eventControls = [new(_lifetime.CancelAsync), new(_taskService.CloseForReleaseAsync), new(WaitEventAttemptsAsync)];
        foreach (var control in _eventControls) control.Launch();
        await PluginOwnedOperation.JoinAsync(_eventControls).ConfigureAwait(false);
    }

    private async Task WaitEventAttemptsAsync()
    {
        while (true)
        {
            Task changed;
            lock (_eventGate)
            {
                if (_eventAttempts.Count == 0) return;
                changed = _eventChanged.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    internal Task DeactivateOriginalAsync()
    {
        ThrowIfAgentEventSelfJoin();
        PluginOwnedOperation operation;
        lock (_eventGate)
        {
            _eventClosed = true;
            operation = _deactivation ??= new PluginOwnedOperation(DeactivateOriginalCoreAsync);
        }
        operation.Launch();
        return operation.Work;
    }

    private async Task DeactivateOriginalCoreAsync()
    {
        State = PluginRuntimeState.Deactivating;
        _contributionRegistry.RemoveByPlugin(Descriptor.RuntimeKey);
        Contributions = [];
        await QuiesceAgentEventsAsync().ConfigureAwait(false);
        // A fault leaves the instance, context, CTS and load context retained; no finally-release.
        await DeactivateAndReleaseInstanceAsync().ConfigureAwait(false);
        VerifyUnload(_deactivationDiagnostics);
        _lifetime.Dispose();
    }

    // Separate plugin-bearing locals from the frame that verifies collectibility, including
    // Tier-0 code where synchronous plugin hooks can leave their arguments live on the stack.
    private async ValueTask DeactivateAndReleaseInstanceAsync()
    {
        await DeactivatePluginInstanceAsync(_instance, CancellationToken.None).ConfigureAwait(false);
        RuntimeContext.Invalidate();
        State = PluginRuntimeState.Deactivated;
        _instance = null;
    }

    private async ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> WaitDeactivationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Validate a wait argument before starting the owner. Infinite waits remain supported.
        if ((timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var original = DeactivateOriginalAsync();
        try { await original.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) when (!original.IsCompleted)
        {
            return [PluginRuntimeDiagnostic.Warning(PluginRuntimeDiagnosticSource.Unload,
                "Plugin deactivation wait timed out; the original and dependencies remain retained.", SourcePackage?.PackageId, SourcePackage?.PackageDirectory)];
        }
        return _deactivationDiagnostics;
    }
}

public sealed partial class PluginRuntimeManager
{
    private readonly AsyncLocal<PluginOwnedOperation?> _startScope = new AsyncLocal<PluginOwnedOperation?>();
    private PluginOwnedOperation? _ownedStart;
    private PluginOwnedOperation? _eventDrain;
    private PluginOwnedOperation? _ownedDeactivation;
    private bool _eventAdmissionClosed;
    private readonly List<PluginOwnedOperation> _lateEventControls = [];
    private PluginEventDependencyException? _retainedDependencyFailure;

    internal Task RunOwnedStartAsync(Func<Task> body)
    {
        PluginOwnedOperation? operation = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownedStart is not null || _eventAdmissionClosed)
                throw new InvalidOperationException("A plugin manager permits one actual startup and no startup after quiescence.");
            operation = new PluginOwnedOperation(async () =>
            {
                var previous = _startScope.Value;
                _startScope.Value = operation;
                try { await body().ConfigureAwait(false); }
                finally { _startScope.Value = previous; }
            }, captureContext: true);
            _ownedStart = operation;
        }
        operation.Launch();
        return operation.Work;
    }

    internal void OwnActivation(ActivePluginInstance active)
    {
        PluginOwnedOperation? control = null;
        lock (_lock)
        {
            // Metadata locks only, in manager -> activation order. No activation is lost to a stale close snapshot.
            if (_eventAdmissionClosed)
            {
                active.CloseAgentEventAdmission();
                control = new PluginOwnedOperation(active.QuiesceAgentEventsAsync);
                _lateEventControls.Add(control);
            }
            _activePlugins.Add(active);
        }
        control?.Launch();
    }

    /// <summary>Rejects self-dependent event/startup shutdown before admission or memoized close state changes.</summary>
    /// <exception cref="InvalidOperationException">An admitted event callback or startup would await itself.</exception>
    public void ThrowIfAgentEventSelfJoin()
    {
        ActivePluginInstance[] active;
        lock (_lock)
        {
            if (_startScope.Value is { } start && ReferenceEquals(start, _ownedStart) && !start.Work.IsCompleted)
                throw new InvalidOperationException("Plugin startup cannot await its own dependency release.");
            active = [.. _activePlugins];
        }
        foreach (var plugin in active) plugin.ThrowIfAgentEventSelfJoin();
    }

    /// <summary>Closes manager-wide event/start admission and joins admitted startup, events and task controls before borrowed-service release.</summary>
    /// <remarks>Includes all activations of a prestarted/borrowed manager; this is not per-borrower isolation or ownership transfer.
    /// Does not invoke plugin deactivation/disposal. A failed or pending original prohibits dependent release.</remarks>
    /// <returns>The single retained quiescence task.</returns>
    /// <exception cref="InvalidOperationException">The caller would await its own startup or event operation.</exception>
    /// <exception cref="Exception">An original startup or quiescence operation failed.</exception>
    public Task QuiesceAgentEventsAsync()
    {
        ThrowIfAgentEventSelfJoin();
        PluginOwnedOperation operation;
        lock (_lock)
        {
            _eventAdmissionClosed = true;
            foreach (var plugin in _activePlugins) plugin.CloseAgentEventAdmission();
            operation = _eventDrain ??= new PluginOwnedOperation(QuiesceManagerCoreAsync);
        }
        operation.Launch();
        return operation.Work;
    }

    private async Task QuiesceManagerCoreAsync()
    {
        PluginOwnedOperation? startup;
        ActivePluginInstance[] early;
        lock (_lock) { startup = _ownedStart; early = [.. _activePlugins]; }
        // Start existing controls before joining startup, which can itself be awaiting a plugin task.
        var earlyControls = early.Select(plugin => new PluginOwnedOperation(plugin.QuiesceAgentEventsAsync)).ToArray();
        foreach (var control in earlyControls) control.Launch();
        var startupFailure = startup is null ? null : await startup.Outcome.ConfigureAwait(false);
        ActivePluginInstance[] all;
        PluginOwnedOperation[] lateControls;
        lock (_lock) { all = [.. _activePlugins]; lateControls = [.. _lateEventControls]; }
        var controls = all.Select(plugin => new PluginOwnedOperation(plugin.QuiesceAgentEventsAsync)).ToArray();
        foreach (var control in controls) control.Launch();
        // Startup's terminal outcome bounds late acquisitions. Join every wrapper and its observer too,
        // not just the shared activation original which can finish before these wrappers unwind.
        var outcomes = await Task.WhenAll(earlyControls.Concat(lateControls).Concat(controls).Select(control => control.Outcome)).ConfigureAwait(false);
        var failures = outcomes.OfType<Exception>().ToList();
        if (startupFailure is not null) failures.Insert(0, startupFailure);
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private Task DeactivateManagerOriginalAsync()
    {
        ThrowIfAgentEventSelfJoin();
        PluginOwnedOperation operation;
        lock (_lock)
        {
            _eventAdmissionClosed = true;
            foreach (var plugin in _activePlugins) plugin.CloseAgentEventAdmission();
            operation = _ownedDeactivation ??= new PluginOwnedOperation(async () =>
            {
                await QuiesceAgentEventsAsync().ConfigureAwait(false);
                ActivePluginInstance[] active;
                lock (_lock) active = [.. _activePlugins];
                foreach (var plugin in active.Reverse())
                {
                    await plugin.DeactivateOriginalAsync().ConfigureAwait(false);
                    _diagnostics.AddRange(plugin.DeactivationDiagnostics);
                }
                lock (_lock) _activePlugins.Clear();
            });
        }
        operation.Launch();
        return operation.Work;
    }

    internal void RetainDependencyFailure(PluginEventDependencyException failure)
    { lock (_lock) _retainedDependencyFailure ??= failure; }
}

/// <summary>Signals that failed creation cannot release dependencies because plugin quiescence failed.</summary>
/// <remarks>Creation and barrier failures remain distinct direct exceptions. Outer rollback must retain rather than release.</remarks>
public sealed class PluginEventDependencyException : AggregateException
{
    /// <summary>Creates a retained failed-prerequisite result.</summary>
    /// <param name="creationFailure">The original creation failure.</param>
    /// <param name="barrierFailure">The original barrier failure.</param>
    /// <param name="dependencies">Acquired dependencies which must not be disposed.</param>
    public PluginEventDependencyException(Exception creationFailure, Exception barrierFailure, object dependencies)
        : base("Plugin event dependency barrier failed; acquired services are retained.", creationFailure, barrierFailure)
    { CreationFailure = creationFailure; BarrierFailure = barrierFailure; Dependencies = dependencies; }
    /// <summary>Gets the original creation failure.</summary>
    public Exception CreationFailure { get; }
    /// <summary>Gets the original barrier/control failure.</summary>
    public Exception BarrierFailure { get; }
    /// <summary>Gets retained inner-owner dependencies.</summary>
    public object Dependencies { get; }
    /// <summary>Gets retained outer-owner dependencies, when the inner host was never returned.</summary>
    public object? OuterDependencies { get; private set; }
    internal void RetainOuter(object dependencies) => OuterDependencies ??= dependencies;
}

/// <summary>Mandatory pre-release barriers for plugin-borrowed Host and frontend dependencies.</summary>
public static class PluginEventDependencyBarrier
{
    /// <summary>Wraps existing cleanup without changing its post-barrier order or borrowed-plugin disposal policy.</summary>
    /// <remarks>Owners must enter through <see cref="EnterDispose"/> so self-join is checked before lazy state access.</remarks>
    /// <param name="runtime">Associated application-wide manager, or null when there are no plugin services.</param>
    /// <param name="release">Existing retained cleanup operation.</param>
    /// <returns>A single retained drain-then-release operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="release"/> is null.</exception>
    public static Lazy<Task> Wrap(PluginRuntimeManager? runtime, Lazy<Task> release)
        => CreateDisposal(() => runtime?.QuiesceAgentEventsAsync() ?? Task.CompletedTask, release);

    /// <summary>Creates the exact drain-before-release pipeline; injected stages allow root-free verification.</summary>
    /// <param name="drain">Mandatory prerequisite; failure prohibits release.</param>
    /// <param name="release">Existing cleanup, not evaluated until successful drainage.</param>
    /// <returns>A single original operation, including its independent outcome observer.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static Lazy<Task> CreateDisposal(Func<Task> drain, Lazy<Task> release)
    {
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentNullException.ThrowIfNull(release);
        return new Lazy<Task>(() =>
        {
            // Capture the disposing frontend's context, not the earlier construction context.
            var operation = new PluginOwnedOperation(async () =>
            {
                await drain();
                await release.Value;
            }, captureContext: true);
            operation.Launch();
            return operation.Work;
        });
    }

    /// <summary>Rejects self-join before touching memoized owner state.</summary>
    /// <param name="runtime">The associated manager, including a borrowed manager.</param>
    /// <param name="disposal">The retained owner operation.</param>
    /// <returns>The existing owner's completion.</returns>
    /// <exception cref="InvalidOperationException">The caller would await itself.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="disposal"/> is null.</exception>
    public static ValueTask EnterDispose(PluginRuntimeManager? runtime, Lazy<Task> disposal)
    {
        ArgumentNullException.ThrowIfNull(disposal);
        runtime?.ThrowIfAgentEventSelfJoin();
        return new(disposal.Value);
    }

    /// <summary>Gates rollback and propagates retained inner-host failure before outer metadata/logging can be released.</summary>
    /// <param name="runtime">Acquired manager, if any.</param>
    /// <param name="creationFailure">Original creation failure, possibly an inner retained-barrier failure.</param>
    /// <param name="dependencies">Strong references to acquisitions requiring the barrier.</param>
    /// <returns>Successful admission to the existing rollback pipeline.</returns>
    /// <exception cref="PluginEventDependencyException">The barrier failed; dependencies remain retained.</exception>
    public static async Task BeforeRollbackAsync(PluginRuntimeManager? runtime, Exception creationFailure, object dependencies)
    {
        if (creationFailure is PluginEventDependencyException inherited)
        {
            inherited.RetainOuter(dependencies);
            ExceptionDispatchInfo.Throw(inherited);
        }
        try { if (runtime is not null) await runtime.QuiesceAgentEventsAsync().ConfigureAwait(false); }
        catch (Exception barrierFailure)
        {
            var retained = new PluginEventDependencyException(creationFailure, barrierFailure, dependencies);
            runtime?.RetainDependencyFailure(retained);
            throw retained;
        }
    }
}
