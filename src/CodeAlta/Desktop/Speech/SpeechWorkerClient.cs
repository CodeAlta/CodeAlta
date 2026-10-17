using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Speech;

/// <summary>A running speech worker, as its host sees it.</summary>
internal interface ISpeechWorkerProcess : IDisposable
{
    /// <summary>Gets the stream the host writes frames to.</summary>
    Stream Input { get; }

    /// <summary>Gets the stream the host reads frames from. It ends when the worker does.</summary>
    Stream Output { get; }

    /// <summary>Ends the worker now.</summary>
    void Kill();
}

/// <summary>What a dictation reads as so far.</summary>
/// <param name="State"><c>loading</c> (the model), <c>listening</c>, <c>finishing</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="FinalText">The finished utterances, in order.</param>
/// <param name="PartialText">The utterance being spoken, which can still change.</param>
/// <param name="Error">For <c>failed</c>: <c>load_failed</c>, <c>engine_failed</c> or <c>timeout</c>.</param>
internal sealed record SpeechTranscriptSnapshot(string State, string FinalText, string PartialText, string? Error)
{
    /// <summary>Gets whether the dictation is over.</summary>
    public bool IsOver => State is "done" or "failed";
}

/// <summary>One dictation: the audio given to the worker in order, and the text it gave back.</summary>
internal sealed class SpeechDictation
{
    private const int MaximumWaitingChunks = 256;
    private readonly Lock _gate = new();
    private readonly List<Action<SpeechTranscriptSnapshot>> _observers = [];
    private readonly SortedDictionary<int, byte[]> _waiting = [];
    private readonly TaskCompletionSource<SpeechTranscriptSnapshot> _over = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _final = new();
    private SpeechTranscriptSnapshot _snapshot;
    private int _next;

    internal SpeechDictation(int number, string state, string? error)
    {
        Number = number;
        _snapshot = new(state, "", "", error);
        if (_snapshot.IsOver) _over.TrySetResult(_snapshot);
    }

    /// <summary>Gets the id the page names the dictation by.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the number the worker knows the dictation by.</summary>
    public int Number { get; }

    /// <summary>Gets the latest state.</summary>
    public SpeechTranscriptSnapshot Current { get { lock (_gate) return _snapshot; } }

    /// <summary>Gets the state the dictation ends with.</summary>
    public Task<SpeechTranscriptSnapshot> Over => _over.Task;

    /// <summary>Follows the dictation. The observer gets the current state at once, then every change.</summary>
    public IDisposable Subscribe(Action<SpeechTranscriptSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
            observer(_snapshot);
        }

        return new Subscription(this, observer);
    }

    /// <summary>
    /// Takes a chunk of audio and gives <paramref name="write"/> every chunk that is next in order. Calls of
    /// the page run concurrently, so a chunk can arrive before the one it follows.
    /// </summary>
    internal bool Accept(int sequence, byte[] chunk, Action<byte[]> write)
    {
        lock (_gate)
        {
            if (_snapshot.State is not ("loading" or "listening") || sequence < _next) return false;
            _waiting[sequence] = chunk;
            // A chunk that never comes does not hold the rest for ever.
            if (_waiting.Count > MaximumWaitingChunks) _next = _waiting.Keys.First();
            while (_waiting.Remove(_next, out var ready))
            {
                write(ready);
                _next++;
            }

            return true;
        }
    }

    internal void Update(Func<SpeechTranscriptSnapshot, SpeechTranscriptSnapshot> change)
    {
        lock (_gate)
        {
            if (_snapshot.IsOver) return;
            var next = change(_snapshot);
            if (next == _snapshot) return;
            _snapshot = next;
            foreach (var observer in _observers) observer(next);
            if (next.IsOver) _over.TrySetResult(next);
        }
    }

    internal void AddFinal(string text)
        => Update(snapshot =>
        {
            if (_final.Length > 0) _final.Append(' ');
            _final.Append(text);
            return snapshot with { FinalText = _final.ToString(), PartialText = "" };
        });

    private sealed class Subscription(SpeechDictation owner, Action<SpeechTranscriptSnapshot> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._observers.Remove(observer);
        }
    }
}

