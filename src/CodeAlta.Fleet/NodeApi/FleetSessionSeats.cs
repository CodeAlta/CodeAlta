using XenoAtom.Logging;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>A run that asks a seat of the machine before it starts.</summary>
/// <param name="Id">The run: the retry key of a send, a queued prompt, the run of an automation. A cancel or a start now names it.</param>
/// <param name="RootId">
/// The session at the root of the tree of the run: the sessions of one tree share one seat, so that a parent never waits for a
/// second seat while its child works. For the run of an automation whose session does not exist yet, the run itself.
/// </param>
/// <param name="SessionId">The session the run is for; null for the run of an automation that has none yet.</param>
/// <param name="Title">What the run is, for the people who see the queue.</param>
/// <param name="Kind"><c>send</c>, <c>queue</c>, <c>agent</c> or <c>automation</c>.</param>
public sealed record FleetSeatRequest(string Id, string RootId, string? SessionId, string? Title, string Kind);

/// <summary>A run that waits for a seat.</summary>
/// <param name="Id">The run.</param>
/// <param name="SessionId">Its session; null for the run of an automation that has none yet.</param>
/// <param name="Title">What it is.</param>
/// <param name="Kind">What asked it (<see cref="FleetSeatRequest.Kind"/>).</param>
/// <param name="Position">Its place in the queue: 1 for the next one.</param>
/// <param name="QueuedAt">When it started to wait.</param>
public sealed record FleetSeatQueueItem(string Id, string? SessionId, string? Title, string Kind, int Position, DateTimeOffset QueuedAt);

/// <summary>The seats of a machine at one time.</summary>
/// <param name="Limit">How many sessions work at once; null for no limit.</param>
/// <param name="Working">How many seats are taken: the trees of sessions at work, and the runs admitted that did not start yet.</param>
/// <param name="Queue">The runs that wait, the next one first.</param>
public sealed record FleetSeatsSnapshot(int? Limit, int Working, IReadOnlyList<FleetSeatQueueItem> Queue);

/// <summary>A seat asked: taken at once, or a place in the queue until it is.</summary>
public sealed class FleetSeatTicket
{
    internal FleetSeatTicket(string id, Task admitted, int position)
    {
        Id = id;
        Admitted = admitted;
        Position = position;
    }

    /// <summary>Gets the run.</summary>
    public string Id { get; }

    /// <summary>
    /// Gets the task that completes once the run has its seat; canceled when the run is cancelled in the queue, or when the
    /// seats are disposed while it waits.
    /// </summary>
    public Task Admitted { get; }

    /// <summary>Gets its place in the queue when it was asked: 0 when it had its seat at once.</summary>
    public int Position { get; }

    /// <summary>Gets whether it had its seat at once.</summary>
    public bool IsAdmitted => Admitted.IsCompletedSuccessfully;
}

