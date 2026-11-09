namespace CodeAlta.ModelServers;

/// <summary>
/// The slots of the models of the servers of a machine, as the requests of inference take them (<c>doc/fleet/model-servers.md</c>):
/// a request beyond the slots of its model waits here, first in first out, told its place, instead of in <c>llama-server</c>
/// where nobody sees it. The sessions of this machine and the requests other machines relay through it take the same
/// gate, so this machine holds the whole queue of a model, in order. Owned by the <see cref="ModelHost"/>.
/// </summary>
public sealed class ModelSlotGates
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Server, string Model), ModelSlotGate> _gates = new();
    private readonly Action? _changed;

    /// <summary>Initializes a new instance of the <see cref="ModelSlotGates"/> class.</summary>
    public ModelSlotGates()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSlotGates"/> class.</summary>
    /// <param name="changed">Told, on any thread, when a request starts or stops waiting for a slot.</param>
    public ModelSlotGates(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _changed = changed;
    }

    /// <summary>Gets the gate of a model of a server, made at its first request; its limit is the one given now.</summary>
    /// <param name="server">The server.</param>
    /// <param name="model">The model, as the requests name it; empty for the one model of a server.</param>
    /// <param name="slots">How many requests it serves at once; null or 0 when it is not known, which waits for nothing.</param>
    /// <returns>The gate.</returns>
    /// <exception cref="ArgumentException"><paramref name="server"/> is empty.</exception>
    public ModelSlotGate For(string server, string model, int? slots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ModelSlotGate? gate;
        lock (_gate)
        {
            if (!_gates.TryGetValue((server, model ?? string.Empty), out gate))
            {
                gate = new ModelSlotGate(_changed);
                _gates[(server, model ?? string.Empty)] = gate;
            }
        }

        gate.SetLimit(slots is > 0 ? slots : null);
        return gate;
    }

    /// <summary>Gets how many requests wait for a slot of a model of a server.</summary>
    /// <param name="server">The server.</param>
    /// <param name="model">The model; empty for the one model of a server.</param>
    /// <returns>How many wait; 0 for a model no request asked.</returns>
    public int Waiting(string server, string model)
    {
        lock (_gate) return _gates.TryGetValue((server, model ?? string.Empty), out var gate) ? gate.WaitingCount : 0;
    }
}

/// <summary>The slots of one model of a server: at most its slots of requests at once, the others waiting in turn.</summary>
/// <remarks>Safe to use from any thread. Each signal is swapped under the lock and completed after it.</remarks>
public sealed class ModelSlotGate
{
    private readonly Action? _changed;
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int? _limit;
    private int _active;

    /// <summary>Initializes a new instance of the <see cref="ModelSlotGate"/> class, with no limit until one is set.</summary>
    public ModelSlotGate()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ModelSlotGate"/> class.</summary>
    /// <param name="changed">Told when a request starts or stops waiting; null for nothing.</param>
    public ModelSlotGate(Action? changed) => _changed = changed;

    /// <summary>Gets how many requests hold a slot.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>Gets how many requests wait for a slot.</summary>
    public int WaitingCount
    {
        get { lock (_gate) return _waiters.Count; }
    }

    /// <summary>Changes the limit; the requests that wait take the slots it frees, in order.</summary>
    /// <param name="limit">How many requests at once; null for no limit.</param>
    public void SetLimit(int? limit)
    {
        List<TaskCompletionSource> signals;
        lock (_gate)
        {
            if (_limit == limit) return;
            _limit = limit;
            signals = AdmitLocked();
        }

        Raise(signals);
    }

