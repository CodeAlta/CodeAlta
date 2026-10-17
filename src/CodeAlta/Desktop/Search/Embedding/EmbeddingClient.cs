using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Search.Embedding;

/// <summary>A request that the worker could not answer: it failed, died, or did not answer in time.</summary>
internal sealed class EmbeddingException(string code, string message) : Exception(message)
{
    /// <summary>Gets the reason: <c>load_failed</c>, <c>engine_failed</c>, <c>worker_died</c>, <c>timeout</c> or <c>closed</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The host's side of the embedding workers. A worker starts with the first request that has no free worker and loads
/// the model; up to <see cref="Parallelism"/> workers answer requests at once, each one request at a time, queries before
/// the batches of the indexing. The first worker ends after a while without work, the others soon after, so that their
/// memory is given back once the indexing is done. A dead worker fails the request it had; the next request starts another.
/// </summary>
internal sealed class EmbeddingClient : IAsyncDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<IWorkerProcess> _start;
    private readonly TimeSpan _idle;
    private readonly TimeSpan _extraIdle;
    private readonly Lock _gate = new();
    private readonly LinkedList<Pending> _queries = new();
    private readonly LinkedList<Pending> _documents = new();
    private readonly List<Slot> _slots = [];
    private readonly HashSet<Task> _running = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private EmbeddingWorkerLoad _load;
    private int _parallelism = 1;
    private int _correlation;
    private int _dimensions;
    private bool _paused;

