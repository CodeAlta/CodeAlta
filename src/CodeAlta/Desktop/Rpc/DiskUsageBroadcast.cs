namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The records of one scan of the disk usage and those who follow it. The worker of the scan is the one writer of what it
/// produced; a listener that joins takes a copy of it under the same lock, so that it misses nothing and receives nothing twice.
/// </summary>
/// <remarks>
/// <para>
/// What a listener holds is bounded: the records of the projects and the end, which the number of projects
/// bounds, and the latest progress only, which replaces the one it has not read. Nothing comes after the end (a
/// <c>done</c> or <c>failed</c> record), also for a listener that joins after it.
/// </para>
/// <para>
/// Whether a scan is kept and whether it is stopped is one decision, taken under the lock: once the last listener left or
/// the service stopped it, <see cref="TryCommit"/> refuses; once it committed, the last listener that leaves no longer
/// stops it.
/// </para>
/// </remarks>
internal sealed class DiskUsageBroadcast
{
    private readonly Lock _gate = new();
    private readonly List<DiskUsageScanEvent> _produced = [];
    private readonly List<Listener> _listeners = [];
    private DiskUsageScanEvent? _progress;
    private bool _ended;
    private bool _committed;
    private bool _finished;

    /// <summary>Gets the cancellation of the scan: canceled when it is stopped before it committed.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Gets or sets the worker of the scan.</summary>
    public Task? Worker { get; set; }

    /// <summary>Gets whether nobody can join the scan any more: it ended, or it is stopping.</summary>
    public bool Ended
    {
        get { lock (_gate) return _ended; }
    }

    /// <summary>A new listener with what was produced so far; null when the scan ended or is stopping.</summary>
    public Listener? TryJoin()
    {
        lock (_gate)
        {
            if (_ended) return null;
            var listener = new Listener(_gate);
            foreach (var produced in _produced) listener.Give(produced);
            if (_progress is not null) listener.Give(_progress);
            _listeners.Add(listener);
            return listener;
        }
    }

    /// <summary>Removes a listener; the last one that goes stops a scan that did not commit.</summary>
    public void Leave(Listener listener)
    {
        bool stop;
        lock (_gate)
        {
            if (!_listeners.Remove(listener)) return;
            listener.Complete();
            stop = _listeners.Count == 0 && !_ended && !_committed;
            if (stop) _ended = true;
        }

        if (stop) Cancel();
    }

    /// <summary>Stops the scan whatever follows it: every listener completes, and a scan that did not commit is canceled.</summary>
    public void Stop()
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_committed;
            _ended = true;
            foreach (var listener in _listeners) listener.Complete();
            _listeners.Clear();
        }

        if (cancel) Cancel();
    }

    /// <summary>Decides that the scan is kept; false when it was stopped, and is then never kept.</summary>
    public bool TryCommit()
    {
        lock (_gate)
        {
            if (_ended) return false;
            _committed = true;
            return true;
        }
    }

    /// <summary>Gives a record to every listener: a progress replaces the one not read yet, and nothing is given after the end.</summary>
    public void Publish(DiskUsageScanEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (_ended || _finished) return;
            if (update.Kind == "progress") _progress = update;
            else
            {
                _produced.Add(update);
                if (update.Kind is "done" or "failed")
                {
                    _finished = true;
                    _progress = null;
                }
            }

            foreach (var listener in _listeners) listener.Give(update);
        }
    }

    /// <summary>Ends the scan: every listener completes after what it was given.</summary>
    public void End()
    {
        lock (_gate)
        {
            _ended = true;
            foreach (var listener in _listeners) listener.Complete();
            _listeners.Clear();
        }
    }

    private void Cancel()
    {
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The worker ended meanwhile. */ }
    }

    /// <summary>One who follows the scan: the records it has not read, and the latest progress.</summary>
    internal sealed class Listener(Lock gate)
    {
        private readonly Queue<DiskUsageScanEvent> _records = new();
        private DiskUsageScanEvent? _progress;
        private TaskCompletionSource? _waiting;
        private bool _finished;
        private bool _completed;

        /// <summary>Gets how many records wait to be read, the progress included.</summary>
        public int Buffered
        {
            get { lock (gate) return _records.Count + (_progress is null ? 0 : 1); }
        }

        // Under the lock of the scan.
        internal void Give(DiskUsageScanEvent update)
        {
            if (_finished) return;
            if (update.Kind == "progress") _progress = update;
            else
            {
                _records.Enqueue(update);
                if (update.Kind is "done" or "failed")
                {
                    _finished = true;
                    _progress = null;
                }
            }

            Wake();
        }

        // Under the lock of the scan.
        internal void Complete()
        {
            _completed = true;
            Wake();
        }

        private void Wake()
        {
            var waiting = _waiting;
            _waiting = null;
            waiting?.TrySetResult();
        }

        /// <summary>Reads the records until the scan ends: the records in their order, the latest progress when none waits.</summary>
        /// <param name="cancellationToken">Stops the reading; the listener still has to leave.</param>
        public async IAsyncEnumerable<DiskUsageScanEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryTake(out var next, out var wait))
                {
                    if (next is null) yield break;
                    yield return next;
                    continue;
                }

                await wait!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The next record, or none at the end (true); or what to wait for (false).
        private bool TryTake(out DiskUsageScanEvent? next, out Task? wait)
        {
            lock (gate)
            {
                wait = null;
                if (_records.TryDequeue(out next)) return true;
                if (_progress is not null)
                {
                    (next, _progress) = (_progress, null);
                    return true;
                }

                if (_completed) return true;
                _waiting ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiting.Task;
                return false;
            }
        }
    }
}
