namespace CodeAlta.Orchestration.Runtime;

// Runtime-specific ownership, not a provider scheduler or another hosting lifetime.
// The gate protects records only. All executable work waits on a retained asynchronous launch.
internal sealed class OwnedProviderEventForwarding
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (Task Work, Task Observer)> _active = [];
    private readonly List<(long Ordinal, int Stage, Exception Error)> _failures = [];
    private readonly HashSet<Attachment> _attachments = [];
    private TaskCompletionSource _changed = NewCompletion();
    private Task? _close;
    private long _ordinal;
    private bool _closed;

    internal bool IsClosed { get { lock (_gate) return _closed; } }
    internal int ActiveWorkCount { get { lock (_gate) return _active.Count; } }

    internal Task<T> RunAsync<T>(Func<Task<T>> body, bool external = true, bool reportFailure = false)
    {
        var launch = NewCompletion();
        Task<T> work;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(external && _closed, this);
            var ordinal = ++_ordinal;
            work = ExecuteAsync(ordinal, launch.Task, body, reportFailure);
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));
        }
        launch.TrySetResult();
        return work;
    }

    internal Task RunAsync(Func<Task> body, bool external = true, bool reportFailure = false)
        => RunAsync(async () => { await body().ConfigureAwait(false); return true; }, external, reportFailure);

    private async Task<T> ExecuteAsync<T>(long ordinal, Task launch, Func<Task<T>> body, bool reportFailure, AttachmentIdentity? identity = null)
    {
        await launch.ConfigureAwait(false);
        try { return await body().ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (reportFailure) RecordFailure(ordinal, 0, ex, identity);
            throw;
        }
    }

    private async Task ObserveOwnedAsync(long ordinal, Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* ExecuteAsync records reportable failures; external callers receive their result. */ }
        finally
        {
            lock (_gate)
            {
                _active.Remove(ordinal);
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
            retire = _closed;
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
            var use = new Use(attachment, projection: true);
            var ordinal = ++_ordinal;
            work = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                try { await body(use).ConfigureAwait(false); return true; }
                finally { use.Dispose(); }
            }, reportFailure: true, identity: attachment.Identity);
            // Observe independently of the Action callback's discarded return value.
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));
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
            retirement = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                await RetireCoreAsync(attachment).ConfigureAwait(false);
                return true;
            }, reportFailure: false);
            attachment.Retirement = retirement;
            _active.Add(ordinal, (retirement, ObserveOwnedAsync(ordinal, retirement)));
        }
        launch.TrySetResult();
        return retirement;
    }

    private async Task RetireCoreAsync(Attachment attachment)
    {
        // Neither cancellation callbacks nor abort are awaited before the other is initiated.
        var cancelStage = new RetirementStage(attachment.Cancellation.CancelAsync);
        var abortStage = new RetirementStage(attachment.Abort);
        var cancellation = cancelStage.Work;
        var abort = abortStage.Work;
        attachment.Controls = [cancellation, abort];
        cancelStage.Launch();
        abortStage.Launch();
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
        attachment.Unsubscription = unsubscribe;
        unsubscribeStage.Launch();
        await CaptureAsync(attachment.Ordinal, 3, unsubscribe, attachment.Identity).ConfigureAwait(false);
        await WaitUsesAsync(attachment, projection: true).ConfigureAwait(false);
        if (!unsubscribe.IsCompletedSuccessfully)
        {
            // An attempted closure is not a receipt of successful unsubscription. Keep the
            // provider handle, cancellation source, attachment, and runtime dependencies alive.
            foreach (var stage in new[] { cancelStage, abortStage, unsubscribeStage })
                await stage.Observer.ConfigureAwait(false);
            throw new AggregateException(new[] { cancellation, abort, unsubscribe }
                .Where(task => !task.IsCompletedSuccessfully)
                .Select(task => task.Exception?.GetBaseException() ?? new TaskCanceledException(task)));
        }
        var stopStage = new RetirementStage(attachment.Stop);
        var stop = stopStage.Work;
        attachment.StopWork = stop;
        stopStage.Launch();
        await CaptureAsync(attachment.Ordinal, 4, stop, attachment.Identity).ConfigureAwait(false);
        foreach (var stage in new[] { cancelStage, abortStage, unsubscribeStage, stopStage })
            await stage.Observer.ConfigureAwait(false);
        lock (_gate)
        {
            attachment.Stopped = stop.IsCompletedSuccessfully;
            // A failed unsubscribe is not declared successful. Keep its receipt/delegates/failure.
            if (attachment.Stopped && unsubscribe.IsCompletedSuccessfully) _attachments.Remove(attachment);
            Pulse();
        }
        if (attachment.Stopped) attachment.Cancellation.Dispose();
        var failed = new[] { cancellation, abort, unsubscribe, stop }.Where(task => !task.IsCompletedSuccessfully).ToArray();
        if (failed.Length != 0)
            throw new AggregateException(failed.Select(task => task.Exception?.GetBaseException() ?? new TaskCanceledException(task)));
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
        Exception[]? retainedFailures;
        lock (_gate)
        {
            retainedFailures = _attachments.Count == 0 ? null : _failures
                .OrderBy(item => item.Ordinal).ThenBy(item => item.Stage).Select(item => item.Error).ToArray();
        }
        if (retainedFailures is not null)
        {
            // Fault with retained dependencies, rather than manufacturing an infinite wait or
            // treating failed closure/stop as permission to dispose the actors and event stream.
            var failure = new AggregateException("Attachment retirement did not confirm dependency release.", retainedFailures);
            failure.Data["RetainedForwardingOwner"] = this;
            throw failure;
        }
        var actorStage = new RetirementStage(actors);
        actorStage.Launch();
        await CaptureAsync(long.MaxValue, 0, actorStage.Work).ConfigureAwait(false);
        await actorStage.Observer.ConfigureAwait(false);
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
        lock (_gate) _failures.Add((ordinal, stage, reported));
    }

    internal sealed record AttachmentIdentity(string SessionId, string HandleId);

    internal sealed class AttachmentFailureException(AttachmentIdentity identity, long ordinal, int stage, Exception error)
        : Exception($"Provider forwarding failed for session '{identity.SessionId}', attachment '{identity.HandleId}', admission {ordinal}, stage {stage}: {error.Message}", error)
    {
        internal AttachmentIdentity Identity { get; } = identity;
        internal long Ordinal { get; } = ordinal;
        internal int Stage { get; } = stage;
    }

    private sealed class RetirementStage
    {
        private readonly TaskCompletionSource _launch = NewCompletion();
        internal RetirementStage(Func<Task> body)
        {
            Work = InvokeAsync(body);
            Observer = ObserveAsync(Work);
        }
        internal Task Work { get; }
        internal Task Observer { get; }
        internal void Launch() => _launch.TrySetResult();
        private async Task InvokeAsync(Func<Task> body)
        {
            await _launch.Task.ConfigureAwait(false);
            await body().ConfigureAwait(false);
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
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Setup { get; } = NewCompletion();
        internal Task[] Controls { get; set; } = [];
        internal IDisposable? Subscription { get; private set; }
        internal Task? Unsubscription { get; set; }
        internal Task? StopWork { get; set; }
        internal Task? Retirement { get; set; }
        internal bool HandleAdmission { get; set; } = true;
        internal bool CallbackAdmission { get; set; } = true;
        internal int HandleUses { get; set; }
        internal int ProjectionUses { get; set; }
        internal bool Stopped { get; set; }
        internal bool IsRetiring { get { lock (_owner._gate) return !HandleAdmission; } }
        internal Use? TryAcquireHandleUse()
        {
            lock (_owner._gate)
            {
                if (_owner._closed || !HandleAdmission) return null;
                HandleUses++;
                return new Use(this, projection: false);
            }
        }
        internal void InstallSubscription(IDisposable subscription)
        { lock (_owner._gate) Subscription = subscription; }
        internal void CompleteSetup() => Setup.TrySetResult();
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
        public void Dispose()
        { if (Interlocked.Exchange(ref _released, 1) == 0) attachment.Release(projection); }
    }
}
