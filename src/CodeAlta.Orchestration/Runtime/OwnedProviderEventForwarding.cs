using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Plugins;

namespace CodeAlta.Orchestration.Runtime;

// Runtime-specific ownership, not a provider scheduler or another hosting lifetime.
// The gate protects records only. All executable work waits on a retained asynchronous launch.
internal sealed class OwnedProviderEventForwarding
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (Task Work, Task Observer, WorkReceipt Receipt)> _active = [];
    private readonly List<(Task Work, Task Observer, WorkReceipt Receipt)> _retainedWork = [];
    private readonly List<object> _retainedDependencies = [];
    private readonly List<(long Ordinal, int Stage, Exception Error)> _failures = [];
    private readonly HashSet<Attachment> _attachments = [];
    private TaskCompletionSource _changed = NewCompletion();
    private Task? _close;
    private long _ordinal;
    private bool _closed;

    internal bool IsClosed { get { lock (_gate) return _closed || _retainedDependencies.Count != 0; } }
    internal int ActiveWorkCount { get { lock (_gate) return _active.Count; } }
    internal int RetainedWorkCount { get { lock (_gate) return _retainedWork.Count; } }

    internal static bool HasRetention(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is AgentDependencyRetentionException or PluginEventDependencyException) return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    internal void RetainDependencies(Exception failure, object dependencies)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!HasRetention(failure)) throw new ArgumentException("Explicit retention evidence is required.", nameof(failure));
        lock (_gate)
        {
            _retainedDependencies.Add(dependencies);
            _failures.Add((++_ordinal, 0, failure));
            foreach (var attachment in _attachments) attachment.HandleAdmission = false;
            Pulse();
        }
    }

    internal Task<T> RunAsync<T>(Func<Task<T>> body, bool external = true, bool reportFailure = false)
        => RunWithReceiptAsync(_ => body(), external, reportFailure);

    private Task<T> RunWithReceiptAsync<T>(Func<WorkReceipt, Task<T>> body, bool external, bool reportFailure)
    {
        var launch = NewCompletion();
        Task<T> work;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(external && (_closed || _retainedDependencies.Count != 0), this);
            var ordinal = ++_ordinal;
            var receipt = new WorkReceipt();
            work = ExecuteAsync(ordinal, launch.Task, () => body(receipt), reportFailure, receipt);
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work), receipt));
        }
        launch.TrySetResult();
        return work;
    }

    internal Task RunAsync(Func<Task> body, bool external = true, bool reportFailure = false)
        => RunWithReceiptAsync(async receipt =>
        {
            receipt.CallbackOriginal = body() ?? throw new InvalidOperationException("A forwarding body returned no original.");
            await receipt.CallbackOriginal.ConfigureAwait(false);
            return true;
        }, external, reportFailure);

    private async Task<T> ExecuteAsync<T>(long ordinal, Task launch, Func<Task<T>> body, bool reportFailure,
        WorkReceipt receipt, AttachmentIdentity? identity = null)
    {
        await launch.ConfigureAwait(false);
        try
        {
            var original = body() ?? throw new InvalidOperationException("A forwarding invocation returned no original.");
            receipt.Original = original;
            return await original.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            receipt.AwaitedFailure = ex;
            receipt.OriginalFaults = receipt.Original?.Exception;
            receipt.CallbackFaults = receipt.CallbackOriginal?.Exception;
            receipt.Failure = receipt.CallbackFaults is { InnerExceptions.Count: > 1 } ? receipt.CallbackFaults
                : receipt.OriginalFaults is { InnerExceptions.Count: > 1 } ? receipt.OriginalFaults : ex;
            if (reportFailure || HasRetention(receipt.Failure)) RecordFailure(ordinal, 0, receipt.Failure, identity);
            if (!ReferenceEquals(receipt.Failure, ex)) ExceptionDispatchInfo.Throw(receipt.Failure);
            throw;
        }
    }

    private async Task ObserveOwnedAsync(long ordinal, Task work, Attachment? callbackAttachment = null)
    {
        Exception? failure = null;
        try { await work.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_gate)
            {
                if (_active.Remove(ordinal, out var invocation) && failure is not null && HasRetention(failure))
                    _retainedWork.Add(invocation);
                if (callbackAttachment is not null) callbackAttachment.ActiveCallbacks--;
                Pulse();
            }
        }
    }

    internal Attachment RegisterAttachment(string sessionId, string handleId, Func<Task> abort, Func<Task> stop)
    {
        Attachment attachment;
        bool retire;
        lock (_gate)
        {
            attachment = new Attachment(this, ++_ordinal, new AttachmentIdentity(sessionId, handleId), abort, stop);
            _attachments.Add(attachment);
            retire = _closed || _retainedDependencies.Count != 0;
        }
        // A late acquisition is owned before control can run or metadata can await.
        if (retire) _ = RetireAsync(attachment);
        return attachment;
    }

    internal Task Forward(Attachment attachment, Func<Use, Task> body)
    {
        var launch = NewCompletion();
        Task work;
        lock (_gate)
        {
            if (!attachment.CallbackAdmission) return Task.CompletedTask;
            attachment.ProjectionUses++;
            attachment.ActiveCallbacks++;
            var use = new Use(attachment, projection: true);
            var ordinal = ++_ordinal;
            var receipt = new WorkReceipt();
            work = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                try
                {
                    receipt.CallbackOriginal = body(use) ?? throw new InvalidOperationException("A forwarding callback returned no original.");
                    await receipt.CallbackOriginal.ConfigureAwait(false);
                    return true;
                }
                catch (Exception failure)
                {
                    receipt.CallbackAwaitedFailure = failure;
                    receipt.CallbackFaults = receipt.CallbackOriginal?.Exception;
                    var evidence = receipt.CallbackFaults is { InnerExceptions.Count: > 1 } ? receipt.CallbackFaults : failure;
                    if (HasRetention(evidence)) use.Retain(evidence);
                    ExceptionDispatchInfo.Throw(evidence);
                    throw;
                }
                finally { use.Dispose(); }
            }, reportFailure: true, receipt, identity: attachment.Identity);
            // Observe independently of the Action callback's discarded return value.
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work, attachment), receipt));
        }
        launch.TrySetResult();
        return work;
    }

    internal Task RetireAsync(Attachment attachment)
    {
        var launch = NewCompletion();
        Task retirement;
        lock (_gate)
        {
            if (attachment.Retirement is not null) return attachment.Retirement;
            attachment.HandleAdmission = false;
            var ordinal = ++_ordinal;
            var receipt = new WorkReceipt();
            retirement = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                receipt.CallbackOriginal = RetireCoreAsync(attachment);
                await receipt.CallbackOriginal.ConfigureAwait(false);
                return true;
            }, reportFailure: false, receipt);
            attachment.Retirement = retirement;
            _active.Add(ordinal, (retirement, ObserveOwnedAsync(ordinal, retirement), receipt));
        }
        launch.TrySetResult();
        return retirement;
    }

    private async Task RetireCoreAsync(Attachment attachment)
    {
        // Neither cancellation callbacks nor abort are awaited before the other is initiated.
        var cancelStage = new RetirementStage(attachment.Cancellation.CancelAsync);
        var abortStage = new RetirementStage(attachment.Abort);
        var permissionStage = new RetirementStage(attachment.CloseOwnedPermissions ?? (() => Task.CompletedTask));
        attachment.Stages = [cancelStage, abortStage, permissionStage];
        var cancellation = cancelStage.Work;
        var abort = abortStage.Work;
        attachment.Controls = [cancellation, abort];
        cancelStage.Launch();
        abortStage.Launch();
        permissionStage.Launch();
        // A permission waiting on a provider-supplied None token must not hold a handle use forever.
        // Cancellation and abort above still start independently, before this owner-only join.
        await CaptureAsync(attachment.Ordinal, 0, permissionStage.Work, attachment.Identity).ConfigureAwait(false);
        await attachment.Setup.Task.ConfigureAwait(false);
        await WaitUsesAsync(attachment, projection: false).ConfigureAwait(false);
        await CaptureAsync(attachment.Ordinal, 1, cancellation, attachment.Identity).ConfigureAwait(false);
        await CaptureAsync(attachment.Ordinal, 2, abort, attachment.Identity).ConfigureAwait(false);

        IDisposable? subscription;
        lock (_gate)
        {
            attachment.CallbackAdmission = false;
            subscription = attachment.Subscription;
        }
        // Admission closure precedes actual IDisposable.Dispose, which may invoke callbacks/throw.
        var unsubscribeStage = new RetirementStage(() => { subscription?.Dispose(); return Task.CompletedTask; });
        var unsubscribe = unsubscribeStage.Work;
        attachment.Stages = [cancelStage, abortStage, permissionStage, unsubscribeStage];
        attachment.Unsubscription = unsubscribe;
        unsubscribeStage.Launch();
        await CaptureAsync(attachment.Ordinal, 3, unsubscribe, attachment.Identity).ConfigureAwait(false);
        await WaitUsesAsync(attachment, projection: true).ConfigureAwait(false);
        // A callback tail can replace this attachment (or drain a queued prompt) after releasing
        // its projection use. Joining that tail here would join our own retirement. The runtime
        // close still joins every complete callback through _active before releasing dependencies.
        foreach (var stage in attachment.Stages) await stage.Observer.ConfigureAwait(false);
        if (!unsubscribeStage.Succeeded || attachment.RetainedUses.Count != 0 || HasRetainedDependencies())
        {
            throw RetainedFailure("attachment prerequisites", attachment);
        }
        var stopStage = new RetirementStage(attachment.Stop);
        attachment.Stages = [cancelStage, abortStage, permissionStage, unsubscribeStage, stopStage];
        var stop = stopStage.Work;
        attachment.StopWork = stop;
        stopStage.Launch();
        await CaptureAsync(attachment.Ordinal, 4, stop, attachment.Identity).ConfigureAwait(false);
        foreach (var stage in attachment.Stages)
            await stage.Observer.ConfigureAwait(false);
        if (!stopStage.Succeeded || HasRetainedDependencies())
            throw RetainedFailure("attachment stop", attachment);
        try { attachment.Cancellation.Dispose(); }
        catch (Exception failure)
        {
            RecordFailure(attachment.Ordinal, 5, failure, attachment.Identity);
            throw RetainedFailure("attachment source disposal", attachment);
        }
        lock (_gate)
        {
            attachment.Stopped = true;
            _attachments.Remove(attachment);
            Pulse();
        }
        var failures = attachment.Stages.Where(stage => stage.Failure is not null).Select(stage => stage.Failure!).ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Length > 1) throw new AggregateException(failures);
    }

    private bool HasRetainedDependencies() { lock (_gate) return _retainedDependencies.Count != 0; }

    private AgentDependencyRetentionException RetainedFailure(string stage, object dependencies)
    {
        lock (_gate)
        {
            var failures = _failures.OrderBy(item => item.Ordinal).ThenBy(item => item.Stage).Select(item => item.Error).ToArray();
            // A failed required receipt is itself evidence, even when no callback returned an original.
            if (failures.Length == 0) failures = [new InvalidOperationException("A required forwarding release receipt is missing.")];
            return new AgentDependencyRetentionException("provider forwarding", stage, failures, new { Owner = this, Dependencies = dependencies });
        }
    }

    private async Task WaitUsesAsync(Attachment attachment, bool projection)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if ((projection ? attachment.ProjectionUses : attachment.HandleUses) == 0) return;
                changed = _changed.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    internal Task CloseAsync(Func<Task> permissions, Func<Task> actors, Action completeEvents)
    {
        var launch = NewCompletion();
        Task close;
        lock (_gate)
        {
            if (_close is not null) return _close;
            _closed = true;
            foreach (var attachment in _attachments) attachment.HandleAdmission = false;
            close = CloseCoreAsync(launch.Task, permissions, actors, completeEvents);
            _close = close;
        }
        launch.TrySetResult();
        return close;
    }

    private async Task CloseCoreAsync(Task launch, Func<Task> permissions, Func<Task> actors, Action completeEvents)
    {
        await launch.ConfigureAwait(false);
        var permissionStage = new RetirementStage(permissions);
        var permissionWork = permissionStage.Work;
        permissionStage.Launch();
        Attachment[] attachments;
        lock (_gate) attachments = [.. _attachments];
        foreach (var attachment in attachments) _ = RetireAsync(attachment);
        // Late RegisterAttachment initiates its own retirement. Setup/body/tail work stays admitted
        // until actual completion; a single entry snapshot is never used as a drainage proof.
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_active.Count == 0) break;
                changed = _changed.Task;
            }
            await changed.ConfigureAwait(false);
        }
        await CaptureAsync(0, 0, permissionWork).ConfigureAwait(false);
        await permissionStage.Observer.ConfigureAwait(false);
        if (permissionStage.Original is null)
            throw RetainedFailure("permission cleanup", permissionStage);
        Exception[]? retainedFailures;
        lock (_gate)
        {
            retainedFailures = _attachments.Count == 0 && _retainedDependencies.Count == 0 ? null : _failures
                .OrderBy(item => item.Ordinal).ThenBy(item => item.Stage).Select(item => item.Error).ToArray();
        }
        if (retainedFailures is not null)
        {
            // Fault with retained dependencies, rather than manufacturing an infinite wait or
            // treating failed closure/stop as permission to dispose the actors and event stream.
            throw RetainedFailure("runtime drainage", this);
        }
        var actorStage = new RetirementStage(actors);
        actorStage.Launch();
        await CaptureAsync(long.MaxValue, 0, actorStage.Work).ConfigureAwait(false);
        await actorStage.Observer.ConfigureAwait(false);
        // A terminal ordinary returned-task error confirms unwind; a missing original or any
        // explicit marker in the captured original graph does not authorize stream release.
        if (actorStage.Original is null || HasRetainedDependencies())
            throw RetainedFailure("actor cleanup", actorStage);
        try { completeEvents(); }
        catch (Exception ex) { RecordFailure(long.MaxValue, 1, ex); }
        Exception[] failures;
        lock (_gate) failures = _failures.OrderBy(item => item.Ordinal).ThenBy(item => item.Stage).Select(item => item.Error).ToArray();
        if (failures.Length != 0) throw new AggregateException(failures);
    }

    private async Task CaptureAsync(long ordinal, int stage, Task task, AttachmentIdentity? identity = null)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { RecordFailure(ordinal, stage, ex, identity); }
    }

    private void RecordFailure(long ordinal, int stage, Exception error, AttachmentIdentity? identity = null)
    {
        var reported = identity is null ? error : new AttachmentFailureException(identity, ordinal, stage, error);
        lock (_gate)
        {
            _failures.Add((ordinal, stage, reported));
            if (HasRetention(error))
            {
                _retainedDependencies.Add(error);
                foreach (var attachment in _attachments) attachment.HandleAdmission = false;
            }
        }
    }

    internal sealed record AttachmentIdentity(string SessionId, string HandleId);

    private sealed class WorkReceipt
    {
        internal Task? Original { get; set; }
        internal Task? CallbackOriginal { get; set; }
        internal AggregateException? OriginalFaults { get; set; }
        internal AggregateException? CallbackFaults { get; set; }
        internal Exception? CallbackAwaitedFailure { get; set; }
        internal Exception? AwaitedFailure { get; set; }
        internal Exception? Failure { get; set; }
    }

    internal sealed class AttachmentFailureException(AttachmentIdentity identity, long ordinal, int stage, Exception error)
        : Exception($"Provider forwarding failed for session '{identity.SessionId}', attachment '{identity.HandleId}', admission {ordinal}, stage {stage}: {error.Message}", error)
    {
        internal AttachmentIdentity Identity { get; } = identity;
        internal long Ordinal { get; } = ordinal;
        internal int Stage { get; } = stage;
    }

    internal sealed class RetirementStage
    {
        private readonly TaskCompletionSource _launch = NewCompletion();
        internal RetirementStage(Func<Task> body)
        {
            Work = InvokeAsync(body);
            Observer = ObserveAsync(Work);
        }
        internal Task Work { get; }
        internal Task Observer { get; }
        internal Task? Original { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal Exception? AwaitedFailure { get; private set; }
        internal Exception? Failure { get; private set; }
        internal bool Succeeded { get; private set; }
        internal void Launch() => _launch.TrySetResult();
        private async Task InvokeAsync(Func<Task> body)
        {
            await _launch.Task.ConfigureAwait(false);
            try
            {
                Original = body() ?? throw new InvalidOperationException("A forwarding control returned no original.");
                await Original.ConfigureAwait(false);
                Succeeded = true;
            }
            catch (Exception failure)
            {
                AwaitedFailure = failure;
                OriginalFaults = Original?.Exception;
                Failure = OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
                ExceptionDispatchInfo.Throw(Failure);
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch { /* The owning operation records the error. */ } }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var changed = _changed; _changed = NewCompletion(); changed.TrySetResult(); }

    internal sealed class Attachment
    {
        private readonly OwnedProviderEventForwarding _owner;
        internal Attachment(OwnedProviderEventForwarding owner, long ordinal, AttachmentIdentity identity, Func<Task> abort, Func<Task> stop)
        { _owner = owner; Ordinal = ordinal; Identity = identity; Abort = abort; Stop = stop; }
        internal long Ordinal { get; }
        internal AttachmentIdentity Identity { get; }
        internal Func<Task> Abort { get; set; }
        internal Func<Task> Stop { get; }
        internal Func<Task>? CloseOwnedPermissions { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Setup { get; } = NewCompletion();
        internal Task[] Controls { get; set; } = [];
        internal RetirementStage[] Stages { get; set; } = [];
        internal List<(Use Use, Exception Failure)> RetainedUses { get; } = [];
        internal IDisposable? Subscription { get; private set; }
        internal Task? Unsubscription { get; set; }
        internal Task? StopWork { get; set; }
        internal Task? Retirement { get; set; }
        internal bool HandleAdmission { get; set; } = true;
        internal bool CallbackAdmission { get; set; } = true;
        internal int HandleUses { get; set; }
        internal int ProjectionUses { get; set; }
        internal int ActiveCallbacks { get; set; }
        internal bool Stopped { get; set; }
        internal bool IsRetiring { get { lock (_owner._gate) return !HandleAdmission; } }
        internal Use? TryAcquireHandleUse()
        {
            lock (_owner._gate)
            {
                if (_owner._closed || _owner._retainedDependencies.Count != 0 || !HandleAdmission) return null;
                HandleUses++;
                return new Use(this, projection: false);
            }
        }
        internal void InstallSubscription(IDisposable subscription)
        { lock (_owner._gate) Subscription = subscription; }
        internal void CompleteSetup() => Setup.TrySetResult();
        internal void Retain(Use use, bool projection, Exception failure)
        {
            lock (_owner._gate)
            {
                RetainedUses.Add((use, failure));
                _owner._retainedDependencies.Add(use);
                _owner._failures.Add((Ordinal, 0, failure));
                HandleAdmission = false;
                Release(projection);
            }
        }
        internal void Release(bool projection)
        {
            lock (_owner._gate)
            {
                if (projection) ProjectionUses--; else HandleUses--;
                _owner.Pulse();
            }
        }
    }

    internal sealed class Use(Attachment attachment, bool projection) : IDisposable
    {
        private int _released;
        internal Attachment Attachment => attachment;
        internal bool IsReleased => Volatile.Read(ref _released) == 1;
        internal void Retain(Exception failure)
        {
            if (!HasRetention(failure)) throw new ArgumentException("Explicit retention evidence is required.", nameof(failure));
            if (Interlocked.CompareExchange(ref _released, 2, 0) != 0) return;
            attachment.Retain(this, projection, failure);
        }
        public void Dispose()
        { if (Interlocked.CompareExchange(ref _released, 1, 0) == 0) attachment.Release(projection); }
    }
}
