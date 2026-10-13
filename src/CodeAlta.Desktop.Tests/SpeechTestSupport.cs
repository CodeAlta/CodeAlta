using System.IO.Pipes;
using System.Text.Json;
using CodeAlta.Desktop.Speech;

namespace CodeAlta.Desktop.Tests;

/// <summary>A recognizer that says how much audio it was given, so a test knows what was decoded.</summary>
internal sealed class FakeSpeechRecognizer : ISpeechRecognizer
{
    public int Decodes { get; private set; }
    public bool Disposed { get; private set; }
    public string Decode(float[] samples)
    {
        Decodes++;
        return $" {samples.Length / 512} windows ";
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A voice detector that hears speech in a loud window and ends an utterance after two quiet ones.</summary>
internal sealed class FakeSpeechDetector : ISpeechVoiceDetector
{
    private readonly List<float> _utterance = [];
    private readonly Queue<float[]> _finished = new();
    private int _quiet;
    public int WindowSize => 512;
    public bool IsSpeech { get; private set; }
    public int Resets { get; private set; }

    public void Accept(float[] window)
    {
        Assert.AreEqual(WindowSize, window.Length);
        if (window.Any(static sample => Math.Abs(sample) > 0.1f))
        {
            IsSpeech = true;
            _quiet = 0;
            _utterance.AddRange(window);
        }
        else if (IsSpeech)
        {
            _utterance.AddRange(window);
            if (++_quiet >= 2) End();
        }
    }

    public bool TryTake(out float[] samples) => _finished.TryDequeue(out samples!);

    public void Flush()
    {
        if (IsSpeech) End();
    }

    public void Reset()
    {
        Resets++;
        _utterance.Clear();
        _finished.Clear();
        (IsSpeech, _quiet) = (false, 0);
    }

    public void Dispose() { }

    private void End()
    {
        _finished.Enqueue([.. _utterance]);
        _utterance.Clear();
        (IsSpeech, _quiet) = (false, 0);
    }
}

/// <summary>An engine that loads the fakes, or refuses a model named "broken".</summary>
internal sealed class FakeSpeechEngine : ISpeechEngine
{
    public List<SpeechWorkerLoad> Loads { get; } = [];
    public FakeSpeechRecognizer? Recognizer { get; private set; }

    public (ISpeechRecognizer Recognizer, ISpeechVoiceDetector Detector) Load(SpeechWorkerLoad load)
    {
        lock (Loads) Loads.Add(load);
        if (load.ModelDirectory.Contains("broken", StringComparison.Ordinal)) throw new InvalidOperationException("The engine did not load the model.");
        Recognizer = new();
        return (Recognizer, new FakeSpeechDetector());
    }
}

/// <summary>The real worker loop on a thread, joined to its host by two pipes: every frame crosses a real stream.</summary>
internal sealed class FakeSpeechWorkerProcess : ISpeechWorkerProcess
{
    private readonly AnonymousPipeServerStream _hostWrites = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _workerWrites = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _workerReads;
    private readonly AnonymousPipeClientStream _hostReads;
    private readonly Thread _thread;

    public FakeSpeechWorkerProcess(ISpeechEngine engine)
    {
        _workerReads = new(PipeDirection.In, _hostWrites.ClientSafePipeHandle);
        _hostReads = new(PipeDirection.In, _workerWrites.ClientSafePipeHandle);
        _thread = new(() =>
        {
            try { SpeechWorker.Run(_workerReads, _workerWrites, engine); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            finally { Close(_workerWrites); }
        }) { IsBackground = true };
        _thread.Start();
    }

    public Stream Input => _hostWrites;
    public Stream Output => _hostReads;
    public bool Killed { get; private set; }
    public bool Ended => !_thread.IsAlive;

    /// <summary>Ends the worker as an engine that closes its process does: without a word.</summary>
    public void Kill()
    {
        Killed = true;
        Close(_workerWrites);
        Close(_hostWrites);
    }

    public void Dispose()
    {
        Kill();
        _thread.Join(TimeSpan.FromSeconds(5));
        Close(_workerReads);
        Close(_hostReads);
    }

    private static void Close(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}

/// <summary>A clock a test moves: timers fire when it is advanced past them.</summary>
internal sealed class SpeechTestClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() => new(GetTimestamp() + new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, TimeSpan.Zero);
    public int PendingTimers { get { lock (_gate) return _timers.Count(static timer => timer.DueAt is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        Timer[] due;
        lock (_gate)
        {
            _ticks += by.Ticks;
            due = [.. _timers.Where(timer => timer.DueAt <= _ticks)];
            foreach (var timer in due) timer.DueAt = null;
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class Timer(SpeechTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt { get; set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
            return true;
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class SpeechTestSupport
{
    /// <summary>Audio the fake detector hears as speech (loud) or not, in 16-bit samples.</summary>
    public static byte[] Audio(int windows, bool loud)
    {
        var samples = new short[windows * 512];
        if (loud) Array.Fill(samples, (short)16000);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "codealta-speech-" + Guid.NewGuid().ToString("N"));

    /// <summary>Waits for something another thread does.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>Writes into a cache what a finished download leaves: the engine, the voice detector and one model.</summary>
    public static string InstallModel(string speechRoot, string runtimeIdentifier, string id, string source, string name = "Test model")
    {
        var engine = Path.Combine(speechRoot, "runtime", SpeechModelCatalog.EngineVersion, runtimeIdentifier);
        Directory.CreateDirectory(engine);
        foreach (var library in SpeechModelCatalog.FindEngine(runtimeIdentifier)!.Libraries) File.WriteAllText(Path.Combine(engine, library), "");
        File.WriteAllText(Path.Combine(engine, "engine.json"), "{}");
        var detector = Path.Combine(speechRoot, "vad");
        Directory.CreateDirectory(detector);
        File.WriteAllText(Path.Combine(detector, SpeechModelCatalog.VoiceDetector.Files[0].Name), "vad");
        File.WriteAllText(Path.Combine(detector, "vad.json"), "{}");
        var model = Path.Combine(speechRoot, "models", id);
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "encoder.onnx"), "12345");
        File.WriteAllText(Path.Combine(model, "tokens.txt"), "123");
        File.WriteAllBytes(Path.Combine(model, "model.json"), JsonSerializer.SerializeToUtf8Bytes(new SpeechModelManifest
        {
            Id = id, Name = name, Author = "tests", Languages = "english", License = "MIT", Source = source, Repository = "tests/model",
            Commit = new string('a', 40), Family = nameof(SpeechModelFamily.Whisper),
            Files = [new() { Name = "encoder.onnx", Role = "encoder", Size = 5 }, new() { Name = "tokens.txt", Role = "tokens", Size = 3 }],
        }, SpeechJsonContext.Default.SpeechModelManifest));
        return model;
    }
}