    /// <summary>Creates the client, with one worker at a time until <see cref="SetParallelism"/> asks for more.</summary>
    /// <param name="start">Starts a worker.</param>
    /// <param name="load">What a worker loads, its threads included.</param>
    /// <param name="idle">How long the first worker stays without work; 10 minutes by default.</param>
    /// <param name="extraIdle">How long the other workers stay without work; 30 seconds by default.</param>
    public EmbeddingClient(Func<IWorkerProcess> start, EmbeddingWorkerLoad load, TimeSpan? idle = null, TimeSpan? extraIdle = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _idle = idle ?? TimeSpan.FromMinutes(10);
        _extraIdle = extraIdle ?? TimeSpan.FromSeconds(30);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Gets what a worker loads now.</summary>
    public EmbeddingWorkerLoad Load { get { lock (_gate) return _load; } }

    /// <summary>Gets how many workers may answer at once.</summary>
    public int Parallelism { get { lock (_gate) return _parallelism; } }

    /// <summary>Gets the width of the vectors, once the model was loaded; zero before.</summary>
    public int Dimensions => Volatile.Read(ref _dimensions);

    /// <summary>Gets whether a worker runs now.</summary>
    public bool Running { get { lock (_gate) return _slots.Any(static slot => slot.Worker is not null); } }

    /// <summary>Gets how many workers run now.</summary>
    public int Workers { get { lock (_gate) return _slots.Count(static slot => slot.Worker is not null); } }

    /// <summary>Gets or sets whether the batches of the indexing wait (a dictation runs, or the user paused); queries still run.</summary>
    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate) _paused = value;
            _wake.Release();
        }
    }

    /// <summary>
    /// Gets the threads of each worker for a number of workers at once: about two thirds of the cores shared between
    /// them, at most four each (more threads in one run gain little beside more runs).
    /// </summary>
    /// <param name="parallelism">The workers at once.</param>
    /// <param name="processorCount">The cores.</param>
    /// <returns>The threads of a worker.</returns>
    public static int ThreadsPerWorker(int parallelism, int processorCount)
        => Math.Clamp(Math.Max(1, processorCount * 2 / 3) / Math.Max(1, parallelism), 1, 4);

    /// <summary>
    /// Changes how many workers answer at once, and their threads. It applies during the work: more workers start with
    /// the next requests; a worker over the count ends, and a worker with other threads starts again, once its request is answered.
    /// </summary>
    /// <param name="parallelism">The workers at once, at least one.</param>
    /// <param name="threads">The threads of each.</param>
    public void SetParallelism(int parallelism, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        lock (_gate)
        {
            if (_parallelism == parallelism && _load.Threads == threads) return;
            _parallelism = parallelism;
            _load = _load with { Threads = threads };
        }

        _wake.Release();
    }

    /// <summary>Embeds a query, before every batch of the indexing that waits.</summary>
    /// <exception cref="EmbeddingException">The worker could not answer.</exception>
    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
        => (await EnqueueAsync([text], query: true, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Embeds a batch of documents for the indexing.</summary>
    /// <exception cref="EmbeddingException">The worker could not answer.</exception>
    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => EnqueueAsync(texts, query: false, cancellationToken);

    private Task<float[][]> EnqueueAsync(IReadOnlyList<string> texts, bool query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return Task.FromResult<float[][]>([]);
        var pending = new Pending(texts, cancellationToken);
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) throw new EmbeddingException("closed", "Search by meaning is closed.");
            (query ? _queries : _documents).AddLast(pending);
        }

        // A request given up before its turn leaves the queue.
        pending.Registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _queries.Remove(pending);
                _documents.Remove(pending);
            }

            pending.Completion.TrySetCanceled(cancellationToken);
        });
        _wake.Release();
        return pending.Completion.Task;
    }

    private async Task PumpAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            var ending = new List<IWorkerProcess>();
            var starts = new List<(Slot Slot, Pending Pending)>();
            var wait = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                for (var index = 0; index < _slots.Count; index++)
                {
                    var slot = _slots[index];
                    if (slot.Busy || slot.Worker is not { } worker) continue;
                    // A worker over the count, or loaded with other threads, ends now that it is free; a worker without
                    // work for a while ends too, and its memory is given back.
                    var limit = index == 0 ? _idle : _extraIdle;
                    var idle = TimeSpan.FromMilliseconds(now - slot.FreeSince);
                    if (index >= _parallelism || slot.Threads != _load.Threads || idle >= limit)
                    {
                        ending.Add(worker);
                        slot.Worker = null;
                        continue;
                    }

                    var remaining = limit - idle;
                    if (wait == Timeout.InfiniteTimeSpan || remaining < wait) wait = remaining;
                }

                // Each free slot within the count takes the next request: a query first, a batch unless paused.
                for (var index = 0; index < _parallelism; index++)
                {
                    if (index == _slots.Count) _slots.Add(new Slot());
                    var slot = _slots[index];
                    if (slot.Busy) continue;
                    var next = _queries.First?.Value ?? (_paused ? null : _documents.First?.Value);
                    if (next is null) break;
                    (_queries.Count > 0 ? _queries : _documents).RemoveFirst();
                    slot.Busy = true;
                    starts.Add((slot, next));
                }
            }

            foreach (var worker in ending) Stop(worker, graceful: true);
            foreach (var (slot, pending) in starts)
            {
                var run = RunAsync(slot, pending, token);
                lock (_gate) _running.Add(run);
                _ = run.ContinueWith(task => { lock (_gate) _running.Remove(task); }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            try { await _wake.WaitAsync(wait, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // What is left waits no more.
        List<Pending> left;
        lock (_gate)
        {
            left = [.. _queries, .. _documents];
            _queries.Clear();
            _documents.Clear();
        }

        foreach (var pending in left) pending.Completion.TrySetException(new EmbeddingException("closed", "Search by meaning is closed."));
    }

    private async Task RunAsync(Slot slot, Pending next, CancellationToken token)
    {
        next.Registration.Dispose();
        try
        {
            if (next.Cancellation.IsCancellationRequested)
            {
                next.Completion.TrySetCanceled(next.Cancellation);
                return;
            }

            next.Completion.TrySetResult(await Task.Run(() => Run(slot, next.Texts), token).ConfigureAwait(false));
        }
        catch (EmbeddingException exception)
        {
            next.Completion.TrySetException(exception);
        }
        catch (OperationCanceledException)
        {
            next.Completion.TrySetException(new EmbeddingException("closed", "Search by meaning is closed."));
        }
        finally
        {
            lock (_gate)
            {
                slot.Busy = false;
                slot.FreeSince = Environment.TickCount64;
            }

            _wake.Release();
        }
    }

    // One request, on the worker of a slot or on one started for it. The protocol is one request at a time per worker.
    private float[][] Run(Slot slot, IReadOnlyList<string> texts)
    {
        var worker = EnsureWorker(slot);
        var correlation = Interlocked.Increment(ref _correlation);
        var content = JsonSerializer.SerializeToUtf8Bytes(new EmbeddingRequest(texts), EmbeddingJsonContext.Default.EmbeddingRequest);
        var answer = Exchange(slot, worker, EmbeddingFrameKind.Embed, correlation, content, RequestTimeout);
        return answer.Kind switch
        {
            EmbeddingFrameKind.Vectors => EmbeddingProtocol.ReadVectors(answer.Content),
            _ => throw new EmbeddingException("engine_failed", answer.Text),
        };
    }

    private IWorkerProcess EnsureWorker(Slot slot)
    {
        EmbeddingWorkerLoad load;
        lock (_gate)
        {
            if (slot.Worker is { } running) return running;
            load = _load;
        }

        IWorkerProcess worker;
        try { worker = _start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new EmbeddingException("worker_died", $"The embedding worker did not start: {exception.Message}");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(load, EmbeddingJsonContext.Default.EmbeddingWorkerLoad);
        WorkerFrame answer;
        try { answer = Exchange(slot, worker, EmbeddingFrameKind.Load, 0, content, LoadTimeout); }
        catch
        {
            Stop(worker, graceful: false);
            throw;
        }

        if (answer.Kind != EmbeddingFrameKind.Loaded || !int.TryParse(answer.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions))
        {
            Stop(worker, graceful: false);
            throw new EmbeddingException("load_failed", answer.Kind == EmbeddingFrameKind.Error ? answer.Text : "The model did not load.");
        }

        Volatile.Write(ref _dimensions, dimensions);
        lock (_gate) (slot.Worker, slot.Threads) = (worker, load.Threads);
        return worker;
    }

    // Writes a frame and reads the answer, within a time. A worker that dies or does not answer is ended.
    private WorkerFrame Exchange(Slot slot, IWorkerProcess worker, byte kind, int correlation, byte[] content, TimeSpan timeout)
    {
        var reading = Task.Run(() =>
        {
            WorkerFrames.Write(worker.Input, kind, correlation, content);
            worker.Input.Flush();
            return WorkerFrames.Read(worker.Output);
        });
        bool done;
        try { done = reading.Wait(timeout); }
        catch (AggregateException exception) when (exception.InnerException is IOException or InvalidDataException or ObjectDisposedException)
        {
            Forget(slot, worker);
            throw new EmbeddingException("worker_died", "The embedding worker ended.");
        }

        if (!done)
        {
            Forget(slot, worker);
            throw new EmbeddingException("timeout", "The embedding worker did not answer in time.");
        }

        if (reading.Result is not { } frame)
        {
            Forget(slot, worker);
            throw new EmbeddingException("worker_died", "The embedding worker ended.");
        }

        return frame;
    }

    private void Forget(Slot slot, IWorkerProcess worker)
    {
        lock (_gate)
        {
            if (ReferenceEquals(slot.Worker, worker)) slot.Worker = null;
        }

        Stop(worker, graceful: false);
    }

    private static void Stop(IWorkerProcess worker, bool graceful)
    {
        try
        {
            if (graceful)
            {
                WorkerFrames.Write(worker.Input, EmbeddingFrameKind.Exit, 0, []);
                worker.Input.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (!graceful)
            {
                try { worker.Kill(); }
                catch (InvalidOperationException) { }
            }

            worker.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
        }

        _wake.Release();
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        // The requests under way are answered, then every worker is asked to exit.
        Task[] running;
        lock (_gate) running = [.. _running];
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception exception) when (exception is EmbeddingException or OperationCanceledException) { }
        List<IWorkerProcess> workers;
        lock (_gate)
        {
            workers = [.. _slots.Select(static slot => slot.Worker).OfType<IWorkerProcess>()];
            foreach (var slot in _slots) slot.Worker = null;
        }

        foreach (var worker in workers) Stop(worker, graceful: true);
        _lifetime.Dispose();
        _wake.Dispose();
    }

    // One place for a worker: the worker that runs in it, if any, and whether it answers a request now.
    private sealed class Slot
    {
        public IWorkerProcess? Worker { get; set; }
        public int Threads { get; set; }
        public bool Busy { get; set; }
        public long FreeSince { get; set; } = Environment.TickCount64;
    }

    private sealed class Pending(IReadOnlyList<string> texts, CancellationToken cancellation)
    {
        public IReadOnlyList<string> Texts { get; } = texts;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<float[][]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
}