/// <summary>
/// The seats of a machine (<c>doc/fleet/node.md</c>, limits and good citizenship): at most <c>N</c> trees of sessions work there
/// at once, set by its owner (<see cref="Server.FleetMachineLimits"/>); the work that comes from elsewhere waits, first in first
/// out, for a seat. What takes a seat is a tree of sessions with a session at work that does not wait for a person, the work the
/// person at the machine starts included (it is counted, never queued), and a run admitted that did not start yet.
/// </summary>
/// <remarks>
/// The limit is held by this machine only, in memory: a run that ends frees its seat, and nothing needs to be given back when
/// the process ends. One evaluation at a time decides; while runs wait, it is made again every <see cref="Poll"/> and whenever
/// <see cref="Poke"/> says that something changed, since the end of a run is seen by reading the sessions at work.
/// </remarks>
public sealed class FleetSessionSeats : IAsyncDisposable
{
    private readonly Func<int?> _limit;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>>> _busyRoots;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    // The runs admitted that did not start yet, by run: their tree, and when they were admitted.
    private readonly Dictionary<string, (string Root, DateTimeOffset At)> _reserved = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evaluate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Fleet.Seats");
    private HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private int _working;
    private int? _lastLimit;
    private Task _loop = Task.CompletedTask;
    // Whether the loop runs, under the gate: it ends under the gate once nothing waits, so a waiter added then starts a new one.
    private bool _looping;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="FleetSessionSeats"/> class.</summary>
    /// <param name="limit">Reads how many trees of sessions work at once, null for no limit; read at each decision.</param>
    /// <param name="busyRoots">Reads the roots of the trees of sessions at work now that do not wait for a person.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limit"/> or <paramref name="busyRoots"/> is null.</exception>
    public FleetSessionSeats(Func<int?> limit, Func<CancellationToken, Task<IReadOnlyCollection<string>>> busyRoots, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(busyRoots);
        _limit = limit;
        _busyRoots = busyRoots;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets or sets how often the seats are decided again while runs wait.</summary>
    public TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets how long a run admitted keeps its seat before its tree is seen at work, when nothing gives it back sooner (its
    /// run started, or it was refused): one that never starts gives it back then, rather than hold it forever.
    /// </summary>
    public TimeSpan ReservationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Raised, on any thread, when the queue or the number of seats taken changed.</summary>
    public event Action? Changed;

    /// <summary>Asks a seat for a run: it has it at once when one is free, or when its tree holds one already; else it waits in the queue.</summary>
    /// <param name="request">The run.</param>
    /// <param name="cancellationToken">Cancels the first decision; the wait itself ends with <see cref="Cancel"/>.</param>
    /// <returns>The ticket of the run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The seats are disposed.</exception>
    public async Task<FleetSeatTicket> RequestAsync(FleetSeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            // The same run asked again (a retry): its place, or its seat, as it is.
            if (_queue.FirstOrDefault(item => item.Request.Id == request.Id) is { } known)
                return new FleetSeatTicket(request.Id, known.Admitted.Task, Position(known));
            if (_reserved.ContainsKey(request.Id)) return new FleetSeatTicket(request.Id, Task.CompletedTask, 0);
            waiter = new Waiter(request, _time.GetUtcNow());
            waiter.Node = _queue.AddLast(waiter);
        }

        try
        {
            await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not asked after all: it leaves the queue, or gives back the seat it was given meanwhile.
            if (!Cancel(request.Id)) Release(request.Id);
            throw;
        }

        lock (_gate)
        {
            var position = waiter.Node is null ? 0 : Position(waiter);
            if (position > 0) EnsureLoop();
            return new FleetSeatTicket(request.Id, waiter.Admitted.Task, position);
        }
    }

    /// <summary>Gives a run that waits its seat now, whatever the limit: a person's choice.</summary>
    /// <param name="id">The run.</param>
    /// <returns>Whether it waited.</returns>
    public bool StartNow(string id)
    {
        Waiter? waiter;
        lock (_gate)
        {
            waiter = _queue.FirstOrDefault(item => item.Request.Id == id);
            if (waiter is null) return false;
            Remove(waiter);
            _reserved[id] = (waiter.Request.RootId, _time.GetUtcNow());
            _working = Occupied().Count;
        }

        waiter.Admitted.TrySetResult();
        Raise();
        return true;
    }

    /// <summary>Takes a run out of the queue: it does not start.</summary>
    /// <param name="id">The run.</param>
    /// <returns>Whether it waited.</returns>
    public bool Cancel(string id)
    {
        Waiter? waiter;
        lock (_gate)
        {
            waiter = _queue.FirstOrDefault(item => item.Request.Id == id);
            if (waiter is null) return false;
            Remove(waiter);
        }

        waiter.Admitted.TrySetCanceled();
        Raise();
        Poke();
        return true;
    }

    /// <summary>Gives back the seat of a run admitted that will not start (its send was refused), so that the next one takes it.</summary>
    /// <param name="id">The run.</param>
    public void Release(string id)
    {
        bool released;
        lock (_gate) released = _reserved.Remove(id);
        if (!released) return;
        Raise();
        Poke();
    }

    /// <summary>
    /// Says the tree a run admitted belongs to once its session exists: the run of an automation took its seat under its own id,
    /// and its session holds it from now on.
    /// </summary>
    /// <param name="id">The run.</param>
    /// <param name="rootId">The root of the tree of its session.</param>
    public void Bind(string id, string rootId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        lock (_gate)
        {
            if (_reserved.TryGetValue(id, out var reserved)) _reserved[id] = (rootId, reserved.At);
        }
    }

    /// <summary>Asks for a decision now: a run may have ended, or the limit changed.</summary>
    public void Poke()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* One is asked already. */ }
        catch (ObjectDisposedException) { /* Disposed. */ }
    }

    /// <summary>Reads the seats now, as last decided.</summary>
    /// <returns>The limit, the seats taken and the queue.</returns>
    public FleetSeatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var position = 0;
            return new FleetSeatsSnapshot(_lastLimit, _working,
                [.. _queue.Select(waiter => new FleetSeatQueueItem(waiter.Request.Id, waiter.Request.SessionId, waiter.Request.Title, waiter.Request.Kind, ++position, waiter.QueuedAt))]);
        }
    }

    /// <summary>Decides the seats once: the runs that wait take the seats free, in order. Tests call it directly.</summary>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>A task that completes once decided.</returns>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _evaluate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<Waiter> admitted = [];
        bool changed;
        try
        {
            IReadOnlyCollection<string>? busy = null;
            try
            {
                busy = await _busyRoots(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Not read now: what was read last decides, and the next decision reads again.
                Warn($"The sessions at work could not be read: {exception.Message}");
            }

            int? limit;
            try
            {
                limit = _limit();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A file of limits that does not read keeps the limit it had, rather than let every run start.
                Warn(exception.Message);
                lock (_gate) limit = _lastLimit;
            }

            lock (_gate)
            {
                if (busy is not null) _busy = new HashSet<string>(busy, StringComparer.OrdinalIgnoreCase);
                var now = _time.GetUtcNow();
                // A run admitted holds its seat until its tree is seen at work (then the tree holds it), or until it is too old.
                foreach (var (id, reserved) in _reserved.ToArray())
                {
                    if (_busy.Contains(reserved.Root) || now - reserved.At > ReservationTimeout) _reserved.Remove(id);
                }

                var occupied = Occupied();
                for (var node = _queue.First; node is not null;)
                {
                    var next = node.Next;
                    var waiter = node.Value;
                    // Its tree holds a seat already: it takes no other. Else it takes a free seat, in order: none is free for a later one either.
                    if (limit is null || occupied.Contains(waiter.Request.RootId) || occupied.Count < limit)
                    {
                        Remove(waiter);
                        _reserved[waiter.Request.Id] = (waiter.Request.RootId, now);
                        occupied.Add(waiter.Request.RootId);
                        admitted.Add(waiter);
                    }

                    node = next;
                }

                changed = admitted.Count > 0 || _working != occupied.Count || _lastLimit != limit;
                _working = occupied.Count;
                _lastLimit = limit;
            }
        }
        finally
        {
            _evaluate.Release();
        }

        foreach (var waiter in admitted) waiter.Admitted.TrySetResult();
        if (changed) Raise();
    }

    /// <summary>Stops deciding; the runs that wait are canceled, and keep what their callers kept of them.</summary>
    /// <returns>A task that completes once the loop ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Waiter[] waiting;
        lock (_gate)
        {
            waiting = [.. _queue];
            _queue.Clear();
        }

        foreach (var waiter in waiting) waiter.Admitted.TrySetCanceled();
        _stop.Dispose();
    }

    // Under the gate. The trees that hold a seat: those at work, and those of the runs admitted that did not start yet.
    private HashSet<string> Occupied()
    {
        var occupied = new HashSet<string>(_busy, StringComparer.OrdinalIgnoreCase);
        foreach (var reserved in _reserved.Values) occupied.Add(reserved.Root);
        return occupied;
    }

    // Under the gate.
    private int Position(Waiter waiter)
    {
        var position = 0;
        foreach (var item in _queue)
        {
            position++;
            if (ReferenceEquals(item, waiter)) return position;
        }

        return 0;
    }

    // Under the gate.
    private void Remove(Waiter waiter)
    {
        if (waiter.Node is null) return;
        _queue.Remove(waiter.Node);
        waiter.Node = null;
    }

    // Under the gate. One loop decides while runs wait, and ends once none does.
    private void EnsureLoop()
    {
        if (_looping || Volatile.Read(ref _disposed) != 0) return;
        _looping = true;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(Poll, token).ConfigureAwait(false);
                await EvaluateAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Warn($"The seats of the machine could not be decided: {exception.Message}");
            }

            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _looping = false;
                    return;
                }
            }
        }

        lock (_gate) _looping = false;
    }

    private void Warn(string message)
    {
        if (LogManager.IsInitialized) _logger.Warn(message);
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Warn($"A listener of the seats failed: {exception.Message}");
        }
    }

    private sealed class Waiter(FleetSeatRequest request, DateTimeOffset queuedAt)
    {
        public FleetSeatRequest Request { get; } = request;

        public DateTimeOffset QueuedAt { get; } = queuedAt;

        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its place in the queue; null once admitted or cancelled. Under the gate.
        public LinkedListNode<Waiter>? Node { get; set; }
    }
}
