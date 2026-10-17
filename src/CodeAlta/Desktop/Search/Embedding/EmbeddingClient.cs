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
/// The host's side of the embedding worker. The worker starts with the first request and loads the model; queries go
/// before the batches of the indexing, which wait at most one batch; the worker ends after a while without work and
/// starts again with the next request. A dead worker fails the request it had; the next request starts another.
/// </summary>
internal sealed class EmbeddingClient : IAsyncDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<IWorkerProcess> _start;
    private readonly EmbeddingWorkerLoad _load;
    private readonly TimeSpan _idle;
    private readonly Lock _gate = new();
    private readonly LinkedList<Pending> _queries = new();
    private readonly LinkedList<Pending> _documents = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private IWorkerProcess? _worker;
    private int _correlation;
    private int _dimensions;
    private bool _paused;

    /// <summary>Creates the client.</summary>
    /// <param name="start">Starts a worker.</param>
    /// <param name="load">What the worker loads.</param>
    /// <param name="idle">How long the worker stays without work; 10 minutes by default.</param>
    public EmbeddingClient(Func<IWorkerProcess> start, EmbeddingWorkerLoad load, TimeSpan? idle = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _idle = idle ?? TimeSpan.FromMinutes(10);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Gets the model the worker loads.</summary>
    public EmbeddingWorkerLoad Load => _load;

    /// <summary>Gets the width of the vectors, once the model was loaded; zero before.</summary>
    public int Dimensions => Volatile.Read(ref _dimensions);

    /// <summary>Gets whether a worker runs now.</summary>
    public bool Running { get { lock (_gate) return _worker is not null; } }

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
            Pending? next;
            lock (_gate)
            {
                next = _queries.First?.Value ?? (_paused ? null : _documents.First?.Value);
                if (next is not null) (_queries.Count > 0 ? _queries : _documents).RemoveFirst();
            }

            if (next is null)
            {
                bool woken;
                try { woken = await _wake.WaitAsync(Running ? _idle : Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                // Nothing to do for a while: the worker ends, and its memory is given back.
                if (!woken) StopWorker(graceful: true);
                continue;
            }

            next.Registration.Dispose();
            if (next.Cancellation.IsCancellationRequested)
            {
                next.Completion.TrySetCanceled(next.Cancellation);
                continue;
            }

            try
            {
                next.Completion.TrySetResult(await Task.Run(() => Run(next.Texts), token).ConfigureAwait(false));
            }
            catch (EmbeddingException exception)
            {
                next.Completion.TrySetException(exception);
            }
            catch (OperationCanceledException)
            {
                next.Completion.TrySetException(new EmbeddingException("closed", "Search by meaning is closed."));
            }
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

    // One request, on the worker that runs or on one started for it. The protocol is one request at a time.
    private float[][] Run(IReadOnlyList<string> texts)
    {
        var worker = EnsureWorker();
        var correlation = Interlocked.Increment(ref _correlation);
        var content = JsonSerializer.SerializeToUtf8Bytes(new EmbeddingRequest(texts), EmbeddingJsonContext.Default.EmbeddingRequest);
        var answer = Exchange(worker, EmbeddingFrameKind.Embed, correlation, content, RequestTimeout);
        return answer.Kind switch
        {
            EmbeddingFrameKind.Vectors => EmbeddingProtocol.ReadVectors(answer.Content),
            _ => throw new EmbeddingException("engine_failed", answer.Text),
        };
    }

    private IWorkerProcess EnsureWorker()
    {
        lock (_gate)
        {
            if (_worker is not null) return _worker;
        }

        IWorkerProcess worker;
        try { worker = _start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new EmbeddingException("worker_died", $"The embedding worker did not start: {exception.Message}");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(_load, EmbeddingJsonContext.Default.EmbeddingWorkerLoad);
        WorkerFrame answer;
        try { answer = Exchange(worker, EmbeddingFrameKind.Load, 0, content, LoadTimeout); }
        catch
        {
            worker.Kill();
            worker.Dispose();
            throw;
        }

        if (answer.Kind != EmbeddingFrameKind.Loaded || !int.TryParse(answer.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions))
        {
            worker.Kill();
            worker.Dispose();
            throw new EmbeddingException("load_failed", answer.Kind == EmbeddingFrameKind.Error ? answer.Text : "The model did not load.");
        }

        Volatile.Write(ref _dimensions, dimensions);
        lock (_gate) _worker = worker;
        return worker;
    }

    // Writes a frame and reads the answer, within a time. A worker that dies or does not answer is ended.
    private WorkerFrame Exchange(IWorkerProcess worker, byte kind, int correlation, byte[] content, TimeSpan timeout)
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
            Forget(worker);
            throw new EmbeddingException("worker_died", "The embedding worker ended.");
        }

        if (!done)
        {
            Forget(worker);
            throw new EmbeddingException("timeout", "The embedding worker did not answer in time.");
        }

        if (reading.Result is not { } frame)
        {
            Forget(worker);
            throw new EmbeddingException("worker_died", "The embedding worker ended.");
        }

        return frame;
    }

    private void Forget(IWorkerProcess worker)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_worker, worker)) _worker = null;
        }

        try { worker.Kill(); }
        catch (InvalidOperationException) { }
        worker.Dispose();
    }

    private void StopWorker(bool graceful)
    {
        IWorkerProcess? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }

        if (worker is null) return;
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
        StopWorker(graceful: true);
        _lifetime.Dispose();
        _wake.Dispose();
    }

    private sealed class Pending(IReadOnlyList<string> texts, CancellationToken cancellation)
    {
        public IReadOnlyList<string> Texts { get; } = texts;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<float[][]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
}