    /// <summary>Takes a slot, waiting for one in turn, and says the place of the request while it waits.</summary>
    /// <param name="onPlaceChanged">Called with how many requests are before this one when it starts to wait and each time that changes; null says nothing.</param>
    /// <param name="cancellationToken">Cancels the wait: the request leaves the queue without a slot.</param>
    /// <returns>The slot, given back once disposed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was given.</exception>
    public async ValueTask<IDisposable> AcquireAsync(Func<int, CancellationToken, ValueTask>? onPlaceChanged, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter();
        lock (_gate)
        {
            if (_waiters.Count == 0 && (_limit is null || _active < _limit))
            {
                _active++;
                return new Lease(this);
            }

            waiter.Node = _waiters.AddLast(waiter);
        }

        Told();
        try
        {
            using var registration = cancellationToken.Register(() => Leave(waiter));
            return await WaitAsync(waiter, onPlaceChanged, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled, or its callback failed: it leaves the queue, or gives back the slot it was given meanwhile.
            Abandon(waiter);
            throw;
        }
    }

    private async ValueTask<IDisposable> WaitAsync(Waiter waiter, Func<int, CancellationToken, ValueTask>? onPlaceChanged, CancellationToken cancellationToken)
    {
        var reported = -1;
        while (true)
        {
            int before;
            Task changed;
            lock (_gate)
            {
                if (waiter.Node is null) break;
                before = Before(waiter);
                changed = waiter.Changed.Task;
            }

            if (before != reported && onPlaceChanged is not null)
            {
                try
                {
                    await onPlaceChanged(before, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Saying the place is best effort: the request keeps its place.
                }
            }

            reported = before;
            await changed.ConfigureAwait(false);
        }

        // Admitted, or cancelled: then the request left the queue without a slot.
        await waiter.Admitted.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    // Under the gate: how many wait before a waiter.
    private int Before(Waiter waiter)
    {
        var before = 0;
        for (var node = _waiters.First; node is not null && !ReferenceEquals(node.Value, waiter); node = node.Next) before++;
        return before;
    }

    private void Leave(Waiter waiter)
    {
        List<TaskCompletionSource> signals;
        lock (_gate)
        {
            if (waiter.Node is null) return;
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Gone = true;
            signals = [waiter.SwapChanged(), .. _waiters.Select(static other => other.SwapChanged())];
        }

        waiter.Admitted.TrySetCanceled();
        Raise(signals);
    }

    // Decided under one lock: a waiter still queued leaves; one admitted meanwhile gives its slot back, once.
    private void Abandon(Waiter waiter)
    {
        bool release;
        lock (_gate)
        {
            release = waiter.Node is null && !waiter.Gone;
            waiter.Gone = true;
        }

        if (release) Release();
        else Leave(waiter);
    }

    private void Release()
    {
        List<TaskCompletionSource> signals;
        lock (_gate)
        {
            _active = Math.Max(0, _active - 1);
            signals = AdmitLocked();
        }

        Raise(signals);
    }

    // Under the gate: the waiters the free slots take now, in order, and the others told their new place.
    private List<TaskCompletionSource> AdmitLocked()
    {
        var signals = new List<TaskCompletionSource>();
        while (_waiters.First is { } first && (_limit is null || _active < _limit))
        {
            _waiters.RemoveFirst();
            first.Value.Node = null;
            _active++;
            signals.Add(first.Value.Admitted);
            signals.Add(first.Value.SwapChanged());
        }

        if (signals.Count > 0) signals.AddRange(_waiters.Select(static waiter => waiter.SwapChanged()));
        return signals;
    }

    private void Raise(List<TaskCompletionSource> signals)
    {
        foreach (var signal in signals) signal.TrySetResult();
        if (signals.Count > 0) Told();
    }

    private void Told()
    {
        try { _changed?.Invoke(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A listener that fails changes nothing of the slots.
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completed when the place of the waiter changes, then replaced: swapped under the lock of the gate only.
        public TaskCompletionSource Changed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its place in the queue; null once admitted or gone. Under the lock of the gate.
        public LinkedListNode<Waiter>? Node { get; set; }

        // Whether it left without a slot, or gave back the one it was given. Under the lock of the gate.
        public bool Gone { get; set; }

        public TaskCompletionSource SwapChanged()
        {
            var changed = Changed;
            Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return changed;
        }
    }

    private sealed class Lease(ModelSlotGate owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}