/// <summary>
/// The host side of the speech worker: starts the process when a dictation needs it, keeps the model loaded
/// between dictations, and ends the process when it has been idle, when another model is asked for and when
/// the application closes. A worker that dies ends the dictation with an error; the next one starts another.
/// </summary>
internal sealed class SpeechWorkerClient
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<string, ISpeechWorkerProcess> _start;
    private readonly TimeProvider _time;
    private readonly TimeSpan _idle;
    private readonly Lock _gate = new();
    private readonly Lock _writing = new();
    private Worker? _worker;
    private SpeechDictation? _dictation;
    private ITimer? _idleTimer;
    private int _dictations;
    private bool _closed;

    /// <summary>Creates the client.</summary>
    /// <param name="start">Starts a worker in a working folder.</param>
    /// <param name="time">The clock of the idle time.</param>
    /// <param name="idle">How long a worker stays without a dictation.</param>
    public SpeechWorkerClient(Func<string, ISpeechWorkerProcess> start, TimeProvider time, TimeSpan idle)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(time);
        _start = start;
        _time = time;
        _idle = idle;
    }

    /// <summary>Gets the folder of the model the running worker was given, or null.</summary>
    public string? ModelDirectory { get { lock (_gate) return _worker?.Key; } }

    /// <summary>Gets a dictation by its id: the running one, or the last one.</summary>
    public SpeechDictation? Find(string? id)
    {
        lock (_gate) return _dictation is { } dictation && dictation.Id == id ? dictation : null;
    }

    /// <summary>Starts a dictation with a model. A dictation still running ends first.</summary>
    public SpeechDictation Start(SpeechWorkerLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            if (_dictation is { } previous && !previous.Current.IsOver)
            {
                Send(_worker, SpeechFrameKind.Cancel, previous.Number, []);
                previous.Update(static snapshot => snapshot with { State = "done", PartialText = "" });
            }

            var worker = _closed ? null : EnsureLocked(load);
            // The model is loaded, is loading, or was refused when this worker was given it.
            var failure = worker is null ? "engine_failed" : worker.Loaded.Task.IsCompleted ? worker.Loaded.Task.Result : null;
            var dictation = new SpeechDictation(++_dictations, failure is not null ? "failed" : worker!.Loaded.Task.IsCompleted ? "listening" : "loading", failure);
            _dictation = dictation;
            return dictation;
        }
    }

    /// <summary>Gives a chunk of audio to a dictation. False when the dictation is unknown or over.</summary>
    public bool Append(string id, int sequence, byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Worker? worker;
        SpeechDictation? dictation;
        lock (_gate) (worker, dictation) = (_worker, _dictation);
        if (worker is null || dictation is null || dictation.Id != id) return false;
        return dictation.Accept(sequence, chunk, ready => Send(worker, SpeechFrameKind.Audio, dictation.Number, ready));
    }

    /// <summary>Ends a dictation and gives its text, with what was still being spoken unless it is canceled.</summary>
    public async Task<SpeechTranscriptSnapshot?> StopAsync(string id, bool cancel, CancellationToken cancellationToken)
    {
        Worker? worker;
        SpeechDictation? dictation;
        lock (_gate) (worker, dictation) = (_worker, _dictation);
        if (dictation is null || dictation.Id != id) return null;
        if (!dictation.Current.IsOver)
        {
            dictation.Update(static snapshot => snapshot with { State = "finishing" });
            Send(worker, cancel ? SpeechFrameKind.Cancel : SpeechFrameKind.Finish, dictation.Number, []);
            try
            {
                await dictation.Over.WaitAsync(FinishTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                dictation.Update(static snapshot => snapshot with { State = "failed", Error = "timeout" });
            }
        }

        lock (_gate)
        {
            if (!_closed && ReferenceEquals(_dictation, dictation) && _worker is not null)
            {
                _idleTimer?.Dispose();
                _idleTimer = _time.CreateTimer(_ => _ = UnloadAsync(), null, _idle, Timeout.InfiniteTimeSpan);
            }
        }

        return dictation.Current;
    }

    /// <summary>Loads a model in the worker to know that the engine takes it. Null when it does, else an error code.</summary>
    public async Task<string?> ProbeAsync(SpeechWorkerLoad load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        Worker? worker;
        lock (_gate)
        {
            if (_dictation is { } dictation && !dictation.Current.IsOver) return null; // The worker is in use: this is not the time to change its model.
            worker = _closed ? null : EnsureLocked(load);
        }

        if (worker is null) return "engine_failed";
        try
        {
            return await worker.Loaded.Task.WaitAsync(LoadTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "load_failed";
        }
        finally
        {
            lock (_gate)
            {
                if (!_closed && ReferenceEquals(_worker, worker) && _idleTimer is null && (_dictation is null || _dictation.Current.IsOver))
                    _idleTimer = _time.CreateTimer(_ => _ = UnloadAsync(), null, _idle, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Ends the worker, and the dictation with it. The memory of the model goes back to the system.</summary>
    public async Task UnloadAsync()
    {
        Worker? worker;
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            worker = _worker;
            _worker = null;
            _dictation?.Update(static snapshot => snapshot with { State = "done", PartialText = "" });
        }

        if (worker is null) return;
        Send(worker, SpeechFrameKind.Exit, 0, []);
        try
        {
            await worker.Reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        worker.Process.Kill();
        await worker.Reading.ConfigureAwait(false);
        worker.Process.Dispose();
    }

    /// <summary>Ends the worker for good.</summary>
    public Task CloseAsync()
    {
        lock (_gate) _closed = true;
        return UnloadAsync();
    }

    private Worker? EnsureLocked(SpeechWorkerLoad load)
    {
        if (_worker is { } current && current.Key == load.ModelDirectory && !current.Reading.IsCompleted) return current;
        if (_worker is { } stale)
        {
            // Another model, or a worker that died: this one ends on its own once its input closes.
            Send(stale, SpeechFrameKind.Exit, 0, []);
            _ = Task.Run(async () =>
            {
                try { await stale.Reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
                stale.Process.Kill();
                stale.Process.Dispose();
            });
            _worker = null;
        }

        ISpeechWorkerProcess process;
        try { process = _start(load.ModelDirectory); }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SpeechLog.Warn($"The speech worker did not start: {exception.Message}");
            return null;
        }

        var worker = new Worker(process, load.ModelDirectory);
        worker.Reading = Task.Run(() => Read(worker));
        _worker = worker;
        Send(worker, SpeechFrameKind.Load, 0, JsonSerializer.SerializeToUtf8Bytes(load, SpeechJsonContext.Default.SpeechWorkerLoad));
        return worker;
    }

    private void Send(Worker? worker, SpeechFrameKind kind, int dictation, byte[] content)
    {
        if (worker is null) return;
        try
        {
            lock (_writing) SpeechWorkerProtocol.Write(worker.Process.Input, kind, dictation, content);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The worker is gone: its reader says so to whoever waits.
        }
    }

    private void Read(Worker worker)
    {
        try
        {
            while (SpeechWorkerProtocol.Read(worker.Process.Output) is { } frame)
            {
                SpeechDictation? dictation;
                lock (_gate) dictation = ReferenceEquals(_worker, worker) && _dictation is { } current && current.Number == frame.Dictation ? current : null;
                switch (frame.Kind)
                {
                    case SpeechFrameKind.Loaded:
                        worker.Loaded.TrySetResult(null);
                        lock (_gate) dictation = ReferenceEquals(_worker, worker) ? _dictation : null;
                        dictation?.Update(static snapshot => snapshot.State == "loading" ? snapshot with { State = "listening" } : snapshot);
                        break;
                    case SpeechFrameKind.Partial:
                        var partial = frame.Text;
                        dictation?.Update(snapshot => snapshot with { PartialText = partial });
                        break;
                    case SpeechFrameKind.Final:
                        dictation?.AddFinal(frame.Text);
                        break;
                    case SpeechFrameKind.Done:
                        dictation?.Update(static snapshot => snapshot with { State = "done", PartialText = "" });
                        break;
                    case SpeechFrameKind.Error:
                        SpeechLog.Warn($"The speech worker reported: {frame.Text}");
                        if (frame.Dictation == 0)
                        {
                            worker.Loaded.TrySetResult("load_failed");
                            lock (_gate) dictation = ReferenceEquals(_worker, worker) ? _dictation : null;
                            dictation?.Update(static snapshot => snapshot with { State = "failed", Error = "load_failed" });
                        }
                        else
                        {
                            dictation?.Update(static snapshot => snapshot with { State = "failed", Error = "engine_failed" });
                        }

                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            SpeechLog.Warn($"The speech worker stream ended: {exception.Message}");
        }

        // The worker ended: by a request, or because the engine refused the model and closed its process.
        var loaded = worker.Loaded.Task.IsCompletedSuccessfully && worker.Loaded.Task.Result is null;
        worker.Loaded.TrySetResult("load_failed");
        lock (_gate)
        {
            if (!ReferenceEquals(_worker, worker)) return;
            _worker = null;
            _dictation?.Update(snapshot => snapshot with { State = "failed", Error = loaded ? "engine_failed" : "load_failed" });
        }
    }

    private sealed class Worker(ISpeechWorkerProcess process, string key)
    {
        public ISpeechWorkerProcess Process { get; } = process;
        public string Key { get; } = key;
        public TaskCompletionSource<string?> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reading { get; set; } = Task.CompletedTask;
    }
}

/// <summary>The speech worker as a process: this executable started with <see cref="SpeechWorker.Option"/>.</summary>
internal sealed class SpeechWorkerProcess : ISpeechWorkerProcess
{
    private readonly WorkerProcess _process;

    private SpeechWorkerProcess(WorkerProcess process) => _process = process;

    /// <inheritdoc />
    public Stream Input => _process.Input;

    /// <inheritdoc />
    public Stream Output => _process.Output;

    /// <summary>Starts the worker in a folder.</summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    /// <exception cref="InvalidOperationException">This process has no executable to start again.</exception>
    public static SpeechWorkerProcess Start(string workingDirectory)
        => Start(Environment.ProcessPath ?? throw new InvalidOperationException("The path of the application is unknown."), workingDirectory);

    /// <summary>Starts the worker of an executable in a folder.</summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    // The engine writes what it refuses on its error stream, which is all it says before it ends.
    internal static SpeechWorkerProcess Start(string executable, string workingDirectory)
        => new(WorkerProcess.Start(executable, SpeechWorker.Option, workingDirectory, static line => SpeechLog.Info($"speech worker: {line}")));

    /// <inheritdoc />
    public void Kill() => _process.Kill();

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
