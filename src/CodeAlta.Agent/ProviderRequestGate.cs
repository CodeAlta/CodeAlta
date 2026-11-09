namespace CodeAlta.Agent;

/// <summary>
/// Bounds how many requests go to one provider at once (<c>max_concurrent_requests</c>). The requests beyond the limit
/// wait in the order they came, each told its place, until a request ends and gives them its slot.
/// </summary>
/// <remarks>
/// <para>
/// A slot is held for one model request with its stream, or for one turn of a provider whose requests CodeAlta does not
/// see (an agent that runs its own loop). The limit can change while requests wait: a higher limit admits the next ones
/// at once, a lower one admits nobody until the requests at work go under it.
/// </para>
/// <para>The gate is safe to use from any thread.</para>
/// </remarks>
public sealed class ProviderRequestGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int? _limit;
    private int _active;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRequestGate"/> class.
    /// </summary>
    /// <param name="name">The name of what the requests go to, as a waiting session names it: the key of the provider.</param>
    /// <param name="limit">The most requests at once, or <see langword="null"/> for no limit.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is zero or negative.</exception>
    public ProviderRequestGate(string name, int? limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfInvalid(limit);
        Name = name;
        _limit = limit;
    }

    /// <summary>Gets the name of what the requests go to: the key of the provider.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets where a request that holds a slot says to its session what it waits for below the provider (a slot of a model
    /// server); null for nowhere.
    /// </summary>
    public Runtime.AgentWaitScope? WaitScope { get; init; }

    /// <summary>Gets the most requests at once, or <see langword="null"/> when there is no limit.</summary>
    public int? Limit
    {
        get
        {
            lock (_sync)
            {
                return _limit;
            }
        }
    }

    /// <summary>Gets how many requests hold a slot.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    /// <summary>Gets how many requests wait for a slot.</summary>
    public int WaitingCount
    {
        get
        {
            lock (_sync)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>
    /// Changes the limit in place. The requests that hold a slot keep it; the ones that wait are admitted, in their
    /// order, as far as the new limit allows.
    /// </summary>
    /// <param name="limit">The most requests at once, or <see langword="null"/> for no limit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is zero or negative.</exception>
    public void SetLimit(int? limit)
    {
        ThrowIfInvalid(limit);
        Signals signals;
        lock (_sync)
        {
            _limit = limit;
            signals = AdmitWaitersLocked();
        }

        signals.Raise();
    }

    /// <summary>
    /// Takes a slot, waiting for one in turn when the limit is reached.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait: the request leaves the queue without a slot.</param>
    /// <returns>The slot, given back when it is disposed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was given.</exception>
    public ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        => AcquireAsync(onPlaceChanged: null, cancellationToken);

    /// <summary>
    /// Takes a slot, waiting for one in turn when the limit is reached, and says the place of the request while it waits.
    /// </summary>
    /// <param name="onPlaceChanged">
    /// Called with the number of requests before this one when it starts to wait and each time that number changes; never
    /// called when a slot is free at once. <see langword="null"/> says nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait: the request leaves the queue without a slot.</param>
    /// <returns>The slot, given back when it is disposed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was given.</exception>
    public async ValueTask<IDisposable> AcquireAsync(
        Func<int, CancellationToken, ValueTask>? onPlaceChanged,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            if (_waiters.Count == 0 && CanAdmitLocked())
            {
                _active++;
                return new Lease(this);
            }

            waiter = new Waiter(this);
            waiter.Node = _waiters.AddLast(waiter);
        }

        try
        {
            using (cancellationToken.UnsafeRegister(
                       static (state, token) => ((Waiter)state!).Gate.Cancel((Waiter)state!, token),
                       waiter))
            {
                var reported = -1;
                while (true)
                {
                    int before;
                    Task changed;
                    lock (_sync)
                    {
                        if (waiter.Node is null)
                        {
                            break;
                        }

                        before = PlaceLocked(waiter);
                        changed = waiter.Changed.Task;
                    }

                    if (onPlaceChanged is not null && before != reported)
                    {
                        await onPlaceChanged(before, cancellationToken).ConfigureAwait(false);
                        reported = before;
                    }

                    await Task.WhenAny(waiter.Admitted.Task, changed).ConfigureAwait(false);
                }

                // Admitted, or cancelled: then this throws.
                await waiter.Admitted.Task.ConfigureAwait(false);
                return new Lease(this);
            }
        }
        catch
        {
            Abandon(waiter);
            throw;
        }
    }

    private static void ThrowIfInvalid(int? limit)
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit of requests at once must be greater than zero.");
        }
    }

    private bool CanAdmitLocked() => _limit is not { } limit || _active < limit;

    private int PlaceLocked(Waiter waiter)
    {
        var before = 0;
        for (var node = _waiters.First; node is not null && !ReferenceEquals(node, waiter.Node); node = node.Next)
        {
            before++;
        }

        return before;
    }

    private void Release()
    {
        Signals signals;
        lock (_sync)
        {
            if (_active <= 0)
            {
                throw new InvalidOperationException("A request slot of the provider was given back more times than it was taken.");
            }

            _active--;
            signals = AdmitWaitersLocked();
        }

        signals.Raise();
    }

    // Hands the free slots to the oldest waiters; the ones that stay are told their place changed.
    private Signals AdmitWaitersLocked()
    {
        List<Waiter>? admitted = null;
        while (_waiters.First is { } first && CanAdmitLocked())
        {
            var waiter = first.Value;
            _waiters.RemoveFirst();
            waiter.Node = null;
            _active++;
            (admitted ??= []).Add(waiter);
        }

        return new Signals(admitted, admitted is null ? null : TakeChangedLocked(_waiters.First));
    }

    // The waiters from this node on moved up: each gets a fresh signal, and the former one is raised outside the lock.
    private static List<TaskCompletionSource>? TakeChangedLocked(LinkedListNode<Waiter>? from)
    {
        List<TaskCompletionSource>? changed = null;
        for (var node = from; node is not null; node = node.Next)
        {
            (changed ??= []).Add(node.Value.Changed);
            node.Value.Changed = NewSignal();
        }

        return changed;
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        List<TaskCompletionSource>? changed;
        lock (_sync)
        {
            if (waiter.Node is not { } node)
            {
                // Admitted: the slot is the waiter's, and its lease gives it back.
                return;
            }

            changed = TakeChangedLocked(node.Next);
            _waiters.Remove(node);
            waiter.Node = null;
            waiter.Cancelled = true;
        }

        waiter.Admitted.TrySetCanceled(cancellationToken);
        new Signals(null, changed).Raise();
    }

    // A waiter that failed while it waited (its callback threw) leaves the queue, or gives back the slot it was given meanwhile.
    private void Abandon(Waiter waiter)
    {
        bool admitted;
        lock (_sync)
        {
            admitted = waiter.Node is null && !waiter.Cancelled;
        }

        if (admitted)
        {
            Release();
        }
        else
        {
            Cancel(waiter, CancellationToken.None);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Signals(List<Waiter>? Admitted, List<TaskCompletionSource>? Changed)
    {
        public void Raise()
        {
            if (Admitted is not null)
            {
                foreach (var waiter in Admitted)
                {
                    waiter.Admitted.TrySetResult();
                }
            }

            if (Changed is not null)
            {
                foreach (var signal in Changed)
                {
                    signal.TrySetResult();
                }
            }
        }
    }

    private sealed class Waiter(ProviderRequestGate gate)
    {
        public ProviderRequestGate Gate { get; } = gate;

        public TaskCompletionSource Admitted { get; } = NewSignal();

        // Raised when the place of the waiter changes; replaced each time, under the lock of the gate.
        public TaskCompletionSource Changed { get; set; } = NewSignal();

        // The place of the waiter in the queue, null once it was admitted or cancelled; under the lock of the gate.
        public LinkedListNode<Waiter>? Node { get; set; }

        public bool Cancelled { get; set; }
    }

    private sealed class Lease(ProviderRequestGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
